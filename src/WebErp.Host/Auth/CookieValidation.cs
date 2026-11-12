using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using WebErp.Core.Auth;
using WebErp.Core.Errors;
using WebErp.Core.Tenancy;

namespace WebErp.Host.Auth;

/// <summary>
/// Kiểm cookie mỗi request (QĐ-07 hệ quả 2):
/// <list type="bullet">
/// <item>công ty trong cookie phải thuộc host đang gọi;</item>
/// <item>chưa quá hạn tối đa tính từ lúc đăng nhập;</item>
/// <item>cấp sau mốc "đăng xuất cả công ty";</item>
/// <item>user còn hoạt động trong <c>SY_User</c> (cache vài chục giây).</item>
/// </list>
/// Mất kết nối DB thì KHÔNG đăng xuất người dùng; endpoint sẽ trả 503.
/// </summary>
public sealed class CookieValidation(IErpUsers users, IMemoryCache cache, TimeProvider time, IOptions<WebErpOptions> options, ILogger<CookieValidation> log)
{
    public async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var principal = ctx.Principal;
        var host = ctx.HttpContext.Features.Get<HostTenants>();
        if (principal is null || host is null) return; // ngoài /erp (vd /health): không dùng phiên

        var reason = await CheckAsync(principal, host, ctx.HttpContext.RequestAborted);
        if (reason is null) return;
        log.LogInformation("Từ chối cookie của {User}: {Reason}", principal.FindFirstValue(ClaimTypes.Name), reason);
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    internal async Task<string?> CheckAsync(ClaimsPrincipal principal, HostTenants host, CancellationToken ct)
    {
        var tenant = host.Find(principal.FindFirstValue(ErpClaims.Tenant));
        if (tenant is null) return "công ty không thuộc tên miền này";
        var userName = principal.FindFirstValue(ClaimTypes.Name);
        if (string.IsNullOrEmpty(userName)) return "thiếu tên người dùng";

        var issued = ErpClaims.IssuedAtOf(principal);
        var now = time.GetUtcNow();
        if (issued is null || now - issued.Value > TimeSpan.FromHours(options.Value.Session.MaxHours)) return "quá hạn tối đa";
        if (tenant.LogoutBefore is { } mark && issued.Value < mark) return "công ty đã đăng xuất mọi người";

        var key = $"user-enabled:{tenant.Key}:{userName}";
        if (cache.TryGetValue(key, out bool enabled)) return enabled ? null : "người dùng đã bị khóa";
        try
        {
            enabled = await users.IsEnabledAsync(tenant, userName, ct);
        }
        catch (ErpUnavailableException)
        {
            return null;
        }
        cache.Set(key, enabled, TimeSpan.FromSeconds(options.Value.Session.UserCheckSeconds));
        return enabled ? null : "người dùng đã bị khóa";
    }
}
