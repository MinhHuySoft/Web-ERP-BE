using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using WebErp.Core.Menu;
using WebErp.Core.Messages;
using WebErp.Core.Metadata;
using WebErp.Core.Print;
using WebErp.Host.Auth;
using WebErp.Host.Html;
using WebErp.Html.Screens;
using WebErp.Web;

namespace WebErp.Host.Endpoints;

/// <summary>
/// In theo mẫu (M6, chuẩn ERP-HTML 15.1):
/// <list type="bullet">
/// <item><c>GET /erp/screens/{FormName}/print?DocumentID=a;b&amp;Template=&amp;UInput1=</c>: màn in (screen REPORT + print-document).</item>
/// <item><c>GET /erp/screens/{FormName}/print/templates/{Template}.rpx</c>: tệp mẫu cho FE dựng (chỉ mẫu thuộc báo cáo của form).</item>
/// </list>
/// Như màn chứng từ: phiên còn hạn, màn có trong menu; từng chứng từ phải xem được (ErpPrint kiểm).
/// </summary>
public static partial class ScreenEndpoints
{
    [GeneratedRegex(@"^[A-Za-z0-9_()\-. ]{1,120}$")]
    private static partial Regex TemplatePattern();

    private static void MapPrint(RouteGroupBuilder g)
    {
        g.MapGet("/screens/{formName}/print", PrintAsync);
        g.MapGet("/screens/{formName}/print/templates/{file}", PrintTemplateAsync);
        g.MapGet("/screens/{formName}/print/designer", DesignerAsync);
        g.MapPost("/screens/{formName}/print/designer/save", SaveTemplateAsync).DisableAntiforgery();
    }

    private static async Task<IResult> PrintAsync(HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time, IErpPrint print)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        var q = ctx.Request.Query;
        var ids = q["DocumentID"].ToString().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.Length > 100 ? x[..100] : x).ToList();
        var template = q["Template"].ToString();
        var inputs = Enumerable.Range(1, 9).Select(i => $"UInput{i}").Where(n => q.ContainsKey(n))
            .ToDictionary(n => n, n => q[n].ToString(), StringComparer.OrdinalIgnoreCase);
        var v = await print.PrepareAsync(Request(ctx, o.Node!, new Dictionary<string, string>()), ids,
            TemplatePattern().IsMatch(template) ? template : null, inputs, ctx.RequestAborted);
        if (v.Status == ScreenStatus.Ok)
        {
            var u = ErpUrls.Of(ctx);
            var f = o.Node!.Entry.FormName!;
            var p = string.IsNullOrEmpty(o.Node.Entry.Para) ? null : o.Node.Entry.Para;
            var self = $"{ctx.Request.PathBase}/erp/screens/{Uri.EscapeDataString(f)}/print";
            return HtmlResults.Page(PrintScreens.Print(v, f, o.Node.Entry.Caption, o.Node.Entry.MenuId,
                new PrintUrls(self, p, t => u.PrintTemplate(f, p, t, v.TemplateFormat, v.TemplateVersion),
                    v.CanDesign ? t => u.PrintDesigner(f, p, t, ids.FirstOrDefault()) : null)));
        }
        return v.Status switch
        {
            ScreenStatus.NotFound => HtmlResults.Error(StatusCodes.Status404NotFound, "Không in được",
                ErpMessage.Error(v.Note ?? "Không tìm thấy chứng từ cần in, hoặc bạn không được xem.")),
            ScreenStatus.NotSupported => HtmlResults.Error(StatusCodes.Status404NotFound, "Không có mẫu in",
                ErpMessage.Error("Màn này không có báo cáo in (SP <Report>Stp) trên DB của công ty.")),
            _ => Fallback(ctx, v.Status, o.Node!, v.Note),
        };
    }

    private static async Task<IResult> PrintTemplateAsync(HttpContext ctx, string formName, string file, string? para, MenuService menu, TimeProvider time,
        IErpPrint print)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        var name = file.EndsWith(".rpx", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? file[..file.LastIndexOf('.')] : file;
        if (!TemplatePattern().IsMatch(name)) return NotFound();
        var t = await print.TemplateAsync(Request(ctx, o.Node!, new Dictionary<string, string>()), name, ctx.RequestAborted);
        if (t is null) return HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy mẫu", ErpMessage.Error($"Không có mẫu in {name}."));
        ctx.Response.Headers.CacheControl = "private, no-cache";
        return Results.Bytes(t.Content, t.ContentType + "; charset=utf-8",
            lastModified: t.FileDate is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Local)) : null);
    }

    private static async Task<IResult> DesignerAsync(HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time, IErpPrint print,
        IAntiforgery af)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        var q = ctx.Request.Query;
        var template = q["Template"].ToString();
        var doc = q["DocumentID"].ToString().Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (doc is { Length: > 100 }) doc = doc[..100];
        var v = await print.DesignAsync(Request(ctx, o.Node!, new Dictionary<string, string>()), TemplatePattern().IsMatch(template) ? template : null,
            ctx.RequestAborted);
        if (v.Status != ScreenStatus.Ok)
            return v.Status switch
            {
                ScreenStatus.NotSupported => HtmlResults.Error(StatusCodes.Status404NotFound, "Không có mẫu in",
                    ErpMessage.Error("Màn này không có báo cáo in (SP <Report>Stp) trên DB của công ty.")),
                ScreenStatus.NotFound => HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy mẫu", ErpMessage.Error(v.Note ?? "Không tìm thấy mẫu in.")),
                _ => Fallback(ctx, v.Status, o.Node!, v.Note),
            };
        var u = ErpUrls.Of(ctx);
        var f = o.Node!.Entry.FormName!;
        var p = string.IsNullOrEmpty(o.Node.Entry.Para) ? null : o.Node.Entry.Para;
        var tokens = af.GetAndStoreTokens(ctx);
        var urls = new PrintDesignerUrls($"{ctx.Request.PathBase}/erp/screens/{Uri.EscapeDataString(f)}/print/designer", p,
            u.PrintTemplate(f, p, v.Template, v.TemplateFormat, v.TemplateVersion),
            doc is null ? null : u.Print(f, p, doc, v.Template), v.CanDesign ? u.PrintDesignerSave(f) : null);
        return HtmlResults.Page(PrintScreens.Designer(v, f, o.Node.Entry.MenuId, urls, doc, tokens.FormFieldName, tokens.RequestToken!));
    }

    /// <summary>Lưu mẫu (chuẩn 15.2): multipart <c>Template</c>, <c>Json</c>, <c>Comment</c> + token; trả <c>messages</c>.</summary>
    private static async Task<IResult> SaveTemplateAsync(HttpContext ctx, string formName, MenuService menu, TimeProvider time, IErpPrint print,
        IAntiforgery af)
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
            return HtmlResults.Error(StatusCodes.Status400BadRequest, "Không lưu được", ErpMessage.Error("Trang thiết kế đã hết hạn. Mở lại màn thiết kế rồi lưu."));
        }
        var template = form["Template"].ToString();
        if (!TemplatePattern().IsMatch(template))
            return HtmlResults.Error(StatusCodes.Status422UnprocessableEntity, "Không lưu được", ErpMessage.Error("Tên mẫu không hợp lệ."));
        var r = await print.SaveTemplateAsync(Request(ctx, o.Node!, new Dictionary<string, string>()), template, form["Json"].ToString(),
            form["Comment"].ToString(), ctx.RequestAborted);
        if (!r.Ok)
            return HtmlResults.Error(StatusCodes.Status422UnprocessableEntity, "Không lưu được",
                [.. (r.Errors ?? ["Không lưu được mẫu."]).Select(e => ErpMessage.Error(e))]);
        var s = ErpSession.From(ctx);
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebErp.Print").LogInformation(
            "Sửa mẫu in {Template} (phiên bản {Version}) của form {Form}, công ty {Tenant}, người dùng {User}.",
            template, r.VersionNo, formName, s?.Tenant.Key, s?.UserName);
        return HtmlResults.Page(BasicScreens.Error("Đã lưu mẫu in",
            ErpMessage.Success($"Đã lưu mẫu in {template} (phiên bản {r.VersionNo}). Bản in trên web dùng mẫu này từ bây giờ.")));
    }
}
