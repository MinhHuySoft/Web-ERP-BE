using WebErp.Core.Messages;
using WebErp.Html.Roles;

namespace WebErp.Html.Screens;

/// <summary>Các màn đơn giản của M1: trang chủ, màn chưa có trên web, trang lỗi.</summary>
public static class BasicScreens
{
    public static string Home(string fullName, string companyName) =>
        ScreenRenderer.Render(
            new ScreenModel("Home", FormTypes.Dashboard, "Home", "Trang chủ", RefreshSrc: null),
            w =>
            {
                w.Element("p", $"Xin chào {fullName}.").Line();
                w.Element("p", $"Bạn đang làm việc trên dữ liệu của {companyName}. Chọn chức năng trên menu để bắt đầu.").Line();
            });

    /// <summary>Màn có trong menu nhưng bản web chưa dựng (M1). Vẫn là <c>screen</c> hợp lệ để FE mở thành tab.</summary>
    public static string NotAvailable(string formName, string? para, bool isReport, string? title = null)
    {
        var key = string.IsNullOrEmpty(para) ? formName : $"{formName}_{para}";
        // Chữ nằm trong thân màn (không dùng message info): FE hiện info thành toast rồi tắt, màn sẽ trống.
        return ScreenRenderer.Render(new ScreenModel(
                formName,
                isReport ? FormTypes.Report : FormTypes.Custom,
                key,
                title ?? formName,
                Para: string.IsNullOrEmpty(para) ? null : para),
            w => w.Element("p", "Màn hình này chưa có trên bản web. Vui lòng dùng ERP WinForms trong thời gian này.").Line());
    }

    /// <summary>Trang chỉ có thông điệp (401, 403, 404, 5xx). Chuẩn 13.1: không có screen thì FE hiện hộp thoại.</summary>
    public static string Error(string title, params ErpMessage[] messages) =>
        HtmlPage.Render(title, w => MessagesRenderer.Write(w, messages));
}
