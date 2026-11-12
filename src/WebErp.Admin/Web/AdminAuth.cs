using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using WebErp.Config;
using WebErp.Core.Messages;
using WebErp.Html.Screens;
using WebErp.Web;

namespace WebErp.Admin.Web;

/// <summary>Phiên quản trị: cookie riêng (<c>erp_admin</c>), SameSite=Strict, hết hạn ngắn.</summary>
public static class AdminAuth
{
    public const string AntiforgeryField = "__RequestVerificationToken";
    private const string IssuedAt = "erp:iat";

    public static IServiceCollection AddAdminAuth(this IServiceCollection services, AdminOptions options)
    {
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "erp_admin";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Strict;
                // Từ xa AdminGate đã bắt HTTPS; trên chính VPS cho phép HTTP để thiết lập lần đầu.
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromMinutes(options.IdleMinutes);
                o.SlidingExpiration = true;
                o.LoginPath = "/admin/login";
                o.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = Validate,
                    OnRedirectToLogin = ctx =>
                    {
                        if (!ctx.Request.Headers.ContainsKey("X-ERP-Request")) { ctx.Response.Redirect(ctx.RedirectUri); return Task.CompletedTask; }
                        return HtmlResults.WriteAsync(ctx.HttpContext, BasicScreens.Error("Chưa đăng nhập",
                            ErpMessage.Warning("Phiên quản trị đã hết. Vui lòng đăng nhập lại.")), StatusCodes.Status401Unauthorized);
                    },
                };
            });
        services.AddAuthorization();
        services.AddAntiforgery(o =>
        {
            o.FormFieldName = AntiforgeryField;
            o.Cookie.Name = "erp_admin_af";
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        return services;
    }

    public static Task SignInAsync(HttpContext ctx, string userName, TimeProvider time) =>
        ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, userName), new Claim(IssuedAt, time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))],
            CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties { IsPersistent = false });

    public static string UserName(HttpContext ctx) => ctx.User.FindFirstValue(ClaimTypes.Name) ?? "";

    /// <summary>Người làm thao tác, ghi vào nhật ký quản trị.</summary>
    public static string Actor(HttpContext ctx) => $"{UserName(ctx)} (web, {ctx.Connection.RemoteIpAddress})";

    /// <summary>Token chống giả mạo cho form. Trả cặp (tên ô, giá trị).</summary>
    public static (string Field, string Token) Tokens(HttpContext ctx)
    {
        var t = ctx.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(ctx);
        return (t.FormFieldName, t.RequestToken ?? "");
    }

    public static async Task<bool> ValidAntiforgeryAsync(HttpContext ctx)
    {
        try
        {
            await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    /// <summary>Tài khoản còn tồn tại và chưa quá hạn tối đa; không thì đăng xuất.</summary>
    private static async Task Validate(CookieValidatePrincipalContext ctx)
    {
        var sp = ctx.HttpContext.RequestServices;
        var options = sp.GetRequiredService<IOptions<AdminOptions>>().Value;
        var time = sp.GetRequiredService<TimeProvider>();
        var name = ctx.Principal?.FindFirstValue(ClaimTypes.Name);
        var issued = long.TryParse(ctx.Principal?.FindFirstValue(IssuedAt), NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s) : (DateTimeOffset?)null;
        var ok = name is not null && issued is not null && time.GetUtcNow() - issued.Value <= TimeSpan.FromHours(options.MaxHours)
                 && sp.GetRequiredService<AdminAccountStore>().Exists(name);
        if (ok) return;
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
