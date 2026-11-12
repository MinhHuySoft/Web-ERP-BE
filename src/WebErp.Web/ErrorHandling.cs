using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using WebErp.Config;
using WebErp.Core.Errors;
using WebErp.Core.Messages;
using WebErp.Html.Screens;

namespace WebErp.Web;

/// <summary>
/// Lỗi chưa bắt → mã HTTP đúng chuẩn 3.2 + <c>messages</c>. Không bao giờ gửi chi tiết lỗi nội bộ (SQL, stack) cho trình duyệt;
/// chỉ gửi mã lỗi để tra nhật ký.
/// </summary>
public static class ErrorHandling
{
    public static async Task HandleAsync(HttpContext ctx)
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        var log = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebErp.Errors");
        var traceId = Activity.Current?.Id ?? ctx.TraceIdentifier;

        switch (ex)
        {
            case ErpUnavailableException u:
                log.LogWarning(u, "Không kết nối được DB công ty {Tenant}. Mã {TraceId}", u.TenantKey, traceId);
                await HtmlResults.WriteAsync(ctx, BasicScreens.Error("Mất kết nối dữ liệu",
                        ErpMessage.Error("Không kết nối được máy chủ dữ liệu của công ty. Vui lòng thử lại sau ít phút.")),
                    StatusCodes.Status503ServiceUnavailable, 30);
                return;
            case ConfigNotInitializedException c:
                log.LogError(c, "Chưa có tệp cấu hình. Mã {TraceId}", traceId);
                await HtmlResults.WriteAsync(ctx, BasicScreens.Error("Chưa cấu hình",
                    ErpMessage.Error("Hệ thống chưa được cấu hình. Vui lòng báo quản trị.")), StatusCodes.Status503ServiceUnavailable, 60);
                return;
            default:
                log.LogError(ex, "Lỗi không xử lý được. Mã {TraceId}", traceId);
                await HtmlResults.WriteAsync(ctx, BasicScreens.Error("Lỗi hệ thống",
                    ErpMessage.Error($"Có lỗi khi xử lý yêu cầu. Mã lỗi: {traceId}")), StatusCodes.Status500InternalServerError);
                return;
        }
    }
}
