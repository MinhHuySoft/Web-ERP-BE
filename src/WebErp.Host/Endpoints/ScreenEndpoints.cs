using System.Text.RegularExpressions;
using WebErp.Core.Menu;
using WebErp.Core.Messages;
using WebErp.Host.Auth;
using WebErp.Host.Html;
using WebErp.Html.Screens;

namespace WebErp.Host.Endpoints;

/// <summary>
/// <c>/erp/screens/{FormName}</c>, <c>/erp/reports/{FormName}</c>. M1: chưa dựng màn nào, trả màn "chưa có trên web" (vẫn là
/// <c>screen</c> hợp lệ để FE mở thành tab). Chỉ mở được màn có trong menu của người dùng (QĐ-03, ngoại lệ bảo mật 4).
/// </summary>
public static partial class ScreenEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,100}$")]
    private static partial Regex FormNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{0,50}$")]
    private static partial Regex ParaPattern();

    public static void MapScreens(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/erp").RequireAuthorization();
        g.MapGet("/screens/{formName}", (HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time) =>
            OpenAsync(ctx, formName, para, false, menu, time));
        g.MapGet("/screens/{formName}/{**rest}", (HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time) =>
            OpenAsync(ctx, formName, para, false, menu, time));
        g.MapGet("/reports/{formName}", (HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time) =>
            OpenAsync(ctx, formName, para, true, menu, time));
    }

    private static async Task<IResult> OpenAsync(HttpContext ctx, string formName, string? para, bool report, MenuService menu, TimeProvider time)
    {
        if (SessionEndpoints.Guard(ctx, time) is { } stop) return stop;
        if (!FormNamePattern().IsMatch(formName) || !ParaPattern().IsMatch(para ?? ""))
            return HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy", ErpMessage.Error("Không có màn hình này."));

        var node = await menu.FindScreenAsync(ErpSession.From(ctx)!, formName, para, ctx.RequestAborted);
        if (node is null)
            return HtmlResults.Error(StatusCodes.Status403Forbidden, "Không có quyền", ErpMessage.Error("Bạn không có quyền mở màn hình này."));

        return HtmlResults.Page(BasicScreens.NotAvailable(node.Entry.FormName!, node.Entry.Para, report || node.Kind == MenuKind.Report, node.Entry.Caption));
    }
}
