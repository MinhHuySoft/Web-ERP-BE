using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using WebErp.Html;

namespace WebErp.Web;

/// <summary>Điểm vào BE mà FE dùng (chuẩn ERP-HTML 3.4). Host: <c>/erp/*</c>; trang quản trị: <c>/admin/*</c>.</summary>
public sealed record FrontendSettings(string AppName, string LoginPath, string SessionPath, string MenuPath, string HomePath);

/// <summary>
/// Trả <c>/config.js</c> cho FE: 4 điểm vào BE trỏ vào chính site này. Đặt trước middleware tệp tĩnh, nên tệp <c>config.js</c>
/// của repo FE (trỏ vào mock) không bao giờ được phục vụ ở máy thật.
/// </summary>
public sealed class FrontendConfig(RequestDelegate next, FrontendSettings settings)
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
            ["appName"] = settings.AppName,
            ["loginUrl"] = ctx.Url(settings.LoginPath),
            ["sessionUrl"] = ctx.Url(settings.SessionPath),
            ["menuUrl"] = ctx.Url(settings.MenuPath),
            ["homeUrl"] = ctx.Url(settings.HomePath),
            ["allowedOrigins"] = Array.Empty<string>(),
            ["contractVersion"] = HtmlPage.ContractVersion,
            ["defaultTheme"] = "ribbon-classic",
            ["maxLiveTabs"] = 12,
        };
        ctx.Response.ContentType = "application/javascript; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.WriteAsync("// Sinh bởi BE Web-ERP — điểm vào BE thật (chuẩn ERP-HTML 3.4).\nwindow.ERP_CONFIG = "
                                      + JsonSerializer.Serialize(cfg) + ";\n", ctx.RequestAborted);
    }
}

public static class FrontendHosting
{
    /// <summary>
    /// <c>/config.js</c> rồi tệp tĩnh FE cùng origin. <paramref name="frontendDir"/> trống thì dùng <c>wwwroot</c> (bản đóng gói).
    /// Trả về thư mục thật đang dùng, hoặc null khi không có FE.
    /// </summary>
    public static string? UseWebErpFrontend(this WebApplication app, FrontendSettings settings, string? frontendDir)
    {
        app.UseMiddleware<FrontendConfig>(settings);
        var dir = string.IsNullOrWhiteSpace(frontendDir)
            ? app.Environment.WebRootPath
            : Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, frontendDir));
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;
        var fe = new PhysicalFileProvider(dir);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fe });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = fe });
        return dir;
    }
}
