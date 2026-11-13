using System.Globalization;

namespace WebErp.Html.Roles;

/// <summary>Phân trang của lưới đứng ngay trước nó (chuẩn 11.5).</summary>
/// <param name="Href">URL của trang n (Host dựng, giữ nguyên bộ lọc đang áp).</param>
public sealed record PagerModel(int Page, int PageSize, int Total, Func<int, string> Href)
{
    public int Pages => PageSize <= 0 ? 1 : Math.Max(1, (Total + PageSize - 1) / PageSize);
}

/// <summary><c>nav[data-erp=pager]</c>: liên kết Đầu / Trước / Sau / Cuối mở trong cùng tab (<c>data-target=self</c>).</summary>
public static class PagerRenderer
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static void Write(HtmlWriter w, PagerModel p)
    {
        var pages = p.Pages;
        var page = Math.Clamp(p.Page, 1, pages);
        w.Open("nav", ("data-erp", "pager"), ("data-page", page.ToString(CultureInfo.InvariantCulture)),
            ("data-page-size", p.PageSize.ToString(CultureInfo.InvariantCulture)), ("data-total", p.Total.ToString(CultureInfo.InvariantCulture)),
            ("aria-label", "Phân trang"));
        if (page > 1)
        {
            Link(w, "first", "Đầu", p.Href(1));
            Link(w, "prev", "Trước", p.Href(page - 1));
        }
        var from = (page - 1) * p.PageSize + 1;
        var to = Math.Min(p.Total, page * p.PageSize);
        w.Element("span", $"Trang {page.ToString("N0", Vi)} / {pages.ToString("N0", Vi)} · dòng {from.ToString("N0", Vi)}–{to.ToString("N0", Vi)} / {p.Total.ToString("N0", Vi)}");
        if (page < pages)
        {
            Link(w, "next", "Sau", p.Href(page + 1));
            Link(w, "last", "Cuối", p.Href(pages));
        }
        w.Close("nav").Line();
    }

    private static void Link(HtmlWriter w, string rel, string text, string href) =>
        w.Element("a", text, ("rel", rel), ("href", href), ("data-target", "self"));
}
