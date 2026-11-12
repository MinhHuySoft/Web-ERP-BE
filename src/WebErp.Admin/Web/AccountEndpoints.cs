using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using QRCoder;
using WebErp.Config;
using WebErp.Core.Auth;
using WebErp.Core.Menu;
using WebErp.Core.Messages;
using WebErp.Html;
using WebErp.Html.Roles;
using WebErp.Html.Screens;
using WebErp.Web;

namespace WebErp.Admin.Web;

/// <summary>
/// Đăng nhập quản trị (<c>/admin/login</c>), thiết lập tài khoản đầu tiên, phiên, menu, trang chủ.
/// Chưa có tài khoản nào: <c>/admin/login</c> trả form THIẾT LẬP, nhưng chỉ khi mở từ chính VPS.
/// </summary>
public static class AccountEndpoints
{
    private const string SetupField = "__setup";
    private static readonly TimeSpan SetupLifetime = TimeSpan.FromMinutes(20);

    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin");
        g.MapGet("/login", (HttpContext ctx, AdminAccountStore accounts, IDataProtectionProvider dp) => LoginPage(ctx, accounts, dp, null, null));
        g.MapPost("/login", LoginAsync);
        g.MapMethods("/logout", ["GET", "POST"], async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return HtmlResults.SeeOther(ctx.Url("/admin/login"));
        });

        var auth = app.MapGroup("/admin").RequireAuthorization();
        auth.MapGet("/session", Session);
        auth.MapGet("/menu", (HttpContext ctx) => HtmlResults.Page(MenuRenderer.Render(MenuTree.Build(MenuEntries), new MenuUrls(ctx))));
        auth.MapGet("/home", Home);
    }

    private static async Task<IResult> LoginAsync(HttpContext ctx, AdminAccountStore accounts, IDataProtectionProvider dp, TimeProvider time, ILoggerFactory logs)
    {
        var log = logs.CreateLogger("WebErp.Admin.Login");
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var userName = form["UserName"].ToString().Trim();
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx))
            return LoginPage(ctx, accounts, dp, userName, [ErpMessage.Error("Trang đã hết hạn. Vui lòng tải lại.")], StatusCodes.Status400BadRequest, form[SetupField]);

        if (accounts.Count() == 0)
        {
            if (!AdminGate.IsLocal(ctx)) return LoginPage(ctx, accounts, dp, null, null, StatusCodes.Status403Forbidden);
            return await SetupAsync(ctx, accounts, dp, time, form, userName, log);
        }

        var result = accounts.Login(userName, form["Password"].ToString(), form["Code"].ToString());
        switch (result.Status)
        {
            case AdminLoginStatus.Ok:
                await AdminAuth.SignInAsync(ctx, userName, time);
                log.LogInformation("Quản trị đăng nhập: {User} từ {Ip}.", userName, ctx.Connection.RemoteIpAddress);
                return HtmlResults.SeeOther(ctx.Url("/admin/session"));
            case AdminLoginStatus.Locked:
                log.LogWarning("Tài khoản quản trị {User} bị khóa tạm (từ {Ip}).", userName, ctx.Connection.RemoteIpAddress);
                return LoginPage(ctx, accounts, dp, userName,
                    [ErpMessage.Error($"Đăng nhập sai quá nhiều lần. Thử lại sau {Math.Max(1, (int)Math.Ceiling((result.LockedUntil!.Value - time.GetUtcNow()).TotalMinutes))} phút.")],
                    StatusCodes.Status429TooManyRequests);
            default:
                log.LogInformation("Quản trị đăng nhập sai: {User} từ {Ip}.", userName, ctx.Connection.RemoteIpAddress);
                return LoginPage(ctx, accounts, dp, userName, [ErpMessage.Error("Tên đăng nhập, mật khẩu hoặc mã xác thực không đúng.")]);
        }
    }

    private static async Task<IResult> SetupAsync(HttpContext ctx, AdminAccountStore accounts, IDataProtectionProvider dp, TimeProvider time,
        IFormCollection form, string userName, ILogger log)
    {
        var protector = SetupProtector(dp);
        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(protector.Unprotect(form[SetupField].ToString()));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            return LoginPage(ctx, accounts, dp, userName, [ErpMessage.Error("Mã QR đã hết hạn (20 phút). Tải lại trang để lấy mã mới.")], StatusCodes.Status400BadRequest);
        }

        var password = form["Password"].ToString();
        var keep = form[SetupField].ToString();
        if (password != form["Password2"].ToString())
            return LoginPage(ctx, accounts, dp, userName, [ErpMessage.Error("Hai lần nhập mật khẩu không khớp.", "Password2")], keep: keep);
        if (Totp.Verify(secret, form["Code"].ToString(), time.GetUtcNow(), 0) is not { } step)
            return LoginPage(ctx, accounts, dp, userName, [ErpMessage.Error("Mã xác thực không đúng. Kiểm tra giờ trên điện thoại và nhập mã đang hiện.", "Code")], keep: keep);
        try
        {
            accounts.Create(userName, password, secret, step, $"{userName} (thiết lập, {ctx.Connection.RemoteIpAddress})");
        }
        catch (ArgumentException ex)
        {
            return LoginPage(ctx, accounts, dp, userName, [ErpMessage.Error(ex.Message)], keep: keep);
        }
        log.LogWarning("Đã tạo tài khoản quản trị đầu tiên: {User}.", userName);
        await AdminAuth.SignInAsync(ctx, userName, time);
        return HtmlResults.SeeOther(ctx.Url("/admin/session"));
    }

    private static IResult LoginPage(HttpContext ctx, AdminAccountStore accounts, IDataProtectionProvider dp, string? userName,
        IReadOnlyList<ErpMessage>? messages, int status = StatusCodes.Status200OK, string? keep = null)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        var action = ctx.Url("/admin/login");
        if (accounts.Count() > 0)
        {
            return HtmlResults.Page(LoginRenderer.Render(new CustomLoginModel("Đăng nhập quản trị", action,
            [
                new FieldModel("UserName", "Tên đăng nhập", Value: userName, Span: 40, Required: true, Autocomplete: "username", MaxLength: 40),
                new FieldModel("Password", "Mật khẩu", "password", Span: 40, Required: true, Autocomplete: "current-password"),
                CodeField(),
            ], field, token, Messages: messages)), status);
        }

        if (!AdminGate.IsLocal(ctx))
        {
            return HtmlResults.Page(LoginRenderer.Render(new CustomLoginModel("Chưa có tài khoản quản trị", action, [], field, token,
                Paragraphs: ["Tài khoản quản trị đầu tiên chỉ tạo được trên chính VPS: đăng nhập máy chủ, mở http://127.0.0.1:8082/ bằng trình duyệt."],
                Messages: messages)), status);
        }

        // Thiết lập lần đầu: khóa TOTP sinh ở server, đi kèm form dưới dạng đã mã hóa và có hạn 20 phút.
        var protector = SetupProtector(dp);
        byte[] secret;
        try
        {
            secret = keep is null ? Totp.NewSecret() : Convert.FromBase64String(protector.Unprotect(keep));
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException)
        {
            secret = Totp.NewSecret();
        }
        var uri = Totp.ProvisioningUri($"WebErp {ctx.Request.Host.Host}", string.IsNullOrEmpty(userName) ? "admin" : userName, secret);
        var qr = "data:image/png;base64," + Convert.ToBase64String(PngByteQRCodeHelper.GetQRCode(uri, QRCodeGenerator.ECCLevel.M, 5));
        var manual = string.Join(' ', Totp.Base32(secret).Chunk(4).Select(c => new string(c)));
        return HtmlResults.Page(LoginRenderer.Render(new CustomLoginModel("Tạo tài khoản quản trị đầu tiên", action,
        [
            new FieldModel("UserName", "Tên đăng nhập", Value: userName, Span: 40, Required: true, Autocomplete: "username", MaxLength: 40,
                Hint: "Chữ, số, dấu chấm, gạch dưới, gạch ngang; 3–40 ký tự."),
            new FieldModel("Password", "Mật khẩu", "password", Span: 40, Required: true, Autocomplete: "new-password",
                Hint: $"Từ {AdminPasswordHasher.MinLength} ký tự, có ít nhất 3 loại: chữ thường, chữ hoa, chữ số, ký tự đặc biệt."),
            new FieldModel("Password2", "Nhập lại mật khẩu", "password", Span: 40, Required: true, Autocomplete: "new-password"),
            CodeField(),
        ], field, token, SubmitText: "Tạo tài khoản",
            Paragraphs:
            [
                "Trang này chỉ mở được trên chính VPS. Đăng nhập trang quản trị luôn cần mật khẩu và mã 6 số từ ứng dụng xác thực.",
                "1. Cài ứng dụng xác thực trên điện thoại (Google Authenticator, Microsoft Authenticator…).",
                $"2. Quét mã QR dưới đây, hoặc nhập tay khóa: {manual}",
                "3. Đặt tên đăng nhập, mật khẩu, rồi nhập mã 6 số đang hiện trong ứng dụng.",
            ],
            ImageDataUri: qr, ImageAlt: "Mã QR cho ứng dụng xác thực",
            Hidden: [(SetupField, keep ?? protector.Protect(Convert.ToBase64String(secret), SetupLifetime))],
            Messages: messages)), status);
    }

    internal static FieldModel CodeField(string label = "Mã xác thực (6 số)") =>
        new("Code", label, Span: 40, Required: true, MaxLength: 6, Pattern: "[0-9]{6}", InputMode: "numeric", Autocomplete: "one-time-code",
            Hint: "Mã đang hiện trong ứng dụng xác thực trên điện thoại.");

    private static ITimeLimitedDataProtector SetupProtector(IDataProtectionProvider dp) =>
        dp.CreateProtector("WebErp.Admin.Setup.v1").ToTimeLimitedDataProtector();

    private static IResult Session(HttpContext ctx, IOptions<AdminOptions> options, TimeProvider time)
    {
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), AdminScreens.Zone(options.Value));
        return HtmlResults.Page(SessionRenderer.Render(new SessionModel(
        [
            new SessionItem("company", "Hệ thống", "Quản trị ERP Web"),
            new SessionItem("user", "Quản trị viên", AdminAuth.UserName(ctx), AdminAuth.UserName(ctx)),
            new SessionItem("server-time", "Giờ máy chủ", local.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), local.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
        ], [new SessionLink("logout", "Đăng xuất", ctx.Url("/admin/logout"))])));
    }

    private static IResult Home(HttpContext ctx, ConfigDatabase db, SecretProtector protector, TimeProvider time)
    {
        var writer = new ConfigWriter(db, protector, time, AdminAuth.Actor(ctx));
        var tenants = writer.List();
        var keySet = writer.HasSetting(SettingNames.LegacyPasswordKey);
        return HtmlResults.Page(ScreenRenderer.Render(new ScreenModel("AdminHome", FormTypes.Dashboard, "AdminHome", "Trang chủ quản trị",
            Messages: keySet ? null : [ErpMessage.Warning("Chưa đặt hằng mật khẩu ERP: người dùng ERP chưa đăng nhập được. Vào Cài đặt chung để đặt.")]), w =>
        {
            Kpi(w, "Công ty", tenants.Count, null);
            Kpi(w, "Đang bật", tenants.Count(t => t.Tenant.Enabled), "success");
            Kpi(w, "Chưa có chuỗi kết nối", tenants.Count(t => !t.HasConnection), tenants.Any(t => !t.HasConnection) ? "warning" : null);
            Kpi(w, "Phiên bản cấu hình", writer.Version(), null);
            w.Open("p").Element("a", "Mở danh sách công ty", ("href", ctx.Url("/admin/tenants")), ("data-target", "tab")).Close("p").Line();
        }));
    }

    private static void Kpi(HtmlWriter w, string title, long value, string? tone)
    {
        var v = value.ToString(CultureInfo.InvariantCulture);
        w.Open("section", ("data-erp", "kpi"), ("data-tone", tone)).Element("h2", title).Element("data", v, ("value", v)).Close("section").Line();
    }

    private static readonly MenuEntry[] MenuEntries =
    [
        new("01", null, "Quản trị", null, null, false, false),
        new("0101", "01", "Công ty", "AdminTenants", null, false, false),
        new("0102", "01", "Cài đặt chung", "AdminSettings", null, false, false),
        new("0103", "01", "Nhật ký thao tác", "AdminAudit", null, true, false),
    ];

    private sealed class MenuUrls(HttpContext ctx) : IMenuUrls
    {
        public string For(MenuNode node) => node.Entry.FormName switch
        {
            "AdminTenants" => ctx.Url("/admin/tenants"),
            "AdminSettings" => ctx.Url("/admin/settings"),
            "AdminAudit" => ctx.Url("/admin/audit"),
            _ => ctx.Url("/admin/home"),
        };
    }
}
