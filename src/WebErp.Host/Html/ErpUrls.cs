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
    public string Session => pathBase + "/erp/session";

    public string For(MenuNode node) => node.Kind switch
    {
        MenuKind.Command => Logout,
        MenuKind.Report => Screen("reports", node.Entry.FormName!, node.Entry.Para),
        _ => Screen("screens", node.Entry.FormName!, node.Entry.Para),
    };

    /// <summary>Danh sách / chứng từ / tab chi tiết của một màn metadata (hướng dẫn BE mục 3).</summary>
    public string Screen(string formName, string? para) => Screen("screens", formName, para);

    public string Record(string formName, string? para, string key) =>
        $"{pathBase}/erp/screens/{Uri.EscapeDataString(formName)}/{Uri.EscapeDataString(key)}" + ParaQuery(para);

    public string Detail(string formName, string? para, string key, string slot) =>
        $"{pathBase}/erp/screens/{Uri.EscapeDataString(formName)}/{Uri.EscapeDataString(key)}/tabs/{Uri.EscapeDataString(slot)}" + ParaQuery(para);

    /// <summary>Tra cứu F4 của ô chứng từ: <c>{q}</c> để FE thay bằng chữ gõ; <c>p0</c>, <c>p1</c>… là tham số của nguồn (chuẩn 10).</summary>
    public string Lookup(string formName, string? para, string field, IReadOnlyList<string> paras) =>
        $"{pathBase}/erp/screens/{Uri.EscapeDataString(formName)}/lookups/{Uri.EscapeDataString(field)}?q={{q}}"
        + (string.IsNullOrEmpty(para) ? "" : "&para=" + Uri.EscapeDataString(para))
        + string.Concat(paras.Select((p, i) => $"&p{i}={Uri.EscapeDataString(p)}"));

    /// <summary>Màn in theo mẫu (M6) của các chứng từ (nối bằng <c>;</c>), mẫu tùy chọn.</summary>
    public string Print(string formName, string? para, string documentIds, string? template = null) =>
        $"{pathBase}/erp/screens/{Uri.EscapeDataString(formName)}/print?DocumentID={Uri.EscapeDataString(documentIds)}"
        + (string.IsNullOrEmpty(template) ? "" : "&Template=" + Uri.EscapeDataString(template))
        + (string.IsNullOrEmpty(para) ? "" : "&para=" + Uri.EscapeDataString(para));

    /// <summary>
    /// Tệp mẫu in (<c>data-template-src</c>): JSON phiên bản web hoặc <c>.rpx</c> mới nhất. <paramref name="version"/> đổi khi mẫu đổi để
    /// FE không dùng bản đệm cũ.
    /// </summary>
    public string PrintTemplate(string formName, string? para, string template, string format = "rpx", string? version = null) =>
        $"{pathBase}/erp/screens/{Uri.EscapeDataString(formName)}/print/templates/{Uri.EscapeDataString(template)}.{(format == "json" ? "json" : "rpx")}"
        + (string.IsNullOrEmpty(version) ? "" : "?v=" + Uri.EscapeDataString(version))
        + (string.IsNullOrEmpty(para) ? "" : (string.IsNullOrEmpty(version) ? "?" : "&") + "para=" + Uri.EscapeDataString(para));

    /// <summary>Màn thiết kế mẫu in (chuẩn 15.2); <paramref name="documentId"/> để xem trước bằng dữ liệu thật.</summary>
    public string PrintDesigner(string formName, string? para, string template, string? documentId) =>
        $"{pathBase}/erp/screens/{Uri.EscapeDataString(formName)}/print/designer?Template={Uri.EscapeDataString(template)}"
        + (string.IsNullOrEmpty(documentId) ? "" : "&DocumentID=" + Uri.EscapeDataString(documentId))
        + (string.IsNullOrEmpty(para) ? "" : "&para=" + Uri.EscapeDataString(para));

    /// <summary>Lưu mẫu in (POST).</summary>
    public string PrintDesignerSave(string formName) => $"{pathBase}/erp/screens/{Uri.EscapeDataString(formName)}/print/designer/save";

    private static string ParaQuery(string? para) => string.IsNullOrEmpty(para) ? "" : "?para=" + Uri.EscapeDataString(para);

    private string Screen(string kind, string formName, string? para) =>
        $"{pathBase}/erp/{kind}/{Uri.EscapeDataString(formName)}" + (string.IsNullOrEmpty(para) ? "" : "?para=" + Uri.EscapeDataString(para));

    public static ErpUrls Of(HttpContext ctx) => new(ctx.Request.PathBase.Value ?? "");
}
