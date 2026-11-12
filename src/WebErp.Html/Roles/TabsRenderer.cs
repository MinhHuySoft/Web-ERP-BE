using System.Globalization;

namespace WebErp.Html.Roles;

/// <summary>Một tab chi tiết. <see cref="Src"/> có giá trị thì FE nạp nội dung khi mở tab lần đầu (nạp lười).</summary>
public sealed record TabPanelModel(string Id, string Title, string? Src = null, int? Badge = null, Action<HtmlWriter>? Content = null);

/// <summary><c>section[data-erp=tabs]</c> &gt; <c>section[data-erp=tab-panel]</c> (chuẩn 12).</summary>
public static class TabsRenderer
{
    public static void Write(HtmlWriter w, IReadOnlyList<TabPanelModel> panels)
    {
        if (panels.Count == 0) return;
        w.Open("section", ("data-erp", "tabs")).Line();
        foreach (var p in panels)
        {
            w.Open("section", ("data-erp", "tab-panel"), ("data-tab-id", p.Id), ("data-title", p.Title),
                ("data-badge", p.Badge?.ToString(CultureInfo.InvariantCulture)), ("data-src", p.Src)).Line();
            p.Content?.Invoke(w);
            w.Close("section").Line();
        }
        w.Close("section").Line();
    }
}
