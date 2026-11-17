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
/// Màn hình nghiệp vụ (hướng dẫn BE mục 3):
/// <list type="bullet">
/// <item><c>GET /erp/screens/{FormName}[?para=]</c>: danh sách dựng từ metadata (M2), lọc bằng query string.</item>
/// <item><c>GET /erp/screens/{FormName}/{key}</c>: xem một chứng từ (M3, <c>data-mode=view</c>).</item>
/// <item><c>GET /erp/screens/{FormName}/{key}/tabs/{T1}</c>: lưới tab chi tiết, nạp lười.</item>
/// <item><c>GET /erp/screens/{FormName}/lookups/{field}?q=&amp;p0=</c>: tra cứu F4 của một ô chứng từ (<c>SY_FrmDrdwTbl</c>).</item>
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
        g.MapGet("/screens/{formName}/lookups/{field}", LookupAsync);
        g.MapGet("/reports/{formName}", ReportAsync);
        MapPrint(g);
        MapLayout(g);
    }

    /// <summary>Báo cáo (M6): chưa có, trả màn "chưa có trên web".</summary>
    private static async Task<IResult> ReportAsync(HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        return o.Stop ?? NotAvailable(ctx, o.Node!, null);
    }

    private static async Task<IResult> ListAsync(HttpContext ctx, string formName, string? para, MenuService menu, TimeProvider time, IErpScreens screens)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        var v = await screens.OpenListAsync(Request(ctx, o.Node!, Query(ctx)), ctx.RequestAborted);
        return v.Status switch
        {
            ScreenStatus.Ok => HtmlResults.Page(MetaScreens.List(v, o.Node!.Entry.Caption, o.Node.Entry.MenuId, Urls(ctx, o.Node))),
            _ => Fallback(ctx, v.Status, o.Node!, v.Note),
        };
    }

    private static async Task<IResult> RecordAsync(HttpContext ctx, string formName, string key, string? para, MenuService menu, TimeProvider time,
        IErpScreens screens, IErpPrint print, IAntiforgery af)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        var request = Request(ctx, o.Node!, new Dictionary<string, string>());
        var v = await screens.OpenRecordAsync(request, Key(key), ctx.RequestAborted);
        if (v.Status != ScreenStatus.Ok) return Fallback(ctx, v.Status, o.Node!, v.Note);
        var tokens = af.GetAndStoreTokens(ctx);
        var urls = Urls(ctx, o.Node!);
        // Nút In (M6) khi form có báo cáo in <Report>Stp, như nút In của WinForms.
        if (await print.ReportOfAsync(request, ctx.RequestAborted) is not null)
        {
            var u = ErpUrls.Of(ctx);
            var f = o.Node!.Entry.FormName!;
            var p = urls.Para;
            urls = urls with { Print = k => u.Print(f, p, k) };
        }
        // Nút Vẽ layout (Ctrl+F12 của WinForms, chế độ quản trị): người có quyền Admin trên form.
        if (v.CanDesignLayout) urls = urls with { Layout = ErpUrls.Of(ctx).Layout(o.Node!.Entry.FormName!, urls.Para) };
        return HtmlResults.Page(MetaScreens.Record(v, o.Node!.Entry.Caption, o.Node.Entry.MenuId, urls, tokens.FormFieldName, tokens.RequestToken!));
    }

    private static async Task<IResult> DetailAsync(HttpContext ctx, string formName, string key, string slot, string? para, MenuService menu,
        TimeProvider time, IErpScreens screens)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        if (!SlotPattern().IsMatch(slot)) return NotFound();
        var v = await screens.OpenDetailAsync(Request(ctx, o.Node!, new Dictionary<string, string>()), Key(key), slot, ctx.RequestAborted);
        return v.Status == ScreenStatus.Ok ? HtmlResults.Page(MetaScreens.Detail(v, o.Node!.Entry.Caption)) : Fallback(ctx, v.Status, o.Node!, v.Note);
    }

    private static async Task<IResult> LookupAsync(HttpContext ctx, string formName, string field, string? para, string? q, MenuService menu,
        TimeProvider time, IErpScreens screens)
    {
        var o = await OpenAsync(ctx, formName, para, menu, time);
        if (o.Stop is not null) return o.Stop;
        if (!FormNamePattern().IsMatch(field)) return NotFound();
        var paras = Enumerable.Range(0, 10).Select(i => ctx.Request.Query[$"p{i}"].ToString()).ToList();
        while (paras.Count > 0 && paras[^1].Length == 0 && !ctx.Request.Query.ContainsKey($"p{paras.Count - 1}")) paras.RemoveAt(paras.Count - 1);
        var v = await screens.LookupAsync(Request(ctx, o.Node!, new Dictionary<string, string>()), field, (q ?? "")[..Math.Min((q ?? "").Length, 100)], paras,
            ctx.RequestAborted);
        return v.Status switch
        {
            ScreenStatus.Ok => HtmlResults.Page(MetaScreens.Lookup(v, o.Node!.Entry.Caption)),
            ScreenStatus.Forbidden => HtmlResults.Error(StatusCodes.Status403Forbidden, "Không có quyền", ErpMessage.Error("Bạn không có quyền tra cứu ở màn hình này.")),
            ScreenStatus.ConfigError => LogConfigError(ctx, o.Node!, v.Note) ?? HtmlResults.Error(StatusCodes.Status422UnprocessableEntity, "Không tra cứu được",
                ErpMessage.Error($"Nguồn tra cứu của ô này có phần web chưa hỗ trợ. Mã lỗi: {ctx.TraceIdentifier}")),
            _ => HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy", ErpMessage.Error("Ô này không có tra cứu trên web.")),
        };
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
        // Trang n: giữ nguyên bộ lọc đang áp, chỉ thay "page".
        var keep = ctx.Request.Query.Where(x => !x.Key.Equals("page", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Value.Select(v => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(v ?? "")}")).ToList();
        return new ScreenUrls(self, para, k => u.Record(f, para, k), (k, slot) => u.Detail(f, para, k, slot),
            self + ctx.Request.QueryString.Value,
            Page: n => self + "?" + string.Join("&", keep.Append($"page={n.ToString(System.Globalization.CultureInfo.InvariantCulture)}")),
            Lookup: (field, paras) => u.Lookup(f, para, field, paras));
    }

    /// <summary>
    /// Ghi cảnh báo khi cấu hình form có SQL web không chạy được, để đếm và sửa được qua Event Log (trang vẫn trả 200 để FE mở thành tab).
    /// Ghi chú do tầng Erp dựng (<c>SqlErrorText</c>): lỗi tên đối tượng ghi nguyên văn (cột / bảng nào sai), lỗi có thể chứa dữ liệu
    /// khách chỉ có mã; kèm slot và bảng chính. Không bao giờ có tham số. Trình duyệt chỉ nhận mã lỗi (TraceId).
    /// </summary>
    private static IResult? LogConfigError(HttpContext ctx, MenuNode node, string? note)
    {
        var s = ErpSession.From(ctx);
        Log(ctx).LogWarning(
            "Màn {Form}{Para} của công ty {Tenant} lỗi cấu hình: {Reason} (TraceId {TraceId}, đường dẫn {Path}).",
            node.Entry.FormName, string.IsNullOrEmpty(node.Entry.Para) ? "" : "_" + node.Entry.Para, s?.Tenant.Key, note ?? "", ctx.TraceIdentifier,
            ctx.Request.Path.Value);
        return null;
    }

    /// <summary>
    /// Ghi log mỗi lần mở màn chưa có trên web (đếm nhu cầu để xếp ưu tiên làm form). <paramref name="mismatch"/>: bản web có nhưng DB
    /// của công ty thiếu bảng / cột (ghi Warning kèm tên cột).
    /// </summary>
    private static void LogNotAvailable(HttpContext ctx, MenuNode node, string? mismatch)
    {
        var s = ErpSession.From(ctx);
        var para = string.IsNullOrEmpty(node.Entry.Para) ? "" : "_" + node.Entry.Para;
        if (mismatch is null)
            Log(ctx).LogInformation("Mở màn chưa có trên web: {Form}{Para} (menu {MenuId}), công ty {Tenant}.", node.Entry.FormName, para, node.Entry.MenuId, s?.Tenant.Key);
        else
            Log(ctx).LogWarning("Màn {Form}{Para} có bản web nhưng DB công ty {Tenant} không khớp, chưa mở được: {Missing}.", node.Entry.FormName, para, s?.Tenant.Key, mismatch);
    }

    private static ILogger Log(HttpContext ctx) => ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebErp.Screens");

    private static IResult NotAvailable(HttpContext ctx, MenuNode node, string? mismatch)
    {
        LogNotAvailable(ctx, node, mismatch);
        return HtmlResults.Page(BasicScreens.NotAvailable(node.Entry.FormName!, node.Entry.Para, node.Kind == MenuKind.Report, node.Entry.Caption,
            forThisCompany: mismatch is not null));
    }

    private static IResult Fallback(HttpContext ctx, ScreenStatus status, MenuNode node, string? note) => status switch
    {
        ScreenStatus.NotFound => HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy", ErpMessage.Error("Không tìm thấy chứng từ này, hoặc bạn không được xem.")),
        ScreenStatus.Forbidden => HtmlResults.Error(StatusCodes.Status403Forbidden, "Không có quyền", ErpMessage.Error("Bạn không có quyền mở màn hình này.")),
        ScreenStatus.ConfigError => LogConfigError(ctx, node, note)
            ?? HtmlResults.Page(MetaScreens.ConfigError(node.Entry.FormName!, node.Entry.Para, node.Entry.Caption, ctx.TraceIdentifier)),
        ScreenStatus.SchemaMismatch => NotAvailable(ctx, node, note ?? ""),
        _ => NotAvailable(ctx, node, null),
    };

    private static IResult NotFound() =>
        HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy", ErpMessage.Error("Không có màn hình này."));
}
