using WebErp.Core.Messages;

namespace WebErp.Html.Roles;

public static class FormTypes
{
    public const string List = "LIST";
    public const string ListEdit = "LISTEDIT";
    public const string Edit = "EDIT";
    public const string Report = "REPORT";
    public const string Dashboard = "DASHBOARD";
    public const string Custom = "CUSTOM";
}

/// <param name="ScreenId"><c>FormName</c> của ERP.</param>
/// <param name="ScreenKey">Khóa chống mở trùng tab, vd <c>AR_InvoiceFrm</c> hoặc <c>AR_InvoiceFrm:SI1126/0001</c>.</param>
/// <param name="Mode"><c>view</c> / <c>new</c> / <c>edit</c> / <c>find</c>.</param>
public sealed record ScreenModel(
    string ScreenId,
    string FormType,
    string ScreenKey,
    string Title,
    string Mode = "view",
    string? Para = null,
    string? MenuId = null,
    string? RefreshSrc = null,
    IReadOnlyList<ErpMessage>? Messages = null);

/// <summary><c>section[data-erp=screen]</c> (chuẩn 6). Thứ tự con: h1 → nội dung → messages.</summary>
public static class ScreenRenderer
{
    public static string Render(ScreenModel m, Action<HtmlWriter>? body = null) => HtmlPage.Render(m.Title, w => Write(w, m, body));

    public static void Write(HtmlWriter w, ScreenModel m, Action<HtmlWriter>? body = null)
    {
        w.Open("section",
            ("data-erp", "screen"),
            ("data-screen-id", m.ScreenId),
            ("data-para", m.Para),
            ("data-form-type", m.FormType),
            ("data-mode", m.Mode),
            ("data-screen-key", m.ScreenKey),
            ("data-menu-id", m.MenuId),
            ("data-refresh-src", m.RefreshSrc)).Line();
        w.Element("h1", m.Title).Line();
        body?.Invoke(w);
        MessagesRenderer.Write(w, m.Messages);
        w.Close("section");
    }
}
