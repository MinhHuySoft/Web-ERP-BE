using System.Globalization;
using WebErp.Core.Messages;
using WebErp.Html;
using WebErp.Html.Screens;

namespace WebErp.Web;

/// <summary>Trả HTML theo chuẩn ERP-HTML (mục 3.2): kiểu nội dung, <c>X-ERP-Contract</c>, không cache dữ liệu cá nhân.</summary>
public static class HtmlResults
{
    public static IResult Page(string html, int status = StatusCodes.Status200OK, int? retryAfterSeconds = null) =>
        new ErpHtmlResult(html, status, retryAfterSeconds);

    public static IResult Error(int status, string title, params ErpMessage[] messages) =>
        Page(BasicScreens.Error(title, messages), status);

    /// <summary>303 See Other (Post/Redirect/Get, chuẩn 3.2).</summary>
    public static IResult SeeOther(string url) => new SeeOtherResult(url);

    public static Task WriteAsync(HttpContext ctx, string html, int status, int? retryAfterSeconds = null) =>
        new ErpHtmlResult(html, status, retryAfterSeconds).ExecuteAsync(ctx);

    /// <summary>URL tuyệt đối theo đường dẫn gốc của site (hỗ trợ site nằm trong thư mục con của IIS).</summary>
    public static string Url(this HttpContext ctx, string path) => (ctx.Request.PathBase.Value ?? "") + path;

    private sealed class ErpHtmlResult(string html, int status, int? retryAfterSeconds) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx)
        {
            var r = ctx.Response;
            r.StatusCode = status;
            r.ContentType = "text/html; charset=utf-8";
            r.Headers["X-ERP-Contract"] = HtmlPage.ContractVersion;
            r.Headers.CacheControl = "no-store";
            if (retryAfterSeconds is { } s) r.Headers.RetryAfter = s.ToString(CultureInfo.InvariantCulture);
            return r.WriteAsync(html, ctx.RequestAborted);
        }
    }

    private sealed class SeeOtherResult(string url) : IResult
    {
        public Task ExecuteAsync(HttpContext ctx)
        {
            ctx.Response.StatusCode = StatusCodes.Status303SeeOther;
            ctx.Response.Headers.Location = url;
            ctx.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        }
    }
}
