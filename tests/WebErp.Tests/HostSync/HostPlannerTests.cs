using WebErp.Config;
using WebErp.HostSync;
using WebErp.HostSync.Platform;

namespace WebErp.Tests.HostSync;

public sealed class HostPlannerTests
{
    private static readonly HostSyncOptions Options = new() { SiteName = "WebErp", PublicIp = "103.232.122.205" };

    [Theory]
    [InlineData("erp.finora.io.vn", true)]
    [InlineData("tinphong.finora.io.vn", true)]
    [InlineData("a-b.c1.vn", true)]
    [InlineData("ERP.finora.io.vn", false)]       // phải chuẩn hóa chữ thường trước
    [InlineData("*.finora.io.vn", false)]         // wildcard
    [InlineData("erp.finora.io.vn:443", false)]   // cổng
    [InlineData("erp.finora.io.vn.", false)]
    [InlineData("localhost", false)]
    [InlineData("103.232.122.205", false)]
    [InlineData("-bad.vn", false)]
    [InlineData("bad-.vn", false)]
    [InlineData("a..vn", false)]
    [InlineData("erp finora.vn", false)]
    [InlineData("erp.finora.vn;calc", false)]
    [InlineData("erp.finora.vn\" --x", false)]
    [InlineData("app.localhost", false)]
    [InlineData("", false)]
    public void Host_validation_is_strict(string host, bool ok) => Assert.Equal(ok, HostNames.IsSyncable(host));

    [Fact]
    public void Host_longer_than_253_is_rejected()
    {
        var label = new string('a', 60);
        var host = string.Join('.', Enumerable.Repeat(label, 5)) + ".vn"; // 307 ký tự
        Assert.False(HostNames.IsSyncable(host));
    }

    private static IisSnapshot Iis(params IisSite[] sites) => new(sites);

    private static IisSite WebErp(params string[] hosts) => new(9, "WebErp",
        [new IisBinding("http", "127.0.0.1:8081:"), .. hosts.SelectMany(h => new[] { new IisBinding("http", $"*:80:{h}"), new IisBinding("https", $"*:443:{h}", 1) })]);

    private static DesiredHost D(string host, bool enabled = true) => new(host, ["T"], enabled);

    [Fact]
    public void Plan_computes_add_remove_and_ignores_unmanaged()
    {
        var plan = HostPlanner.Plan(
            [D("new.finora.io.vn"), D("erp.finora.io.vn")],
            [new ManagedHost("erp.finora.io.vn", HostStates.Active), new ManagedHost("old.finora.io.vn", HostStates.Active)],
            Iis(WebErp("erp.finora.io.vn", "old.finora.io.vn", "manual.finora.io.vn")), Options);

        Assert.Equal(["new.finora.io.vn", "erp.finora.io.vn"], plan.Ensure.Select(e => e.Host));
        Assert.False(plan.Ensure[0].HasHttp);
        Assert.True(plan.Ensure[1].HasHttps);
        Assert.Equal("old.finora.io.vn", Assert.Single(plan.Remove).Host);
        // manual.* có binding nhưng không mong muốn, không thuộc managed_host: không bao giờ bị gỡ.
        Assert.DoesNotContain(plan.Remove, m => m.Host == "manual.finora.io.vn");
        Assert.Empty(plan.BlockedRemovals);
    }

    [Fact]
    public void Plan_reports_conflict_with_other_site()
    {
        var other = new IisSite(1, "medtest", [new IisBinding("http", "*:80:"), new IisBinding("https", "*:443:quantri.finora.io.vn", 1)]);
        var plan = HostPlanner.Plan([D("quantri.finora.io.vn")], [], Iis(WebErp(), other), Options);
        Assert.Empty(plan.Ensure);
        Assert.Equal(("quantri.finora.io.vn", "medtest"), Assert.Single(plan.Conflicts));
    }

    [Fact]
    public void Plan_skips_non_public_hosts()
    {
        var plan = HostPlanner.Plan([D("localhost"), D("dev")], [], Iis(WebErp()), Options);
        Assert.Empty(plan.Ensure);
        Assert.Equal(["localhost", "dev"], plan.Skipped);
    }

    [Fact]
    public void Disabled_company_hosts_are_still_desired()
    {
        var plan = HostPlanner.Plan([D("tinphong.finora.io.vn", enabled: false)], [new ManagedHost("tinphong.finora.io.vn", HostStates.Active)],
            Iis(WebErp("tinphong.finora.io.vn")), Options);
        Assert.Empty(plan.Remove);
        Assert.False(Assert.Single(plan.Ensure).AnyEnabled);
    }

    [Fact]
    public void Mass_removal_is_blocked_until_approved()
    {
        ManagedHost M(string h, bool approved = false) => new(h, HostStates.Active, RemoveApproved: approved);
        var three = new[] { M("a.finora.io.vn"), M("b.finora.io.vn"), M("c.finora.io.vn") };
        var plan = HostPlanner.Plan([D("keep.finora.io.vn")], three, Iis(WebErp()), Options);
        Assert.Empty(plan.Remove);
        Assert.Equal(3, plan.BlockedRemovals.Count);

        // Cấu hình rỗng (vd đọc nhầm) mà có domain đang chạy: luôn chặn, kể cả chỉ 1 domain.
        var empty = HostPlanner.Plan([], [M("a.finora.io.vn")], Iis(WebErp()), Options);
        Assert.Empty(empty.Remove);
        Assert.Single(empty.BlockedRemovals);

        var approved = HostPlanner.Plan([D("keep.finora.io.vn")], [M("a.finora.io.vn", true), M("b.finora.io.vn"), M("c.finora.io.vn")], Iis(WebErp()), Options);
        Assert.Equal("a.finora.io.vn", Assert.Single(approved.Remove).Host);
        Assert.Equal(2, approved.BlockedRemovals.Count);

        var two = HostPlanner.Plan([D("keep.finora.io.vn")], [M("a.finora.io.vn"), M("b.finora.io.vn")], Iis(WebErp()), Options);
        Assert.Equal(2, two.Remove.Count);
    }

    [Fact]
    public void Ignored_hosts_are_never_touched()
    {
        var o = new HostSyncOptions { IgnoreHosts = ["erp.finora.io.vn"] };
        var plan = HostPlanner.Plan([], [new ManagedHost("erp.finora.io.vn", HostStates.Active)], Iis(WebErp("erp.finora.io.vn")), o);
        Assert.Empty(plan.Remove);
        Assert.Empty(plan.BlockedRemovals);
    }

    [Fact]
    public void Dns_must_point_exactly_to_vps()
    {
        const string ip = "103.232.122.205";
        Assert.Null(HostPlanner.CheckDns(new DnsAnswer([ip], []), ip));
        Assert.Contains("Chưa có bản ghi A", HostPlanner.CheckDns(new DnsAnswer([], []), ip));
        Assert.Contains("104.21.1.1", HostPlanner.CheckDns(new DnsAnswer(["104.21.1.1"], []), ip)); // proxy Cloudflare
        Assert.NotNull(HostPlanner.CheckDns(new DnsAnswer([ip, "1.2.3.4"], []), ip));
        Assert.Contains("AAAA", HostPlanner.CheckDns(new DnsAnswer([ip], ["2606:4700::1"]), ip));
        Assert.Equal("NXDOMAIN", HostPlanner.CheckDns(new DnsAnswer([], [], "NXDOMAIN"), ip));
    }

    [Fact]
    public void Renewal_match_compares_whole_names_and_site()
    {
        AcmeRenewal R(long site, params string[] hosts) => new("id", null, hosts, [site], "f");
        Assert.True(HostPlanner.IsRenewalFor(R(9, "medcom.bms79.com"), "medcom.bms79.com", 9));
        Assert.False(HostPlanner.IsRenewalFor(R(9, "api-medcom.bms79.com"), "medcom.bms79.com", 9));
        Assert.False(HostPlanner.IsRenewalFor(R(9, "medcom.bms79.com.vn"), "medcom.bms79.com", 9));
        Assert.False(HostPlanner.IsRenewalFor(R(9, "medcom.bms79.com", "www.medcom.bms79.com"), "medcom.bms79.com", 9));
        Assert.False(HostPlanner.IsRenewalFor(R(1, "medcom.bms79.com"), "medcom.bms79.com", 9));
        Assert.False(HostPlanner.IsRenewalFor(new AcmeRenewal("id", "[Manual] medcom.bms79.com", [], [9], "f"), "medcom.bms79.com", 9));
        Assert.False(HostPlanner.IsRenewalFor(new AcmeRenewal("id", null, ["medcom.bms79.com"], [], "f"), "medcom.bms79.com", 9));
    }

    [Fact]
    public void Backoff_doubles_up_to_cap()
    {
        var first = TimeSpan.FromMinutes(1);
        var max = TimeSpan.FromMinutes(30);
        Assert.Equal(first, HostPlanner.Backoff(1, first, max));
        Assert.Equal(TimeSpan.FromMinutes(2), HostPlanner.Backoff(2, first, max));
        Assert.Equal(TimeSpan.FromMinutes(16), HostPlanner.Backoff(5, first, max));
        Assert.Equal(max, HostPlanner.Backoff(50, first, max));
    }

    [Fact]
    public void Netsh_output_is_parsed()
    {
        const string output = """

            SSL Certificate bindings:
            -------------------------

                Hostname:port                : erp.finora.io.vn:443
                Certificate Hash             : 0a1b2c3d4e5f
                Application ID               : {4dc3e181-e14b-4a21-b022-59fc669b0914}
                Certificate Store Name       : WebHosting
                Verify Client Certificate Revocation : Enabled

                IP:port                      : 0.0.0.0:443
                Certificate Hash             : ffee
                Certificate Store Name       : MY

                Central Certificate Store    : 443
                Certificate Hash             : (null)

            """;
        var list = NetshSslBindings.Parse(output);
        Assert.Equal(2, list.Count);
        Assert.Equal("erp.finora.io.vn", list[0].Host);
        Assert.Equal("0A1B2C3D4E5F", list[0].CertificateHash);
        Assert.Equal("WebHosting", list[0].StoreName);
        Assert.Null(list[1].Host);
    }

    [Fact]
    public void Wacs_renewal_file_is_parsed()
    {
        const string json = """
            {
              "$type": "PKISharp.WACS.DomainObjects.Renewal, wacs",
              "Id": "AbC123xyz",
              "LastFriendlyName": "[Manual] tinphong.finora.io.vn",
              "TargetPluginOptions": { "$type": "x", "CommonName": "TinPhong.finora.io.vn", "AlternativeNames": ["tinphong.finora.io.vn"] },
              "InstallationPluginOptions": [ { "$type": "PKISharp.WACS.Plugins.InstallationPlugins.IISOptions, wacs", "SiteId": 9 } ]
            }
            """;
        var r = WacsClient.ParseRenewal(json, "f.renewal.json")!;
        Assert.Equal("AbC123xyz", r.Id);
        Assert.Equal(["tinphong.finora.io.vn"], r.Hosts);
        Assert.Equal([9L], r.SiteIds);
        Assert.True(HostPlanner.IsRenewalFor(r, "tinphong.finora.io.vn", 9));
    }

    [Fact]
    public void Wacs_summary_masks_email()
    {
        var s = WacsClient.Summarize("Using account ops@finora.io.vn\nAuthorization failed: timeout\n");
        Assert.DoesNotContain("ops@finora.io.vn", s);
        Assert.Contains("Authorization failed", s);
    }
}
