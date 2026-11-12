using WebErp.Core.Menu;
using WebErp.Html.Roles;

namespace WebErp.Host.Html;

/// <summary>
/// URL mà BE đặt cho từng mục menu (chuẩn 2.4: FE không tự ghép URL nghiệp vụ). Bảng endpoint gợi ý ở hướng dẫn BE mục 3.
/// </summary>
public sealed class ErpUrls(string pathBase) : IMenuUrls
{
    public string Login => pathBase + "/erp/login";
    public string Logout => pathBase + "/erp/logout";
    public string Home => pathBase + "/erp/home";

    public string For(MenuNode node) => node.Kind switch
    {
        MenuKind.Command => Logout,
        MenuKind.Report => Screen("reports", node.Entry.FormName!, node.Entry.Para),
        _ => Screen("screens", node.Entry.FormName!, node.Entry.Para),
    };

    private string Screen(string kind, string formName, string? para) =>
        $"{pathBase}/erp/{kind}/{Uri.EscapeDataString(formName)}" + (string.IsNullOrEmpty(para) ? "" : "?para=" + Uri.EscapeDataString(para));

    public static ErpUrls Of(HttpContext ctx) => new(ctx.Request.PathBase.Value ?? "");
}
