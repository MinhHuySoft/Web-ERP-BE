using WebErp.Core.Messages;

namespace WebErp.Html.Roles;

public sealed record CompanyOption(string Key, string Name);

/// <param name="AntiforgeryField">Tên input ẩn chống giả mạo (<c>__RequestVerificationToken</c>).</param>
public sealed record LoginModel(
    string Action,
    IReadOnlyList<CompanyOption> Companies,
    string? SelectedCompany,
    string? UserName,
    string AntiforgeryField,
    string AntiforgeryToken,
    IReadOnlyList<ErpMessage>? Messages = null);

/// <summary>Form đăng nhập tự chọn ô (trang quản trị: thêm mã xác thực 2 lớp; thiết lập lần đầu: kèm mã QR).</summary>
public sealed record CustomLoginModel(
    string Title,
    string Action,
    IReadOnlyList<FieldModel> Fields,
    string AntiforgeryField,
    string AntiforgeryToken,
    string SubmitText = "Đăng nhập",
    IReadOnlyList<string>? Paragraphs = null,
    string? ImageDataUri = null,
    string? ImageAlt = null,
    IReadOnlyList<(string Name, string Value)>? Hidden = null,
    IReadOnlyList<ErpMessage>? Messages = null);

/// <summary>
/// <c>form[data-erp=login]</c> (chuẩn 5.1). Ô chọn dùng tên <c>Database</c> như chuẩn v1.0, giá trị là mã công ty.
/// Domain chỉ có một công ty: không cho đổi, gửi bằng input ẩn (QĐ-07).
/// </summary>
public static class LoginRenderer
{
    public const string CompanyField = "Database";
    public const string UserField = "UserName";
    public const string PasswordField = "Password";

    public static string Render(LoginModel m) => HtmlPage.Render("Đăng nhập", w => Write(w, m));

    public static string Render(CustomLoginModel m) => HtmlPage.Render(m.Title, w =>
    {
        const string formId = "lg";
        w.Open("form", ("data-erp", "login"), ("method", "post"), ("action", m.Action)).Line();
        w.Element("h1", m.Title).Line();
        MessagesRenderer.Write(w, m.Messages);
        foreach (var p in m.Paragraphs ?? []) w.Element("p", p).Line();
        if (m.ImageDataUri is not null) w.Open("p").Void("img", ("src", m.ImageDataUri), ("alt", m.ImageAlt ?? ""), ("width", "200"), ("height", "200")).Close("p").Line();
        foreach (var f in m.Fields) FieldRenderer.Write(w, formId, f);
        foreach (var (name, value) in m.Hidden ?? []) w.Void("input", ("type", "hidden"), ("name", name), ("value", value)).Line();
        w.Void("input", ("type", "hidden"), ("name", m.AntiforgeryField), ("value", m.AntiforgeryToken)).Line();
        w.Element("button", m.SubmitText, ("type", "submit"), ("data-action", "login")).Line();
        w.Close("form");
    });

    public static void Write(HtmlWriter w, LoginModel m)
    {
        w.Open("form", ("data-erp", "login"), ("method", "post"), ("action", m.Action)).Line();
        w.Element("h1", "Đăng nhập ERP").Line();
        MessagesRenderer.Write(w, m.Messages);

        if (m.Companies.Count == 1)
        {
            w.Element("p", m.Companies[0].Name).Line();
            w.Void("input", ("type", "hidden"), ("name", CompanyField), ("value", m.Companies[0].Key)).Line();
        }
        else if (m.Companies.Count > 1)
        {
            w.Open("div", ("data-erp", "field"), ("data-control", "select"), ("data-span", "40"))
                .Element("label", "Công ty", ("for", "lg-company"))
                .Open("select", ("id", "lg-company"), ("name", CompanyField), Attr.Flag("required", true));
            foreach (var c in m.Companies)
            {
                var selected = string.Equals(c.Key, m.SelectedCompany, StringComparison.OrdinalIgnoreCase);
                w.Element("option", c.Name, ("value", c.Key), Attr.Flag("selected", selected));
            }
            w.Close("select").Close("div").Line();
        }

        w.Open("div", ("data-erp", "field"), ("data-control", "text"), ("data-span", "40"))
            .Element("label", "Tên đăng nhập", ("for", "lg-user"))
            .Void("input", ("id", "lg-user"), ("name", UserField), ("autocomplete", "username"), Attr.Flag("required", true),
                ("maxlength", "100"), ("value", m.UserName))
            .Close("div").Line();
        w.Open("div", ("data-erp", "field"), ("data-control", "password"), ("data-span", "40"))
            .Element("label", "Mật khẩu", ("for", "lg-pass"))
            // Không bao giờ ghi lại mật khẩu đã nhập. Không đặt required: ERP cho phép mật khẩu rỗng (tài liệu 12 §2.5).
            .Void("input", ("id", "lg-pass"), ("name", PasswordField), ("type", "password"), ("autocomplete", "current-password"))
            .Close("div").Line();
        w.Void("input", ("type", "hidden"), ("name", m.AntiforgeryField), ("value", m.AntiforgeryToken)).Line();
        w.Element("button", "Đăng nhập", ("type", "submit"), ("data-action", "login")).Line();
        w.Close("form");
    }
}
