using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using WebErp.Config;
using WebErp.Core.Auth;
using WebErp.Core.Errors;
using WebErp.Core.Messages;
using WebErp.Core.Tenancy;
using WebErp.Host.Auth;
using WebErp.Host.Html;
using WebErp.Host.Middleware;
using WebErp.Html.Roles;
using WebErp.Web;

namespace WebErp.Host.Endpoints;

/// <summary><c>/erp/login</c>, <c>/erp/logout</c> (chuẩn 5.1). Đăng nhập bằng tài khoản ERP (<c>SY_User</c>, hash kiểu cũ).</summary>
public static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/erp");
        g.MapGet("/login", (HttpContext ctx, IAntiforgery af) => LoginPage(ctx, af, null, null, null));
        g.MapPost("/login", LoginAsync);
        g.MapMethods("/logout", ["GET", "POST"], async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            // FE gọi URL đăng xuất rồi chờ form đăng nhập (shell.logout → showLogin): 303 về trang đăng nhập.
            return HtmlResults.SeeOther(ErpUrls.Of(ctx).Login);
        });
    }

    private static async Task<IResult> LoginAsync(HttpContext ctx, IAntiforgery af, IErpUsers users, LoginThrottle throttle,
        IOptions<WebErpOptions> options, SqliteTenantStore config, TimeProvider time, ILoggerFactory logs)
    {
        var log = logs.CreateLogger("WebErp.Login");
        var host = ctx.Features.Get<HostTenants>()!;
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var companyKey = form[LoginRenderer.CompanyField].ToString();
        // WinForms cắt khoảng trắng cả tên đăng nhập lẫn mật khẩu trước khi kiểm (LoginFrm, tài liệu 12 §2.4).
        var userName = form[LoginRenderer.UserField].ToString().Trim();
        // Không cắt khoảng trắng: WinForms giữ nguyên mật khẩu, nên mật khẩu có dấu cách ở đầu/cuối vẫn phải đăng nhập được.
        var password = form[LoginRenderer.PasswordField].ToString();

        try
        {
            await af.ValidateRequestAsync(ctx);
        }
        catch (AntiforgeryValidationException)
        {
            return LoginPage(ctx, af, companyKey, userName, [ErpMessage.Error("Trang đăng nhập đã hết hạn. Vui lòng đăng nhập lại.")],
                StatusCodes.Status400BadRequest);
        }

        var tenant = host.Tenants.Count == 1 ? host.Tenants[0] : host.Find(companyKey);
        if (tenant is null)
            return LoginPage(ctx, af, null, userName, [ErpMessage.Error("Vui lòng chọn công ty.", LoginRenderer.CompanyField)]);
        if (userName.Length == 0)
            return LoginPage(ctx, af, tenant.Key, userName, [ErpMessage.Error("Vui lòng nhập tên đăng nhập.", LoginRenderer.UserField)]);

        var now = time.GetUtcNow();
        if (tenant.InMaintenance(now))
            return LoginPage(ctx, af, tenant.Key, userName, [ErpMessage.Warning("Dữ liệu của công ty đang bảo trì. Vui lòng thử lại sau ít phút.")],
                StatusCodes.Status503ServiceUnavailable, TenantResolution.RetryAfter(tenant.MaintenanceUntil!.Value, now));

        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
        if (throttle.LockedFor(tenant.Key, userName, ip) is { } wait)
            return LoginPage(ctx, af, tenant.Key, userName,
                [ErpMessage.Error($"Đăng nhập sai quá nhiều lần. Vui lòng thử lại sau {Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes))} phút.")],
                StatusCodes.Status429TooManyRequests, (int)Math.Ceiling(wait.TotalSeconds));

        // Ưu tiên giá trị đặt ở trang quản trị (lưu mã hóa); biến môi trường của app pool là đường dự phòng.
        var key = await config.GetSettingAsync(SettingNames.LegacyPasswordKey, ctx.RequestAborted) ?? options.Value.LegacyPassword.Key;
        if (string.IsNullOrEmpty(key))
        {
            log.LogCritical("Chưa đặt hằng mật khẩu ERP (trang quản trị → Cài đặt chung, hoặc biến WebErp__LegacyPassword__Key). Không đăng nhập được.");
            return LoginPage(ctx, af, tenant.Key, userName, [ErpMessage.Error("Hệ thống chưa cấu hình xác thực. Vui lòng báo quản trị.")],
                StatusCodes.Status503ServiceUnavailable);
        }

        ErpLoginRecord? record;
        try
        {
            record = await users.FindForLoginAsync(tenant, userName, ctx.RequestAborted);
        }
        catch (ErpUnavailableException ex)
        {
            log.LogWarning(ex, "Không kết nối được DB công ty {Tenant} khi đăng nhập.", tenant.Key);
            return LoginPage(ctx, af, tenant.Key, userName,
                [ErpMessage.Error("Không kết nối được máy chủ dữ liệu của công ty. Vui lòng thử lại sau ít phút.")],
                StatusCodes.Status503ServiceUnavailable, 30);
        }

        if (record is null || !LegacyPassword.Verify(userName, password, record.PasswordHash, key))
        {
            throttle.Fail(tenant.Key, userName, ip);
            log.LogInformation("Đăng nhập sai: công ty {Tenant}, người dùng {User}, IP {Ip}.", tenant.Key, userName, ip);
            // Cùng câu với WinForms, không cho biết là sai tên hay sai mật khẩu.
            return LoginPage(ctx, af, tenant.Key, userName, [ErpMessage.Error("Người dùng không tồn tại hoặc mật mã không đúng.")]);
        }

        throttle.Success(tenant.Key, userName);
        var principal = ErpClaims.Create(tenant, record.User, now, CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties { IsPersistent = false });
        log.LogInformation("Đăng nhập: công ty {Tenant}, người dùng {User}, IP {Ip}.", tenant.Key, record.User.UserName, ip);
        // 303 về trang phiên (chuẩn 5.1 cho phép homeUrl hoặc sessionUrl): FE đọc phiên rồi dựng ứng dụng. Không về trang chủ, vì
        // trang chủ có thể kèm thông điệp cảnh báo mà FE hiểu nhầm là đăng nhập lỗi.
        return HtmlResults.SeeOther(ErpUrls.Of(ctx).Session);
    }

    private static IResult LoginPage(HttpContext ctx, IAntiforgery af, string? selected, string? userName, IReadOnlyList<ErpMessage>? messages,
        int status = StatusCodes.Status200OK, int? retryAfter = null)
    {
        var host = ctx.Features.Get<HostTenants>()!;
        var tokens = af.GetAndStoreTokens(ctx);
        var html = LoginRenderer.Render(new LoginModel(
            ErpUrls.Of(ctx).Login,
            host.Tenants.Select(t => new CompanyOption(t.Key, t.Name)).ToList(),
            selected,
            userName,
            tokens.FormFieldName,
            tokens.RequestToken ?? "",
            messages));
        return HtmlResults.Page(html, status, retryAfter);
    }
}
