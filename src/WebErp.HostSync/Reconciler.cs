using System.Globalization;
using Microsoft.Extensions.Logging;
using WebErp.Config;

namespace WebErp.HostSync;

/// <summary>Kết quả một vòng đồng bộ (in ra ở lệnh <c>once</c>, ghi log).</summary>
public sealed record RunSummary(long ConfigVersion, int Ensured, int Added, int Removed, int Pending, int Errors, int Blocked, bool Committed);

/// <summary>
/// Một vòng đồng bộ: đọc mong muốn (<c>tenant_host</c>) – hiện có (IIS) – đang quản lý (<c>managed_host</c>), rồi thêm / gỡ phần chênh.
/// <list type="number">
/// <item>Domain trùng site khác: ghi lỗi, không làm gì.</item>
/// <item>Domain mới / chưa chạy: kiểm DNS qua 1.1.1.1; chưa đúng thì "Chờ DNS", không gọi Let's Encrypt.</item>
/// <item>Thêm binding còn thiếu và gỡ binding thừa trong MỘT lần commit IIS.</item>
/// <item>Gỡ: ánh xạ SSL, renewal win-acme đúng domain, chứng chỉ không còn ai dùng.</item>
/// <item>Thêm: chạy win-acme, xác minh netsh + <c>https://host/erp/login</c> trả 200.</item>
/// </list>
/// </summary>
public sealed class Reconciler(
    HostSyncStore store,
    IIisAdmin iis,
    ISslBindings ssl,
    IAcmeClient acme,
    ICertificateStore certs,
    IDnsCheck dns,
    IHttpsProbe probe,
    HostSyncOptions options,
    TimeProvider time,
    ILogger<Reconciler> log)
{
    private static readonly TimeSpan DnsFirst = TimeSpan.FromMinutes(1), DnsMax = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AcmeFirst = TimeSpan.FromMinutes(15), AcmeMax = TimeSpan.FromHours(6);
    private static readonly TimeSpan VerifyFirst = TimeSpan.FromMinutes(2), VerifyMax = TimeSpan.FromHours(1);

    public string AgentVersion { get; init; } = "dev";

    /// <summary>Phiên bản agent đang chờ cài ở thư mục staging (null nếu không có).</summary>
    public Func<string?> StagedVersion { get; init; } = () => null;

    public async Task<RunSummary> RunOnceAsync(CancellationToken ct)
    {
        var version = store.ConfigVersion();
        var desired = store.Desired();
        var rows = store.Managed().ToDictionary(m => m.Host, StringComparer.OrdinalIgnoreCase);
        var snap = iis.Read();
        var site = snap.Site(options.SiteName) ?? throw new InvalidOperationException($"Không thấy site IIS '{options.SiteName}'.");
        var plan = HostPlanner.Plan(desired, rows.Values.ToList(), snap, options);
        var now = time.GetUtcNow();
        int added = 0, removed = 0, pending = 0, errors = 0;

        void Put(ManagedHost next, string? reason = null)
        {
            var before = rows.GetValueOrDefault(next.Host);
            next = next with { LastCheckedAt = now };
            rows[next.Host] = next;
            store.Save(next);
            if (before?.State != next.State)
            {
                log.LogInformation("{Host}: {From} -> {To} {Reason}", next.Host, before?.State ?? "(mới)", next.State, reason ?? next.LastError ?? "");
                var why = reason ?? next.LastError;
                store.Audit(HostSyncStore.AgentActor, "hostsync.state", next.Host, $"{before?.State ?? "new"} -> {next.State}{(why is null ? "" : ": " + why)}");
            }
        }

        ManagedHost Row(string host) => rows.GetValueOrDefault(host) ?? new ManagedHost(host, HostStates.PendingDns);

        void Fail(string host, string state, string reason, TimeSpan first, TimeSpan max)
        {
            var m = Row(host);
            var attempts = m.Attempts + 1;
            Put(m with { State = state, LastError = reason, Attempts = attempts, NextAttemptAt = now + HostPlanner.Backoff(attempts, first, max), RemoveApproved = false });
            if (state == HostStates.PendingDns) pending++; else errors++;
        }

        // 1. Trùng site khác.
        foreach (var (host, other) in plan.Conflicts)
        {
            var reason = $"Domain đã có binding trên site IIS '{other}'. Gỡ ở site đó trước, hoặc bỏ domain khỏi công ty.";
            var m = rows.GetValueOrDefault(host);
            // Chưa từng có binding của agent: trạng thái "conflict" (khi bỏ chỉ xóa dòng, không đụng IIS). Đã có: "error", giữ nguyên.
            var state = m is null || m.State is HostStates.Conflict or HostStates.PendingDns ? HostStates.Conflict : HostStates.Error;
            if (m?.State != state || m.LastError != reason) Put(Row(host) with { State = state, LastError = reason });
            errors++;
        }

        // 2. Gỡ bị chặn (vượt ngưỡng gỡ hàng loạt).
        foreach (var m in plan.BlockedRemovals)
        {
            if (m.State != HostStates.RemovalBlocked)
                Put(m with { State = HostStates.RemovalBlocked, LastError = $"Cần gỡ hơn {options.MaxRemovalsWithoutApproval} domain cùng lúc: chờ quản trị viên xác nhận." });
        }

        // 3. Domain mong muốn: phân loại khỏe / nhận quản lý / cần làm.
        var ready = new List<EnsureItem>();
        foreach (var item in plan.Ensure)
        {
            var m = item.Managed;
            var cert = item.HasHttps ? CertOf(ssl.Find(item.Host)) : null;
            var certOk = cert is not null && cert.Covers(item.Host) && cert.NotAfter > now;
            if (item.HasHttp && item.HasHttps && certOk)
            {
                if (m is null)
                {
                    // Binding + chứng chỉ có sẵn (cấu hình tay) và domain thuộc một công ty: nhận quản lý.
                    Put(new ManagedHost(item.Host, HostStates.Active, Adopted: true, cert!.Thumbprint, cert.NotAfter, ExpiryWarning(cert, now)));
                    store.Audit(HostSyncStore.AgentActor, "hostsync.adopt", item.Host, $"cert={cert.Thumbprint}");
                    continue;
                }
                if (m.State == HostStates.Active)
                {
                    if (m.LastCheckedAt is null || now - m.LastCheckedAt >= TimeSpan.FromMinutes(options.CheckIntervalMinutes) || m.CertThumbprint != cert!.Thumbprint)
                        Put(m with { CertThumbprint = cert!.Thumbprint, CertExpires = cert.NotAfter, LastError = ExpiryWarning(cert, now) });
                    continue;
                }
            }
            if (m is not null && m.NextAttemptAt > now && m.State is HostStates.PendingDns or HostStates.Error)
            {
                if (m.State == HostStates.PendingDns) pending++; else errors++;
                continue;
            }
            var dnsError = HostPlanner.CheckDns(await dns.QueryAsync(item.Host, ct).ConfigureAwait(false), options.PublicIp);
            if (dnsError is not null)
            {
                Fail(item.Host, HostStates.PendingDns, dnsError, DnsFirst, DnsMax);
                continue;
            }
            ready.Add(item);
        }

        // 4. Một lần commit IIS cho mọi binding cần thêm / gỡ.
        var addHosts = ready.Where(i => !i.HasHttp || !i.HasHttps).Select(i => i.Host).ToList();
        var removable = plan.Remove.Where(m => m.State != HostStates.Conflict).ToList();
        var removeHosts = removable.Where(m => site.Has("http", m.Host) || site.Has("https", m.Host)).Select(m => m.Host).ToList();
        foreach (var m in removable) Put(m with { State = HostStates.Removing, LastError = null });
        var committed = false;
        if (addHosts.Count > 0 || removeHosts.Count > 0)
        {
            try
            {
                iis.ApplyBindings(site.Name, addHosts, removeHosts);
                committed = true;
                log.LogWarning("Đã sửa binding site {Site}: thêm [{Add}], gỡ [{Remove}]. App WebErp khởi động lại.", site.Name, string.Join(", ", addHosts), string.Join(", ", removeHosts));
                store.Audit(HostSyncStore.AgentActor, "hostsync.bindings", site.Name, $"add={string.Join(",", addHosts)}; remove={string.Join(",", removeHosts)}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Không sửa được binding site {Site}.", site.Name);
                var reason = "Không sửa được binding IIS: " + ex.Message;
                foreach (var i in ready) Fail(i.Host, HostStates.Error, reason, AcmeFirst, AcmeMax);
                foreach (var m in removable) Put(Row(m.Host) with { State = HostStates.Error, LastError = reason });
                store.Heartbeat(version, AgentVersion, StagedVersion(), reason);
                return new RunSummary(version, plan.Ensure.Count, 0, 0, pending, errors + ready.Count + removable.Count, plan.BlockedRemovals.Count, false);
            }
        }

        // 5. Gỡ phần còn lại của domain đã bỏ.
        foreach (var m in plan.Remove)
        {
            if (m.State == HostStates.Conflict)
            {
                store.Delete(m.Host);
                rows.Remove(m.Host);
                store.Audit(HostSyncStore.AgentActor, "hostsync.forget", m.Host, "trùng site khác, không đụng IIS");
                continue;
            }
            try
            {
                var done = await CleanUpAsync(m, site, ct).ConfigureAwait(false);
                store.Delete(m.Host);
                rows.Remove(m.Host);
                removed++;
                log.LogWarning("Đã gỡ domain {Host}: {Done}", m.Host, done);
                store.Audit(HostSyncStore.AgentActor, "hostsync.remove", m.Host, done);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogError(ex, "Gỡ domain {Host} chưa xong.", m.Host);
                Put(Row(m.Host) with { State = HostStates.Error, LastError = "Gỡ chưa xong: " + ex.Message });
                errors++;
            }
        }

        // 6. Cấp chứng chỉ và xác minh domain mới.
        foreach (var item in ready)
        {
            var before = Row(item.Host);
            Put(before with { State = HostStates.Issuing, LastError = null });
            var cert = CertOf(ssl.Find(item.Host));
            if (cert is null || !cert.Covers(item.Host) || cert.NotAfter <= now)
            {
                AcmeResult r;
                try
                {
                    r = await acme.IssueAsync(item.Host, site.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    r = new AcmeResult(false, -1, ex.Message);
                }
                if (!r.Ok)
                {
                    Fail(item.Host, HostStates.Error, $"win-acme lỗi (mã {r.ExitCode}): {HostNames.MaskEmails(r.Summary)}", AcmeFirst, AcmeMax);
                    continue;
                }
                cert = CertOf(ssl.Find(item.Host));
                if (cert is null || !cert.Covers(item.Host))
                {
                    Fail(item.Host, HostStates.Error, $"win-acme chạy xong nhưng chưa có ánh xạ SSL {item.Host}:443 với chứng chỉ đúng domain.", AcmeFirst, AcmeMax);
                    continue;
                }
            }

            var p = await probe.GetAsync(item.Host, options.ProbePath, ct).ConfigureAwait(false);
            // Công ty đang tắt: app trả thông báo tạm dừng, chỉ cần HTTPS bắt tay được và có trả lời.
            var ok = p.Status == 200 || (!item.AnyEnabled && p.Status is not null);
            if (!ok)
            {
                Fail(item.Host, HostStates.Error, $"Kiểm https://{item.Host}{options.ProbePath} không đạt: {(p.Status is { } s ? "HTTP " + s.ToString(CultureInfo.InvariantCulture) : p.Error)}",
                    VerifyFirst, VerifyMax);
                continue;
            }
            Put(Row(item.Host) with
            {
                State = HostStates.Active, CertThumbprint = cert.Thumbprint, CertExpires = cert.NotAfter, LastError = ExpiryWarning(cert, now),
                Attempts = 0, NextAttemptAt = null, RemoveApproved = false,
            });
            added++;
            store.Audit(HostSyncStore.AgentActor, "hostsync.add", item.Host, $"cert={cert.Thumbprint}; expires={cert.NotAfter:yyyy-MM-dd}");
        }

        store.Heartbeat(version, AgentVersion, StagedVersion(), null);
        return new RunSummary(version, plan.Ensure.Count, added, removed, pending, errors, plan.BlockedRemovals.Count, committed);
    }

    /// <summary>
    /// Nhận quản lý một domain đã cấu hình tay (lệnh <c>adopt</c> của setup-hostsync.ps1): phải có binding https trên site của agent
    /// và ánh xạ SSL với chứng chỉ đúng domain. Chạy lại nhiều lần không sao. Trả null nếu được, ngược lại là lý do.
    /// </summary>
    public string? Adopt(string host)
    {
        host = host.Trim().ToLowerInvariant();
        if (!HostNames.IsSyncable(host)) return $"Tên miền '{host}' không hợp lệ.";
        var site = iis.Read().Site(options.SiteName);
        if (site is null) return $"Không thấy site IIS '{options.SiteName}'.";
        if (!site.Has("https", host)) return $"Site {options.SiteName} chưa có binding https cho {host}.";
        var cert = CertOf(ssl.Find(host));
        if (cert is null || !cert.Covers(host)) return $"Chưa có ánh xạ SSL {host}:443 với chứng chỉ đúng domain.";
        var now = time.GetUtcNow();
        var m = store.Get(host) ?? new ManagedHost(host, HostStates.Active);
        store.Save(m with
        {
            State = HostStates.Active, Adopted = true, CertThumbprint = cert.Thumbprint, CertExpires = cert.NotAfter, LastError = ExpiryWarning(cert, now),
            Attempts = 0, NextAttemptAt = null, LastCheckedAt = now,
        });
        store.Audit(HostSyncStore.AgentActor, "hostsync.adopt", host, $"cert={cert.Thumbprint}; expires={cert.NotAfter:yyyy-MM-dd}");
        return null;
    }

    /// <summary>Sau khi binding đã gỡ: ánh xạ SSL, renewal win-acme, chứng chỉ. Trả mô tả đã gỡ gì (không có bí mật).</summary>
    private async Task<string> CleanUpAsync(ManagedHost m, IisSite site, CancellationToken ct)
    {
        var done = new List<string> { "binding" };
        var after = iis.Read();
        // Ánh xạ host:443 là chung của HTTP.sys: chỉ xóa khi không site nào khác còn binding https cho domain này.
        var otherHttps = after.Sites.Any(s => s.Id != site.Id && s.Has("https", m.Host));
        if (!otherHttps && ssl.Find(m.Host) is not null)
        {
            ssl.Delete(m.Host);
            done.Add("sslcert");
        }

        foreach (var r in acme.Renewals().Where(r => HostPlanner.IsRenewalFor(r, m.Host, site.Id)))
        {
            if (!await acme.CancelAsync(r, ct).ConfigureAwait(false)) throw new InvalidOperationException($"Không hủy được renewal win-acme {r.Id}.");
            done.Add("renewal " + r.Id);
        }

        // Chứng chỉ chỉ mang đúng domain này và không còn binding / ánh xạ nào dùng.
        var used = new HashSet<string>(ssl.All().Select(b => b.CertificateHash), StringComparer.OrdinalIgnoreCase);
        foreach (var s in after.Sites)
        foreach (var b in s.Bindings)
            if (!string.IsNullOrEmpty(b.CertificateHash)) used.Add(b.CertificateHash);
        foreach (var c in certs.OnlyFor(m.Host))
        {
            if (used.Contains(c.Thumbprint)) continue;
            certs.Delete(c.Thumbprint);
            done.Add("cert " + c.Thumbprint);
        }
        return string.Join("; ", done);
    }

    private CertInfo? CertOf(SslCertBinding? b) => b is null ? null : certs.Get(b.CertificateHash);

    private string? ExpiryWarning(CertInfo cert, DateTimeOffset now) =>
        cert.NotAfter - now < TimeSpan.FromDays(options.ExpiryWarningDays)
            ? $"Chứng chỉ hết hạn {cert.NotAfter:dd/MM/yyyy}: kiểm tác vụ win-acme renew."
            : null;
}
