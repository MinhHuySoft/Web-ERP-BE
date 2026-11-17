using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WebErp.Core.Errors;
using WebErp.Core.Menu;

namespace WebErp.Host.Auth;

/// <summary>
/// Menu của người dùng, cache theo (công ty, người dùng, ngôn ngữ) như WinForms (tài liệu 12 §5.3). Dùng cho cả <c>/erp/menu</c> lẫn
/// kiểm "màn này có được mở không" khi mở thẳng bằng URL (QĐ-03, ngoại lệ bảo mật 4).
/// <list type="bullet">
/// <item>Trong <see cref="SessionOptions.MenuCacheMinutes"/> phút: dùng menu đã đọc, không hỏi DB.</item>
/// <item>Quá hạn: đọc lại. DB chậm / mất kết nối mà còn menu cũ chưa quá <see cref="SessionOptions.MenuStaleMinutes"/> phút: dùng
/// menu cũ (WinForms giữ menu suốt phiên). Không có thì báo lỗi ("phản hồi chậm" / "không kết nối được").</item>
/// <item>Nhiều request cùng lúc của một người dùng chỉ chạy MỘT lần hàm menu (FE mở menu và màn hình gần như đồng thời).</item>
/// <item>Đăng xuất / đăng nhập: bỏ menu đã cache của người đó.</item>
/// </list>
/// </summary>
public sealed class MenuService(IErpMenu menu, IMemoryCache cache, IOptions<WebErpOptions> options, TimeProvider time, ILogger<MenuService> log)
{
    public const string Language = "vi";

    private readonly ConcurrentDictionary<string, Lazy<Task<Entry>>> _loading = new(StringComparer.Ordinal);

    private sealed record Entry(IReadOnlyList<MenuNode> Tree, DateTimeOffset LoadedAt);

    private static string Key(string tenantKey, string userName, string language) =>
        $"menu:{tenantKey.ToUpperInvariant()}:{userName.ToLowerInvariant()}:{language}";

    public async Task<IReadOnlyList<MenuNode>> GetTreeAsync(ErpSession s, CancellationToken ct)
    {
        var key = Key(s.Tenant.Key, s.UserName, Language);
        var session = options.Value.Session;
        cache.TryGetValue(key, out Entry? cached);
        if (cached is not null && time.GetUtcNow() - cached.LoadedAt < TimeSpan.FromMinutes(session.MenuCacheMinutes)) return cached.Tree;

        try
        {
            return (await LoadAsync(key, s, ct)).Tree;
        }
        catch (ErpDatabaseException ex) when (cached is not null && !ct.IsCancellationRequested
                                              && time.GetUtcNow() - cached.LoadedAt < TimeSpan.FromMinutes(session.MenuStaleMinutes))
        {
            log.LogWarning("Dùng menu cũ (đọc lúc {LoadedAt:O}) cho {User} công ty {Tenant}: {Reason}", cached.LoadedAt, s.UserName, s.Tenant.Key, ex.Message);
            return cached.Tree;
        }
    }

    /// <summary>Bỏ menu đã cache của một người dùng (đăng xuất, đăng nhập lại: đọc quyền mới).</summary>
    public void Invalidate(string tenantKey, string userName) => cache.Remove(Key(tenantKey, userName, Language));

    /// <summary>
    /// Đọc menu từ DB, gộp các request đồng thời. Lần đọc chung không theo <paramref name="ct"/> của request nào (một request bị
    /// hủy không làm hỏng các request khác); thời gian tối đa do timeout câu lệnh menu giới hạn.
    /// </summary>
    private Task<Entry> LoadAsync(string key, ErpSession s, CancellationToken ct)
    {
        Lazy<Task<Entry>>? mine = null;
        mine = new Lazy<Task<Entry>>(async () =>
        {
            try
            {
                var entries = await menu.GetUserMenuAsync(s.Tenant, s.UserName, Language, CancellationToken.None);
                var entry = new Entry(MenuTree.Build(entries), time.GetUtcNow());
                var session = options.Value.Session;
                cache.Set(key, entry, TimeSpan.FromMinutes(Math.Max(session.MenuStaleMinutes, session.MenuCacheMinutes)));
                return entry;
            }
            finally
            {
                _loading.TryRemove(new KeyValuePair<string, Lazy<Task<Entry>>>(key, mine!));
            }
        });
        return _loading.GetOrAdd(key, mine).Value.WaitAsync(ct);
    }

    /// <summary>Mục menu mở màn <paramref name="formName"/> (+ Para); null nếu người dùng không thấy mục nào như vậy.</summary>
    public async Task<MenuNode?> FindScreenAsync(ErpSession s, string formName, string? para, CancellationToken ct)
    {
        var tree = await GetTreeAsync(s, ct);
        return Flatten(tree).FirstOrDefault(n => n.Kind is MenuKind.Form or MenuKind.Report
            && string.Equals(n.Entry.FormName, formName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(n.Entry.Para ?? "", para ?? "", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<MenuNode> Flatten(IEnumerable<MenuNode> nodes) =>
        nodes.SelectMany(n => new[] { n }.Concat(Flatten(n.Children)));
}
