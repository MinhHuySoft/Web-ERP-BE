using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using WebErp.Config;
using WebErp.HostSync;
using WebErp.Tests.Support;

namespace WebErp.Tests.HostSync;

/// <summary>Agent đồng bộ IIS chạy trên config.db thật (tạm) với IIS / netsh / win-acme / DNS / HTTPS giả.</summary>
public sealed class ReconcilerTests : IDisposable
{
    private const string Ip = "103.232.122.205";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weberp-hostsync-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _time = new(new DateTimeOffset(2026, 11, 17, 3, 0, 0, TimeSpan.Zero));
    private readonly ConfigWriter _writer;
    private readonly HostSyncStore _store;
    private readonly FakeIis _iis = new();
    private readonly FakeSsl _ssl = new();
    private readonly FakeCerts _certs = new();
    private readonly FakeAcme _acme;
    private readonly FakeDns _dns = new(Ip);
    private readonly FakeProbe _probe;
    private readonly Reconciler _rec;

    public ReconcilerTests()
    {
        var db = new ConfigDatabase(new WebErpPaths(_root));
        _writer = new ConfigWriter(db, new SecretProtector(new EphemeralDataProtectionProvider()), _time, "test");
        _writer.Initialize();
        _store = new HostSyncStore(db, _time);
        _iis.Site(1, "medtest", new IisBinding("http", "*:80:"), new IisBinding("https", "*:443:medtest.bms7.net", 1, "MEDTESTCERT"))
            .Site(9, "WebErp", new IisBinding("http", "127.0.0.1:8081:"))
            .Site(10, "WebErpAdmin", new IisBinding("http", "127.0.0.1:8082:"), new IisBinding("https", "*:443:quantri.finora.io.vn", 1, "ADMINCERT"));
        _ssl.Add("medtest.bms7.net:443", "MEDTESTCERT");
        _ssl.Add("quantri.finora.io.vn:443", "ADMINCERT");
        _acme = new FakeAcme(_iis, _ssl, _certs, _time);
        _probe = new FakeProbe(_ssl, _certs);
        _rec = new Reconciler(_store, _iis, _ssl, _acme, _certs, _dns, _probe, new HostSyncOptions { PublicIp = Ip }, _time, NullLogger<Reconciler>.Instance)
        {
            AgentVersion = "test",
        };
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Tenant(string key, params string[] hosts)
    {
        _writer.AddTenant(key, key);
        _writer.Update(key, key, "SY", null, null, true, hosts, null);
    }

    private Task<RunSummary> Run() => _rec.RunOnceAsync(CancellationToken.None);

    private ManagedHost? Row(string host) => _store.Get(host);

    private List<IisBinding> WebErp => _iis.Bindings("WebErp");

    /// <summary>Domain có sẵn, cấu hình tay (như erp / tinphong lúc bắt đầu).</summary>
    private void ManualHost(string host, string thumb, string renewalId)
    {
        WebErp.Add(new IisBinding("http", $"*:80:{host}"));
        WebErp.Add(new IisBinding("https", $"*:443:{host}", 1, thumb));
        _ssl.Add($"{host}:443", thumb);
        _certs.Add(thumb, _time.GetUtcNow().AddDays(60), host);
        _acme.Store.Add(new AcmeRenewal(renewalId, "[Manual] " + host, [host], [9], renewalId + ".renewal.json"));
    }

    [Fact]
    public async Task New_domain_gets_bindings_certificate_and_is_verified()
    {
        Tenant("TP", "tinphong.finora.io.vn");
        var s = await Run();

        Assert.Equal(1, s.Added);
        Assert.Equal(1, _iis.Commits);
        Assert.Contains(WebErp, b => b.Protocol == "http" && b.BindingInformation == "*:80:tinphong.finora.io.vn");
        Assert.Contains(WebErp, b => b.Protocol == "https" && b.BindingInformation == "*:443:tinphong.finora.io.vn" && b.SslFlags == 1);
        Assert.Equal(["tinphong.finora.io.vn"], _acme.Issued);
        Assert.Equal(["tinphong.finora.io.vn/erp/login"], _probe.Calls);
        var row = Row("tinphong.finora.io.vn")!;
        Assert.Equal(HostStates.Active, row.State);
        Assert.NotNull(row.CertThumbprint);
        Assert.Equal(_time.GetUtcNow().AddDays(90), row.CertExpires);
        Assert.Contains(_writer.ReadAudit(50), a => a.Actor == "hostsync" && a.Action == "hostsync.add" && a.Target == "tinphong.finora.io.vn");
        Assert.Equal("test", _store.Meta().AgentVersion);

        // Vòng sau: không đổi gì, không commit thêm, không gọi win-acme lại.
        await Run();
        Assert.Equal(1, _iis.Commits);
        Assert.Single(_acme.Issued);
    }

    [Fact]
    public async Task Several_new_domains_share_one_iis_commit()
    {
        Tenant("A", "a.finora.io.vn");
        Tenant("B", "b.finora.io.vn", "c.finora.io.vn");
        var s = await Run();
        Assert.Equal(3, s.Added);
        Assert.Equal(1, _iis.Commits);
    }

    [Fact]
    public async Task Wrong_dns_waits_without_calling_lets_encrypt()
    {
        Tenant("TP", "tinphong.finora.io.vn");
        _dns.Answers["tinphong.finora.io.vn"] = new DnsAnswer(["104.21.5.5"], []);
        var s = await Run();

        Assert.Equal(1, s.Pending);
        Assert.Equal(0, _iis.Commits);
        Assert.Empty(_acme.Issued);
        var row = Row("tinphong.finora.io.vn")!;
        Assert.Equal(HostStates.PendingDns, row.State);
        Assert.Contains("104.21.5.5", row.LastError);

        // Chưa tới giờ thử lại: không hỏi DNS nữa.
        await Run();
        Assert.Single(_dns.Queries);

        // Sửa DNS + hết thời gian chờ: chạy tiếp.
        _dns.Answers.Remove("tinphong.finora.io.vn");
        _time.Advance(TimeSpan.FromMinutes(2));
        await Run();
        Assert.Equal(HostStates.Active, Row("tinphong.finora.io.vn")!.State);
    }

    [Fact]
    public async Task Domain_on_another_site_is_reported_and_left_alone()
    {
        Tenant("TP", "quantri.finora.io.vn");
        var s = await Run();

        Assert.Equal(0, _iis.Commits);
        Assert.Empty(_acme.Issued);
        var row = Row("quantri.finora.io.vn")!;
        Assert.Equal(HostStates.Conflict, row.State);
        Assert.Contains("WebErpAdmin", row.LastError);
        Assert.Equal(1, s.Errors);

        // Bỏ domain: chỉ xóa dòng theo dõi, không đụng binding / ánh xạ SSL / chứng chỉ của site kia.
        _writer.Update("TP", "TP", "SY", null, null, true, [], null);
        await Run();
        Assert.Null(Row("quantri.finora.io.vn"));
        Assert.Contains(_iis.Bindings("WebErpAdmin"), b => b.Host == "quantri.finora.io.vn");
        Assert.NotNull(_ssl.Find("quantri.finora.io.vn"));
        Assert.Empty(_ssl.Deleted);
        Assert.Equal(0, _iis.Commits);
    }

    [Fact]
    public async Task Wacs_failure_is_recorded_and_retried_later()
    {
        Tenant("TP", "tinphong.finora.io.vn");
        _acme.FailExitCode = -1;
        await Run();

        var row = Row("tinphong.finora.io.vn")!;
        Assert.Equal(HostStates.Error, row.State);
        Assert.Contains("win-acme lỗi (mã -1)", row.LastError);
        Assert.DoesNotContain("user@example.com", row.LastError); // email bị che
        Assert.NotNull(row.NextAttemptAt);
        Assert.Contains(WebErp, b => b.Host == "tinphong.finora.io.vn"); // binding đã thêm, giữ nguyên

        // Chưa tới giờ: không gọi lại win-acme (tránh giới hạn Let's Encrypt).
        await Run();
        Assert.Single(_acme.Issued);

        // Quản trị viên bấm "Thử lại" sau khi sửa nguyên nhân.
        _acme.FailExitCode = null;
        Assert.True(_store.Retry("quan.tri", "tinphong.finora.io.vn"));
        await Run();
        Assert.Equal(HostStates.Active, Row("tinphong.finora.io.vn")!.State);
        Assert.Equal(1, _iis.Commits); // binding chỉ thêm một lần
    }

    [Fact]
    public async Task Failed_https_check_does_not_request_a_new_certificate_on_retry()
    {
        Tenant("TP", "tinphong.finora.io.vn");
        _probe.Status = 502;
        await Run();
        Assert.Equal(HostStates.Error, Row("tinphong.finora.io.vn")!.State);
        Assert.Contains("HTTP 502", Row("tinphong.finora.io.vn")!.LastError);

        _probe.Status = 200;
        _time.Advance(TimeSpan.FromMinutes(5));
        await Run();
        Assert.Equal(HostStates.Active, Row("tinphong.finora.io.vn")!.State);
        Assert.Single(_acme.Issued);
    }

    [Fact]
    public async Task Changing_domain_adds_new_and_removes_old_completely()
    {
        Tenant("TP", "old.finora.io.vn");
        await Run();
        var oldThumb = Row("old.finora.io.vn")!.CertThumbprint!;
        Assert.Single(_acme.Store);

        _writer.Update("TP", "TP", "SY", null, null, true, ["new.finora.io.vn"], null);
        var s = await Run();

        Assert.Equal(1, s.Added);
        Assert.Equal(1, s.Removed);
        Assert.Equal(2, _iis.Commits); // lần đầu + một lần cho cả thêm lẫn gỡ
        Assert.DoesNotContain(WebErp, b => b.Host == "old.finora.io.vn");
        Assert.Null(_ssl.Find("old.finora.io.vn"));
        Assert.Contains(oldThumb, _certs.Deleted);
        Assert.DoesNotContain(_acme.Store, r => r.Hosts.Contains("old.finora.io.vn"));
        Assert.Null(Row("old.finora.io.vn"));
        Assert.Equal(HostStates.Active, Row("new.finora.io.vn")!.State);
        Assert.Contains(_writer.ReadAudit(50), a => a.Action == "hostsync.remove" && a.Target == "old.finora.io.vn" && a.Detail!.Contains("renewal"));
        // Các site khác không bị đụng tới.
        Assert.NotNull(_ssl.Find("medtest.bms7.net"));
        Assert.NotNull(_ssl.Find("quantri.finora.io.vn"));
    }

    [Fact]
    public async Task Deleting_company_removes_its_domains()
    {
        ManualHost("erp.finora.io.vn", "ERPCERT", "rErp");
        Tenant("MC", "erp.finora.io.vn");
        Tenant("TP", "tinphong.finora.io.vn");
        await Run();
        Assert.True(Row("erp.finora.io.vn")!.Adopted);

        var hosts = _writer.DeleteTenant("TP");
        Assert.Equal(["tinphong.finora.io.vn"], hosts);
        await Run();

        Assert.Null(Row("tinphong.finora.io.vn"));
        Assert.DoesNotContain(WebErp, b => b.Host == "tinphong.finora.io.vn");
        Assert.Null(_ssl.Find("tinphong.finora.io.vn"));
        // Công ty còn lại không bị ảnh hưởng.
        Assert.Equal(HostStates.Active, Row("erp.finora.io.vn")!.State);
        Assert.NotNull(_ssl.Find("erp.finora.io.vn"));
        Assert.Contains(_acme.Store, r => r.Id == "rErp");
        Assert.Contains(WebErp, b => b.BindingInformation == "127.0.0.1:8081:");
    }

    [Fact]
    public async Task Removing_the_last_domain_waits_for_approval()
    {
        // Cấu hình mong muốn rỗng mà còn domain đang chạy: có thể là đọc nhầm cấu hình, không tự gỡ.
        Tenant("TP", "tinphong.finora.io.vn");
        await Run();
        _writer.Update("TP", "TP", "SY", null, null, true, [], null);
        await Run();
        Assert.Equal(HostStates.RemovalBlocked, Row("tinphong.finora.io.vn")!.State);
        Assert.Contains(WebErp, b => b.Host == "tinphong.finora.io.vn");

        _store.ApproveRemoval("quan.tri", ["tinphong.finora.io.vn"]);
        await Run();
        Assert.Null(Row("tinphong.finora.io.vn"));
        Assert.DoesNotContain(WebErp, b => b.Host == "tinphong.finora.io.vn");
    }

    [Fact]
    public async Task Disabled_company_keeps_bindings_and_certificate()
    {
        Tenant("TP", "tinphong.finora.io.vn");
        await Run();
        _writer.SetEnabled("TP", false);
        _probe.Status = 503;
        await Run();

        Assert.Equal(HostStates.Active, Row("tinphong.finora.io.vn")!.State);
        Assert.Contains(WebErp, b => b.Host == "tinphong.finora.io.vn");
        Assert.Empty(_ssl.Deleted);
        Assert.Equal(1, _iis.Commits);
    }

    [Fact]
    public async Task Host_not_in_managed_table_is_never_removed()
    {
        // Binding cấu hình tay, không thuộc công ty nào, không có trong managed_host.
        ManualHost("manual.finora.io.vn", "MANUALCERT", "rManual");
        Tenant("TP", "tinphong.finora.io.vn");
        Tenant("K", "keep.finora.io.vn");
        await Run();
        _writer.Update("TP", "TP", "SY", null, null, true, [], null);
        await Run();

        Assert.Null(Row("tinphong.finora.io.vn"));
        Assert.DoesNotContain(WebErp, b => b.Host == "tinphong.finora.io.vn");

        Assert.Contains(WebErp, b => b.Host == "manual.finora.io.vn");
        Assert.NotNull(_ssl.Find("manual.finora.io.vn"));
        Assert.Contains(_acme.Store, r => r.Id == "rManual");
        Assert.DoesNotContain("MANUALCERT", _certs.Deleted);
        Assert.Null(Row("manual.finora.io.vn"));
    }

    [Fact]
    public async Task Existing_manual_domain_is_adopted_without_new_certificate()
    {
        ManualHost("erp.finora.io.vn", "ERPCERT", "rErp");
        Tenant("MC", "erp.finora.io.vn");
        await Run();

        var row = Row("erp.finora.io.vn")!;
        Assert.Equal(HostStates.Active, row.State);
        Assert.True(row.Adopted);
        Assert.Equal("ERPCERT", row.CertThumbprint);
        Assert.Empty(_acme.Issued);
        Assert.Equal(0, _iis.Commits);
    }

    [Fact]
    public void Adopt_command_requires_binding_and_certificate()
    {
        Assert.Contains("chưa có binding https", _rec.Adopt("erp.finora.io.vn"));
        ManualHost("erp.finora.io.vn", "ERPCERT", "rErp");
        Assert.Null(_rec.Adopt("ERP.finora.io.vn"));
        Assert.Null(_rec.Adopt("erp.finora.io.vn")); // chạy lại không sao
        Assert.True(Row("erp.finora.io.vn")!.Adopted);
    }

    [Fact]
    public async Task Removing_many_domains_waits_for_approval()
    {
        Tenant("A", "a.finora.io.vn", "b.finora.io.vn", "c.finora.io.vn");
        Tenant("K", "keep.finora.io.vn");
        await Run();
        Assert.Equal(1, _iis.Commits);

        _writer.DeleteTenant("A");
        var s = await Run();
        Assert.Equal(3, s.Blocked);
        Assert.Equal(1, _iis.Commits);
        Assert.All(new[] { "a", "b", "c" }, h => Assert.Equal(HostStates.RemovalBlocked, Row(h + ".finora.io.vn")!.State));
        Assert.Contains(WebErp, b => b.Host == "a.finora.io.vn");

        Assert.Equal(3, _store.ApproveRemoval("quan.tri", ["a.finora.io.vn", "b.finora.io.vn", "c.finora.io.vn"]));
        s = await Run();
        Assert.Equal(3, s.Removed);
        Assert.Equal(2, _iis.Commits);
        Assert.DoesNotContain(WebErp, b => b.Host.EndsWith(".finora.io.vn", StringComparison.Ordinal) && b.Host != "keep.finora.io.vn");
        Assert.Equal(HostStates.Active, Row("keep.finora.io.vn")!.State);
    }

    [Fact]
    public async Task Shared_certificate_and_multi_name_renewal_are_kept()
    {
        Tenant("TP", "tinphong.finora.io.vn");
        Tenant("K", "keep.finora.io.vn");
        await Run();
        // Một renewal khác có nhiều tên (gồm cả domain này) và chứng chỉ SAN dùng chung: không được hủy / xóa.
        _acme.Store.Add(new AcmeRenewal("rMulti", "[Manual] x", ["tinphong.finora.io.vn", "www.tinphong.finora.io.vn"], [9], "m.renewal.json"));
        _acme.Store.Add(new AcmeRenewal("rApi", "[Manual] api-tinphong.finora.io.vn", ["api-tinphong.finora.io.vn"], [9], "a.renewal.json"));
        _certs.Add("SANCERT", _time.GetUtcNow().AddDays(30), "tinphong.finora.io.vn", "www.tinphong.finora.io.vn");

        _writer.Update("TP", "TP", "SY", null, null, true, [], null);
        await Run();

        Assert.Null(Row("tinphong.finora.io.vn"));
        Assert.Contains(_acme.Store, r => r.Id == "rMulti");
        Assert.Contains(_acme.Store, r => r.Id == "rApi");
        Assert.True(_certs.Store.ContainsKey("SANCERT"));
    }

    [Fact]
    public async Task Iis_error_marks_domains_and_keeps_going_next_time()
    {
        Tenant("TP", "tinphong.finora.io.vn");
        _iis.FailWith = new InvalidOperationException("Cannot write configuration file");
        var s = await Run();
        Assert.False(s.Committed);
        Assert.Contains("Không sửa được binding IIS", Row("tinphong.finora.io.vn")!.LastError);
        Assert.Empty(_acme.Issued);

        _iis.FailWith = null;
        _store.Retry("quan.tri", "tinphong.finora.io.vn");
        await Run();
        Assert.Equal(HostStates.Active, Row("tinphong.finora.io.vn")!.State);
    }

    [Fact]
    public async Task Expiring_certificate_is_flagged_but_kept_active()
    {
        ManualHost("erp.finora.io.vn", "ERPCERT", "rErp");
        _certs.Add("ERPCERT", _time.GetUtcNow().AddDays(5), "erp.finora.io.vn");
        Tenant("MC", "erp.finora.io.vn");
        await Run();
        var row = Row("erp.finora.io.vn")!;
        Assert.Equal(HostStates.Active, row.State);
        Assert.Contains("win-acme renew", row.LastError);
    }

    [Fact]
    public async Task Agent_writes_do_not_bump_config_version()
    {
        Tenant("TP", "tinphong.finora.io.vn");
        var before = _store.ConfigVersion();
        await Run();
        _store.RequestSync("quan.tri");
        Assert.Equal(before, _store.ConfigVersion());
        Assert.Equal(before, _store.Meta().AppliedVersion);
    }
}
