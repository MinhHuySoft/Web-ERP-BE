using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace WebErp.Host.Auth;

/// <summary>
/// Chống dò mật khẩu: sai <c>MaxFailures</c> lần liên tiếp thì khóa tạm tài khoản (theo công ty + tên đăng nhập); một IP sai
/// quá nhiều lần (gấp 4) cũng bị khóa tạm. WinForms không giới hạn (tài liệu 12 §2.4); web thì bắt buộc vì mở ra Internet.
/// Lưu trong bộ nhớ: đủ cho 1 VPS (QĐ-07). Lên nhiều máy (tài liệu 05) thì chuyển sang nơi lưu chung.
/// </summary>
public sealed class LoginThrottle(TimeProvider time, IOptions<WebErpOptions> options)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Còn bị khóa bao lâu; null = được thử.</summary>
    public TimeSpan? LockedFor(string tenant, string userName, string ip)
    {
        var now = time.GetUtcNow();
        var wait = new[] { UserKey(tenant, userName), IpKey(ip) }
            .Select(k => _entries.TryGetValue(k, out var e) && e.LockedUntil > now ? e.LockedUntil.Value - now : (TimeSpan?)null)
            .Max();
        return wait;
    }

    public void Fail(string tenant, string userName, string ip)
    {
        var o = options.Value.Login;
        Bump(UserKey(tenant, userName), o.MaxFailures, o.LockMinutes);
        Bump(IpKey(ip), o.MaxFailures * 4, o.LockMinutes);
        if (_entries.Count > 10_000) Sweep();
    }

    public void Success(string tenant, string userName) => _entries.TryRemove(UserKey(tenant, userName), out _);

    private void Bump(string key, int max, int lockMinutes)
    {
        var now = time.GetUtcNow();
        _entries.AddOrUpdate(key,
            _ => new Entry(1, now, max <= 1 ? now.AddMinutes(lockMinutes) : null),
            (_, e) =>
            {
                var failures = now - e.FirstAt > Window ? 1 : e.Failures + 1;
                var first = failures == 1 ? now : e.FirstAt;
                return new Entry(failures, first, failures >= max ? now.AddMinutes(lockMinutes) : e.LockedUntil);
            });
    }

    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var (k, e) in _entries)
            if (now - e.FirstAt > Window && (e.LockedUntil is null || e.LockedUntil < now)) _entries.TryRemove(k, out _);
    }

    private static string UserKey(string tenant, string userName) => $"u:{tenant}|{userName.Trim()}";

    private static string IpKey(string ip) => $"ip:{ip}";

    private sealed record Entry(int Failures, DateTimeOffset FirstAt, DateTimeOffset? LockedUntil);
}
