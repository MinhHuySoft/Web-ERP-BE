using System.Net;
using Microsoft.Extensions.Options;
using WebErp.Core.Messages;
using WebErp.Core.Tenancy;
using WebErp.Html.Screens;
using WebErp.Web;

namespace WebErp.Admin.Web;

/// <summary>
/// Cổng của trang quản trị, chạy trước mọi thứ (kể cả tệp tĩnh FE):
/// <list type="bullet">
/// <item>từ chính VPS (loopback): luôn cho qua, kể cả HTTP (để thiết lập tài khoản đầu tiên);</item>
/// <item>từ xa: chỉ tên miền khai báo ở <c>WebErp:Admin:Hosts</c>, và bắt buộc HTTPS.</item>
/// </list>
/// Nhờ vậy trang quản trị không bao giờ trả lời trên domain của khách, dù binding IIS bị cấu hình nhầm.
/// </summary>
public sealed class AdminGate(RequestDelegate next, IOptions<AdminOptions> options, ILogger<AdminGate> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (IsLocal(ctx))
        {
            await next(ctx);
            return;
        }

        var host = TenantNames.NormalizeHost(ctx.Request.Host.Host);
        if (!options.Value.Hosts.Any(h => string.Equals(TenantNames.NormalizeHost(h), host, StringComparison.OrdinalIgnoreCase)))
        {
            log.LogWarning("Từ chối mở trang quản trị qua host {Host} từ {Ip}.", host, ctx.Connection.RemoteIpAddress);
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!ctx.Request.IsHttps)
        {
            await HtmlResults.WriteAsync(ctx, BasicScreens.Error("Cần HTTPS", ErpMessage.Error("Trang quản trị chỉ mở qua HTTPS.")), StatusCodes.Status403Forbidden);
            return;
        }
        await next(ctx);
    }

    /// <summary>Request từ chính máy chủ (TestServer không có IP: coi là cục bộ).</summary>
    public static bool IsLocal(HttpContext ctx) => ctx.Connection.RemoteIpAddress is not { } ip || IPAddress.IsLoopback(ip);
}
