namespace WebErp.Html.Roles;

/// <summary>
/// Một nút toolbar (chuẩn 7). Có <see cref="Href"/>: hành động GET (<c>&lt;a&gt;</c>). Không có: hành động đổi dữ liệu
/// (<c>&lt;button type=submit form formaction formmethod=post&gt;</c>) gắn với form <see cref="Form"/>.
/// </summary>
/// <param name="Action"><c>data-action</c> chuẩn: new, edit, save, delete, reload, cancel, custom…</param>
/// <param name="Target"><c>self</c> (thay màn hiện tại), <c>tab</c>, <c>dialog</c>, <c>_blank</c>.</param>
public sealed record ToolbarItem(
    string Action,
    string Text,
    string? Href = null,
    string? Form = null,
    string? FormAction = null,
    string? Icon = null,
    string? Confirm = null,
    string? Target = null,
    bool Disabled = false,
    string? Shortcut = null,
    string? Title = null);

/// <summary><c>menu[data-erp=toolbar]</c> (chuẩn 7).</summary>
public static class ToolbarRenderer
{
    public static void Write(HtmlWriter w, IReadOnlyList<ToolbarItem> items)
    {
        if (items.Count == 0) return;
        w.Open("menu", ("data-erp", "toolbar")).Line();
        foreach (var i in items)
        {
            w.Open("li");
            if (i.Href is not null)
            {
                w.Element("a", i.Text, ("data-action", i.Action), ("href", i.Href), ("data-target", i.Target), ("data-icon", i.Icon),
                    ("data-shortcut", i.Shortcut), ("data-confirm", i.Confirm));
            }
            else
            {
                // Nút không gắn form (chỉ hiện cho giống ERP, đang khóa): type=button, không gửi gì.
                w.Element("button", i.Text, ("type", i.Form is null ? "button" : "submit"), ("form", i.Form), ("formaction", i.FormAction),
                    ("formmethod", i.Form is null ? null : "post"), ("data-action", i.Action), ("data-icon", i.Icon), ("data-shortcut", i.Shortcut),
                    ("data-confirm", i.Confirm), ("title", i.Title), Attr.Flag("disabled", i.Disabled));
            }
            w.Close("li").Line();
        }
        w.Close("menu").Line();
    }
}
