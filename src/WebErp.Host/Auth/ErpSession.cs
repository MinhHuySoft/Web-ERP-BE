using System.Globalization;
using System.Security.Claims;
using WebErp.Core.Auth;
using WebErp.Core.Tenancy;

namespace WebErp.Host.Auth;

/// <summary>Các claim trong cookie tự chứa (QĐ-07 hệ quả 2). KHÔNG đặt quyền vào đây: quyền đọc lại từ DB.</summary>
public static class ErpClaims
{
    public const string Tenant = "erp:tenant";
    public const string IssuedAt = "erp:iat";
    public const string FullName = "erp:name";
    public const string Branch = "erp:branch";
    public const string Group = "erp:group";

    public static ClaimsPrincipal Create(Tenant tenant, ErpUser user, DateTimeOffset issuedAt, string scheme)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.UserName),
            new(Tenant, tenant.Key),
            new(IssuedAt, issuedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            new(FullName, user.FullName),
        };
        if (!string.IsNullOrWhiteSpace(user.BranchId)) claims.Add(new Claim(Branch, user.BranchId));
        if (!string.IsNullOrWhiteSpace(user.UserGroupId)) claims.Add(new Claim(Group, user.UserGroupId));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, scheme));
    }

    public static DateTimeOffset? IssuedAtOf(ClaimsPrincipal p) =>
        long.TryParse(p.FindFirstValue(IssuedAt), NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s) : null;
}

/// <summary>Phiên của request hiện tại: công ty (thuộc host đang gọi) + người dùng.</summary>
public sealed record ErpSession(Tenant Tenant, string UserName, string FullName, string? BranchId)
{
    public static ErpSession? From(HttpContext ctx)
    {
        var user = ctx.User;
        if (user.Identity?.IsAuthenticated != true) return null;
        var tenant = ctx.Features.Get<HostTenants>()?.Find(user.FindFirstValue(ErpClaims.Tenant));
        var name = user.FindFirstValue(ClaimTypes.Name);
        return tenant is null || string.IsNullOrEmpty(name) ? null
            : new ErpSession(tenant, name, user.FindFirstValue(ErpClaims.FullName) ?? name, user.FindFirstValue(ErpClaims.Branch));
    }
}
