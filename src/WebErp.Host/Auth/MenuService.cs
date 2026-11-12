using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using WebErp.Core.Menu;

namespace WebErp.Host.Auth;

/// <summary>
/// Menu của người dùng, cache ngắn như WinForms (tài liệu 12 §5.3). Dùng cho cả <c>/erp/menu</c> lẫn kiểm "màn này có được
/// mở không" khi mở thẳng bằng URL (QĐ-03, ngoại lệ bảo mật 4).
/// </summary>
public sealed class MenuService(IErpMenu menu, IMemoryCache cache, IOptions<WebErpOptions> options)
{
    public const string Language = "vi";

    public async Task<IReadOnlyList<MenuNode>> GetTreeAsync(ErpSession s, CancellationToken ct)
    {
        var key = $"menu:{s.Tenant.Key}:{s.UserName}:{Language}";
        if (cache.TryGetValue(key, out IReadOnlyList<MenuNode>? tree) && tree is not null) return tree;
        var entries = await menu.GetUserMenuAsync(s.Tenant, s.UserName, Language, ct);
        tree = MenuTree.Build(entries);
        cache.Set(key, tree, TimeSpan.FromMinutes(options.Value.Session.MenuCacheMinutes));
        return tree;
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
