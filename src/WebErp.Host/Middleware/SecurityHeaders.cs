namespace WebErp.Host.Middleware;

/// <summary>Header bảo mật cơ bản cho mọi response (FE và BE cùng origin).</summary>
public sealed class SecurityHeaders(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h.XContentTypeOptions = "nosniff";
        h.XFrameOptions = "SAMEORIGIN";
        h["Referrer-Policy"] = "same-origin";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        return next(ctx);
    }
}
