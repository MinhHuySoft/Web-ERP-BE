using Microsoft.AspNetCore.Antiforgery;
using WebErp.Core.Layout;
using WebErp.Core.Messages;
using WebErp.Core.Metadata;
using WebErp.Host.Auth;
using WebErp.Host.Html;
using WebErp.Html.Screens;
using WebErp.Web;

namespace WebErp.Host.Endpoints;

/// <summary>
/// Vẽ layout form (M4, chuẩn ERP-HTML 15.3) như Ctrl+F12 của WinForms:
/// <list type="bullet">
/// <item><c>GET /erp/screens/{FormName}/layout</c>: màn vẽ (screen CUSTOM + layout-designer).</item>
/// <item><c>GET /erp/screens/{FormName}/layout/data</c>: bố cục JSON <c>erp-form-layout/1</c>.</item>
/// <item><c>POST /erp/screens/{FormName}/layout/save</c>: lưu (<c>Json</c>, <c>Comment</c>) hoặc khôi phục (<c>Restore</c> = số phiên bản), có token.</item>
/// </list>
/// Như màn chứng từ: phiên còn hạn, màn có trong menu. Lưu cần quyền Admin trên form (ErpLayout kiểm).
/// </summary>
public static partial class ScreenEndpoints
{
    private static void MapLayout(RouteGroupBuilder g)
    {
        g.MapGet("/screens/{formName}/layout", LayoutDesignerAsync);
        g.MapGet("/screens/{formName}/layout/data", LayoutDataAsync);
        g.MapPost("/screens/{formName}/layout/save", LayoutSaveAsync).DisableAntiforgery();
    }

    private static async Task<IResult> LayoutDesignerAsync(HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time,
        IErpLayout layout, IAntiforgery af)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        var req = Request(ctx, o.Node!, new Dictionary<string, string>());
        var v = await layout.OpenAsync(req, ctx.RequestAborted);
        if (v.Status != ScreenStatus.Ok) return LayoutFallback(ctx, v, o.Node!);
        var f = o.Node!.Entry.FormName!;
        var p = string.IsNullOrEmpty(o.Node.Entry.Para) ? null : o.Node.Entry.Para;
        var u = ErpUrls.Of(ctx);
        var tokens = af.GetAndStoreTokens(ctx);
        return HtmlResults.Page(LayoutScreens.Designer(v, f, req.RuntimeName, o.Node.Entry.MenuId,
            new LayoutDesignerUrls(u.Layout(f, p), p, u.LayoutData(f, p), v.CanSave ? u.LayoutSave(f) : null), tokens.FormFieldName, tokens.RequestToken!));
    }

    private static async Task<IResult> LayoutDataAsync(HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time, IErpLayout layout)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        var v = await layout.OpenAsync(Request(ctx, o.Node!, new Dictionary<string, string>()), ctx.RequestAborted);
        if (v.Status != ScreenStatus.Ok || v.Json is null) return LayoutFallback(ctx, v, o.Node!);
        ctx.Response.Headers.CacheControl = "private, no-store";
        return Results.Content(v.Json, "application/json; charset=utf-8");
    }

    private static async Task<IResult> LayoutSaveAsync(HttpContext ctx, string formName, MenuService menu, TimeProvider time, IErpLayout layout, IAntiforgery af)
    {
        if (!ctx.Request.HasFormContentType)
            return HtmlResults.Error(StatusCodes.Status400BadRequest, "Không lưu được", ErpMessage.Error("Yêu cầu không hợp lệ."));
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        var para = form["para"].ToString();
        var o = await OpenAsync(ctx, formName, string.IsNullOrEmpty(para) ? null : para, menu, time);
        if (o.Stop is not null) return o.Stop;
        try
        {
            await af.ValidateRequestAsync(ctx);
        }
        catch (AntiforgeryValidationException)
        {
            return HtmlResults.Error(StatusCodes.Status400BadRequest, "Không lưu được", ErpMessage.Error("Trang vẽ layout đã hết hạn. Mở lại rồi lưu."));
        }
        var req = Request(ctx, o.Node!, new Dictionary<string, string>());
        if (!string.Equals(form["Form"].ToString(), req.RuntimeName, StringComparison.OrdinalIgnoreCase))
            return HtmlResults.Error(StatusCodes.Status422UnprocessableEntity, "Không lưu được", ErpMessage.Error("Bố cục không thuộc form này."));
        var restore = form["Restore"].ToString();
        var r = int.TryParse(restore, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var no) && no > 0
            ? await layout.RestoreAsync(req, no, form["Comment"].ToString(), ctx.RequestAborted)
            : await layout.SaveAsync(req, form["Json"].ToString(), form["Comment"].ToString(), ctx.RequestAborted);
        if (!r.Ok)
            return HtmlResults.Error(r.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity, "Không lưu được",
                [.. (r.Errors ?? ["Không lưu được bố cục."]).Select(e => ErpMessage.Error(e))]);
        var s = ErpSession.From(ctx);
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebErp.Layout").LogInformation(
            "{Action} bố cục form {Form} (phiên bản {Version}, {Changed} dòng SY_FrmCfg), công ty {Tenant}, người dùng {User}.",
            restore.Length > 0 ? "Khôi phục" : "Lưu", req.RuntimeName, r.VersionNo, r.Changed, s?.Tenant.Key, s?.UserName);
        return HtmlResults.Page(BasicScreens.Error(restore.Length > 0 ? "Đã khôi phục bố cục" : "Đã lưu bố cục",
            ErpMessage.Success(r.Changed == 0
                ? $"Bố cục không đổi (phiên bản {r.VersionNo})."
                : $"Đã lưu bố cục form {req.RuntimeName} (phiên bản {r.VersionNo}, {r.Changed} dòng cấu hình). Máy đang mở ERP WinForms cần mở lại ERP để thấy.")));
    }

    private static IResult LayoutFallback(HttpContext ctx, LayoutDesignView v, WebErp.Core.Menu.MenuNode node) => v.Status switch
    {
        ScreenStatus.NotFound => HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy form", ErpMessage.Error("Không tìm thấy cấu hình form.")),
        ScreenStatus.NotSupported => HtmlResults.Error(StatusCodes.Status404NotFound, "Không vẽ được layout", ErpMessage.Error(v.Note ?? "Form này không vẽ layout được.")),
        _ => Fallback(ctx, v.Status, node, v.Note),
    };
}
