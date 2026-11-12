using System.Text.Json;
using Microsoft.Extensions.Options;
using WebErp.Host.Html;
using WebErp.Html;

namespace WebErp.Host.Middleware;

/// <summary>
/// Trả <c>/config.js</c> cho FE: 4 điểm vào BE (chuẩn ERP-HTML 3.4) trỏ vào chính site này. Đặt trước middleware tệp tĩnh, nên
/// tệp <c>config.js</c> của repo FE (trỏ vào mock) không bao giờ được phục vụ ở máy thật.
/// </summary>
public sealed class FrontendConfig(RequestDelegate next, IOptions<WebErpOptions> options)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method)
            || !ctx.Request.Path.Equals("/config.js", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        var cfg = new Dictionary<string, object>
        {
            ["appName"] = options.Value.AppName,
            ["loginUrl"] = ctx.Url("/erp/login"),
            ["sessionUrl"] = ctx.Url("/erp/session"),
            ["menuUrl"] = ctx.Url("/erp/menu"),
            ["homeUrl"] = ctx.Url("/erp/home"),
            ["allowedOrigins"] = Array.Empty<string>(),
            ["contractVersion"] = HtmlPage.ContractVersion,
            ["defaultTheme"] = "ribbon-classic",
            ["maxLiveTabs"] = 12,
        };
        ctx.Response.ContentType = "application/javascript; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.WriteAsync("// Sinh bởi WebErp.Host — điểm vào BE thật (chuẩn ERP-HTML 3.4).\nwindow.ERP_CONFIG = "
                                      + JsonSerializer.Serialize(cfg) + ";\n", ctx.RequestAborted);
    }
}
