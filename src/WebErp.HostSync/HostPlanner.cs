using WebErp.Config;

namespace WebErp.HostSync;

/// <summary>Một domain mong muốn cần giữ / dựng trên site WebErp.</summary>
/// <param name="Managed">Dòng <c>managed_host</c> hiện có (null: domain mới).</param>
/// <param name="AnyEnabled">Có ít nhất một công ty đang bật dùng domain này (ảnh hưởng cách kiểm /erp/login).</param>
public sealed record EnsureItem(string Host, ManagedHost? Managed, bool HasHttp, bool HasHttps, bool AnyEnabled);

/// <summary>Kết quả so sánh mong muốn – hiện có – đang quản lý. Không chạm hệ thống.</summary>
/// <param name="Ensure">Domain mong muốn, không trùng site khác.</param>
/// <param name="Conflicts">Domain mong muốn nhưng đã có binding trên site khác (host, tên site đó).</param>
/// <param name="Remove">Domain do agent quản lý, không còn mong muốn, được phép gỡ ngay.</param>
/// <param name="BlockedRemovals">Domain cần gỡ nhưng vượt ngưỡng gỡ hàng loạt, chờ xác nhận.</param>
/// <param name="Skipped">Domain mong muốn nhưng không phải tên công khai hợp lệ (vd localhost), bỏ qua.</param>
public sealed record HostPlan(
    IReadOnlyList<EnsureItem> Ensure,
    IReadOnlyList<(string Host, string Site)> Conflicts,
    IReadOnlyList<ManagedHost> Remove,
    IReadOnlyList<ManagedHost> BlockedRemovals,
    IReadOnlyList<string> Skipped);

public static class HostPlanner
{
    public static HostPlan Plan(IReadOnlyList<DesiredHost> desired, IReadOnlyList<ManagedHost> managed, IisSnapshot iis, HostSyncOptions options)
    {
        var site = iis.Site(options.SiteName) ?? throw new InvalidOperationException($"Không thấy site IIS '{options.SiteName}'.");
        var ignore = new HashSet<string>(options.IgnoreHosts.Select(h => h.Trim().ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
        var byHost = managed.ToDictionary(m => m.Host, StringComparer.OrdinalIgnoreCase);

        var ensure = new List<EnsureItem>();
        var conflicts = new List<(string, string)>();
        var skipped = new List<string>();
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in desired)
        {
            var host = d.Host.ToLowerInvariant();
            if (ignore.Contains(host)) continue;
            if (!HostNames.IsSyncable(host))
            {
                skipped.Add(host);
                continue;
            }
            wanted.Add(host);
            var other = iis.Sites.FirstOrDefault(s => s.Id != site.Id && s.Bindings.Any(b => b.IsWeb && string.Equals(b.Host, host, StringComparison.OrdinalIgnoreCase)));
            if (other is not null)
            {
                conflicts.Add((host, other.Name));
                continue;
            }
            ensure.Add(new EnsureItem(host, byHost.GetValueOrDefault(host), site.Has("http", host), site.Has("https", host), d.AnyEnabled));
        }

        // Chỉ gỡ domain có trong managed_host. Domain bị bỏ qua (IgnoreHosts) không bao giờ bị gỡ.
        var candidates = managed.Where(m => !wanted.Contains(m.Host) && !ignore.Contains(m.Host)).ToList();
        // Ngưỡng gỡ hàng loạt: tránh trường hợp đọc nhầm cấu hình rỗng mà gỡ sạch mọi domain đang chạy.
        // Domain "trùng site khác" chưa từng có binding của agent: gỡ không đụng IIS, không tính vào ngưỡng.
        var risky = candidates.Where(m => m.State != HostStates.Conflict).ToList();
        var guard = risky.Count > options.MaxRemovalsWithoutApproval || (wanted.Count == 0 && risky.Count > 0);
        var remove = new List<ManagedHost>();
        var blocked = new List<ManagedHost>();
        foreach (var m in candidates)
        {
            if (!guard || m.State == HostStates.Conflict || m.RemoveApproved) remove.Add(m);
            else blocked.Add(m);
        }
        return new HostPlan(ensure, conflicts, remove, blocked, skipped);
    }

    /// <summary>Kiểm DNS: mọi bản ghi A đúng bằng IP của VPS, không có AAAA (Let's Encrypt thử IPv6 trước). null = đạt.</summary>
    public static string? CheckDns(DnsAnswer answer, string publicIp)
    {
        if (answer.Error is not null) return answer.Error;
        if (answer.A.Count == 0) return $"Chưa có bản ghi A trỏ về {publicIp}.";
        var wrong = answer.A.Where(a => a != publicIp).ToList();
        if (wrong.Count > 0)
            return $"Bản ghi A đang là {string.Join(", ", answer.A)}, cần đúng {publicIp} (DNS only, tắt proxy).";
        if (answer.Aaaa.Count > 0) return $"Có bản ghi AAAA ({string.Join(", ", answer.Aaaa)}): xóa đi, VPS không dùng IPv6.";
        return null;
    }

    /// <summary>
    /// Renewal win-acme thuộc RIÊNG domain này trên site của agent: danh sách tên đúng bằng {<paramref name="host"/>} (so nguyên tên,
    /// <c>medcom.bms79.com</c> không khớp <c>api-medcom.bms79.com</c>) và cài vào site <paramref name="siteId"/>. Renewal nhiều tên
    /// hoặc không rõ site thì không bao giờ khớp.
    /// </summary>
    public static bool IsRenewalFor(AcmeRenewal r, string host, long siteId) =>
        r.Hosts.Count > 0
        && r.Hosts.All(h => string.Equals(h.Trim().TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase))
        && r.SiteIds.Contains(siteId);

    /// <summary>Thời gian chờ trước lần thử kế: tăng gấp đôi sau mỗi lần thất bại, có trần.</summary>
    public static TimeSpan Backoff(int attempts, TimeSpan first, TimeSpan max)
    {
        var factor = Math.Pow(2, Math.Clamp(attempts - 1, 0, 20));
        var t = TimeSpan.FromTicks((long)Math.Min(first.Ticks * factor, max.Ticks));
        return t < first ? first : t;
    }
}
