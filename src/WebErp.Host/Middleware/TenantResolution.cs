using WebErp.Config;
using WebErp.Core.Messages;
using WebErp.Core.Tenancy;
using WebErp.Host.Html;
using WebErp.Html.Screens;
using WebErp.Web;

namespace WebErp.Host.Middleware;

/// <summary>
/// Host của request → các công ty phục vụ ở host đó (QĐ-07). Chỉ chạy cho <c>/erp/*</c>.
/// Host lạ thì 404, không bao giờ rơi sang công ty khác. Mọi công ty của host đang bảo trì thì 503 + Retry-After.
/// </summary>
public sealed class TenantResolution(RequestDelegate next, ITenantStore store, TimeProvider time, ILogger<TenantResolution> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/erp", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        var host = TenantNames.NormalizeHost(ctx.Request.Host.Host);
        IReadOnlyList<Tenant> found;
        try
        {
            found = await store.FindByHostAsync(host, ctx.RequestAborted);
        }
        catch (ConfigNotInitializedException ex)
        {
            log.LogError(ex, "Chưa có tệp cấu hình công ty.");
            await HtmlResults.WriteAsync(ctx, BasicScreens.Error("Chưa cấu hình",
                ErpMessage.Error("Hệ thống chưa được cấu hình. Vui lòng báo quản trị.")), StatusCodes.Status503ServiceUnavailable, 60);
            return;
        }

        var tenants = new HostTenants(host, found);
        if (tenants.Tenants.Count == 0)
        {
            log.LogWarning("Host {Host} chưa được cấu hình cho công ty nào.", host);
            await HtmlResults.WriteAsync(ctx, BasicScreens.Error("Không tìm thấy",
                ErpMessage.Error("Tên miền này chưa được cấu hình cho công ty nào.")), StatusCodes.Status404NotFound);
            return;
        }

        var now = time.GetUtcNow();
        if (tenants.Tenants.All(t => t.InMaintenance(now)))
        {
            var until = tenants.Tenants.Min(t => t.MaintenanceUntil!.Value);
            await HtmlResults.WriteAsync(ctx, BasicScreens.Error("Đang bảo trì",
                    ErpMessage.Warning("Hệ thống đang bảo trì. Vui lòng thử lại sau ít phút.")),
                StatusCodes.Status503ServiceUnavailable, RetryAfter(until, now));
            return;
        }

        ctx.Features.Set(tenants);
        await next(ctx);
    }

    internal static int RetryAfter(DateTimeOffset until, DateTimeOffset now) =>
        (int)Math.Clamp(Math.Ceiling((until - now).TotalSeconds), 30, 3600);
}
