using System.Text.RegularExpressions;
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
                new PrintUrls(self, p, t => u.PrintTemplate(f, p, t))));
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
        var name = file.EndsWith(".rpx", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        if (!TemplatePattern().IsMatch(name)) return NotFound();
        var t = await print.TemplateAsync(Request(ctx, o.Node!, new Dictionary<string, string>()), name, ctx.RequestAborted);
        if (t is null) return HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy mẫu", ErpMessage.Error($"Không có mẫu in {name}."));
        ctx.Response.Headers.CacheControl = "private, no-cache";
        return Results.Bytes(t.Content, t.ContentType + "; charset=utf-8",
            lastModified: t.FileDate is { } d ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Local)) : null);
    }
}
