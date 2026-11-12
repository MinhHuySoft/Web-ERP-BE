using WebErp.Core.Menu;

namespace WebErp.Html.Roles;

/// <summary>URL cho từng mục menu. Host quyết URL, renderer chỉ ghi ra (chuẩn mục 2.4: mọi URL nằm trong HTML).</summary>
public interface IMenuUrls
{
    string For(MenuNode node);
}

/// <summary><c>nav[data-erp=menu]</c> (chuẩn 5.3).</summary>
public static class MenuRenderer
{
    /// <summary>Biểu tượng của menu cấp 1 theo 2 chữ số đầu của <c>MenuID</c> (phân hệ ERP). Chỉ là gợi ý cho FE.</summary>
    private static readonly Dictionary<string, string> RootIcons = new(StringComparer.Ordinal)
    {
        ["01"] = "system", ["02"] = "catalog", ["04"] = "office", ["05"] = "purchase", ["06"] = "sales", ["07"] = "warehouse",
        ["08"] = "stock", ["09"] = "cash", ["10"] = "cash", ["11"] = "tools", ["12"] = "asset", ["14"] = "cost", ["16"] = "ledger",
        ["40"] = "business", ["60"] = "hr",
    };

    public static string Render(IReadOnlyList<MenuNode> roots, IMenuUrls urls) => HtmlPage.Render("Menu", w => Write(w, roots, urls));

    public static void Write(HtmlWriter w, IReadOnlyList<MenuNode> roots, IMenuUrls urls)
    {
        w.Open("nav", ("data-erp", "menu"), ("aria-label", "Menu chính")).Open("ul").Line();
        foreach (var r in roots) Item(w, r, urls);
        w.Close("ul").Close("nav");
    }

    private static void Item(HtmlWriter w, MenuNode n, IMenuUrls urls)
    {
        var e = n.Entry;
        if (n.Kind == MenuKind.Group)
        {
            var icon = n.Depth == 1 && e.MenuId.Length >= 2 && RootIcons.TryGetValue(e.MenuId[..2], out var i) ? i : null;
            w.Open("li", ("data-erp", "menu-group"), ("data-menu-id", e.MenuId), ("data-icon", icon),
                    Attr.Flag("data-begin-group", e.BeginGroup), Attr.Flag("data-strong", e.Bold))
                .Element("span", e.Caption)
                .Open("ul");
            foreach (var c in n.Children) Item(w, c, urls);
            w.Close("ul").Close("li").Line();
            return;
        }

        var kind = n.Kind switch { MenuKind.Report => "report", MenuKind.Command => "command", _ => "form" };
        w.Open("li", ("data-erp", "menu-item"), ("data-menu-id", e.MenuId), ("data-kind", kind),
            ("data-screen-id", e.FormName), ("data-para", string.IsNullOrEmpty(e.Para) ? null : e.Para),
            Attr.Flag("data-begin-group", e.BeginGroup), Attr.Flag("data-strong", e.Bold));
        w.Element("a", e.Caption, ("href", urls.For(n)), ("data-action", n.Kind == MenuKind.Command ? "logout" : null));
        w.Close("li").Line();
    }
}
