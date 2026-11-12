using Microsoft.AspNetCore.Authentication.Cookies;
using WebErp.Core.Messages;
using WebErp.Host.Html;
using WebErp.Html.Screens;
using WebErp.Web;

namespace WebErp.Host.Auth;

public static class AuthSetup
{
    public const string AntiforgeryField = "__RequestVerificationToken";

    public static IServiceCollection AddWebErpAuth(this IServiceCollection services, WebErpOptions options)
    {
        services.AddSingleton<LoginThrottle>();
        services.AddSingleton<CookieValidation>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "erp_auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = options.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(options.Session.IdleMinutes);
                o.SlidingExpiration = true;
                o.LoginPath = "/erp/login";
                o.LogoutPath = "/erp/logout";
                o.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = ctx => ctx.HttpContext.RequestServices.GetRequiredService<CookieValidation>().ValidateAsync(ctx),
                    // FE gọi bằng fetch (có X-ERP-Request) cần 401 để mở màn đăng nhập (chuẩn 3.2). Mở thẳng bằng trình duyệt thì
                    // chuyển sang trang đăng nhập.
                    OnRedirectToLogin = ctx =>
                    {
                        if (!IsFrontendCall(ctx.Request)) { ctx.Response.Redirect(ctx.RedirectUri); return Task.CompletedTask; }
                        return HtmlResults.WriteAsync(ctx.HttpContext, BasicScreens.Error("Chưa đăng nhập",
                            ErpMessage.Warning("Phiên làm việc đã hết. Vui lòng đăng nhập lại.")), StatusCodes.Status401Unauthorized);
                    },
                    OnRedirectToAccessDenied = ctx => HtmlResults.WriteAsync(ctx.HttpContext, BasicScreens.Error("Không có quyền",
                        ErpMessage.Error("Bạn không có quyền thực hiện chức năng này.")), StatusCodes.Status403Forbidden),
                };
            });
        services.AddAuthorization();
        services.AddAntiforgery(o =>
        {
            o.FormFieldName = AntiforgeryField;
            o.Cookie.Name = "erp_af";
            o.Cookie.SecurePolicy = options.RequireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        });
        return services;
    }

    public static bool IsFrontendCall(HttpRequest r) => r.Headers.ContainsKey("X-ERP-Request");
}
