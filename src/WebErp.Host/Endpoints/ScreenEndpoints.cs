using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Antiforgery;
using WebErp.Core.Menu;
using WebErp.Core.Messages;
using WebErp.Core.Metadata;
using WebErp.Host.Auth;
using WebErp.Host.Html;
using WebErp.Html.Screens;
using WebErp.Web;

namespace WebErp.Host.Endpoints;

/// <summary>
/// Màn hình nghiệp vụ (hướng dẫn BE mục 3):
/// <list type="bullet">
/// <item><c>GET /erp/screens/{FormName}[?para=]</c>: danh sách dựng từ metadata (M2), lọc bằng query string.</item>
/// <item><c>GET /erp/screens/{FormName}/{key}</c>: xem một chứng từ (M3, <c>data-mode=view</c>).</item>
/// <item><c>GET /erp/screens/{FormName}/{key}/tabs/{T1}</c>: lưới tab chi tiết, nạp lười.</item>
/// </list>
/// Chỉ mở được màn có trong menu của người dùng (QĐ-03, ngoại lệ bảo mật 4). Form viết tay / không có metadata / báo cáo:
/// màn "chưa có trên web".
/// </summary>
public static partial class ScreenEndpoints
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,100}$")]
    private static partial Regex FormNamePattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{0,50}$")]
    private static partial Regex ParaPattern();

    [GeneratedRegex("^T[0-9A-J]$")]
    private static partial Regex SlotPattern();

    public static void MapScreens(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/erp").RequireAuthorization();
        g.MapGet("/screens/{formName}", ListAsync);
        g.MapGet("/screens/{formName}/{key}", RecordAsync);
        g.MapGet("/screens/{formName}/{key}/tabs/{slot}", DetailAsync);
        g.MapGet("/reports/{formName}", ReportAsync);
    }

    /// <summary>Báo cáo (M6): chưa có, trả màn "chưa có trên web".</summary>
    private static async Task<IResult> ReportAsync(HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        return o.Stop ?? HtmlResults.Page(BasicScreens.NotAvailable(o.Node!.Entry.FormName!, o.Node.Entry.Para, true, o.Node.Entry.Caption));
    }

    private static async Task<IResult> ListAsync(HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time, IErpScreens screens)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        var v = await screens.OpenListAsync(Request(ctx, o.Node!, Query(ctx)), ctx.RequestAborted);
        return v.Status switch
        {
            ScreenStatus.Ok => HtmlResults.Page(MetaScreens.List(v, o.Node!.Entry.Caption, o.Node.Entry.MenuId, Urls(ctx, o.Node))),
            _ => Fallback(v.Status, o.Node!, v.Note),
        };
    }

    private static async Task<IResult> RecordAsync(HttpContext ctx, string formName, string key, string? para, MenuService menu, TimeProvider time,
        IErpScreens screens, IAntiforgery af)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        var v = await screens.OpenRecordAsync(Request(ctx, o.Node!, new Dictionary<string, string>()), Key(key), ctx.RequestAborted);
        if (v.Status != ScreenStatus.Ok) return Fallback(v.Status, o.Node!, v.Note);
        var tokens = af.GetAndStoreTokens(ctx);
        return HtmlResults.Page(MetaScreens.Record(v, o.Node!.Entry.Caption, o.Node.Entry.MenuId, Urls(ctx, o.Node), tokens.FormFieldName, tokens.RequestToken!));
    }

    private static async Task<IResult> DetailAsync(HttpContext ctx, string formName, string key, string slot, string? para, MenuService menu,
        TimeProvider time, IErpScreens screens)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        if (!SlotPattern().IsMatch(slot)) return NotFound();
        var v = await screens.OpenDetailAsync(Request(ctx, o.Node!, new Dictionary<string, string>()), Key(key), slot, ctx.RequestAborted);
        return v.Status == ScreenStatus.Ok ? HtmlResults.Page(MetaScreens.Detail(v, o.Node!.Entry.Caption)) : Fallback(v.Status, o.Node!, v.Note);
    }

    /// <summary>Phiên còn hạn, tên hợp lệ, màn có trong menu của người dùng.</summary>
    private static async Task<(MenuNode? Node, IResult? Stop)> OpenAsync(HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time)
    {
        if (SessionEndpoints.Guard(ctx, time) is { } stop) return (null, stop);
        if (!FormNamePattern().IsMatch(formName) || !ParaPattern().IsMatch(para ?? "")) return (null, NotFound());
        var node = await menu.FindScreenAsync(ErpSession.From(ctx)!, formName, para, ctx.RequestAborted);
        return node is null
            ? (null, HtmlResults.Error(StatusCodes.Status403Forbidden, "Không có quyền", ErpMessage.Error("Bạn không có quyền mở màn hình này.")))
            : (node, null);
    }

    private static ScreenRequest Request(HttpContext ctx, MenuNode node, IReadOnlyDictionary<string, string> query)
    {
        var s = ErpSession.From(ctx)!;
        return new ScreenRequest(s.Tenant, s.UserName, s.BranchId, node.Entry.FormName!, string.IsNullOrEmpty(node.Entry.Para) ? null : node.Entry.Para, query);
    }

    private static Dictionary<string, string> Query(HttpContext ctx) =>
        ctx.Request.Query.Where(q => q.Key != "para").ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);

    private static string Key(string raw) => Uri.UnescapeDataString(raw);

    private static ScreenUrls Urls(HttpContext ctx, MenuNode node)
    {
        var u = ErpUrls.Of(ctx);
        var f = node.Entry.FormName!;
        var para = string.IsNullOrEmpty(node.Entry.Para) ? null : node.Entry.Para;
        var self = $"{ctx.Request.PathBase}/erp/screens/{Uri.EscapeDataString(f)}";
        return new ScreenUrls(self, para, k => u.Record(f, para, k), (k, slot) => u.Detail(f, para, k, slot),
            self + ctx.Request.QueryString.Value);
    }

    private static IResult Fallback(ScreenStatus status, MenuNode node, string? note) => status switch
    {
        ScreenStatus.NotFound => HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy", ErpMessage.Error("Không tìm thấy chứng từ này, hoặc bạn không được xem.")),
        ScreenStatus.Forbidden => HtmlResults.Error(StatusCodes.Status403Forbidden, "Không có quyền", ErpMessage.Error("Bạn không có quyền mở màn hình này.")),
        ScreenStatus.ConfigError => HtmlResults.Page(MetaScreens.ConfigError(node.Entry.FormName!, node.Entry.Para, node.Entry.Caption, note ?? "")),
        _ => HtmlResults.Page(BasicScreens.NotAvailable(node.Entry.FormName!, node.Entry.Para, node.Kind == MenuKind.Report, node.Entry.Caption)),
    };

    private static IResult NotFound() =>
        HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy", ErpMessage.Error("Không có màn hình này."));
}
