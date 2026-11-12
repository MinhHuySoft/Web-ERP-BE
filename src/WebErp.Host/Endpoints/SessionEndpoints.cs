using System.Globalization;
using Microsoft.Extensions.Options;
using WebErp.Core.Messages;
using WebErp.Host.Auth;
using WebErp.Host.Html;
using WebErp.Host.Middleware;
using WebErp.Html.Roles;
using WebErp.Html.Screens;
using WebErp.Web;

namespace WebErp.Host.Endpoints;

/// <summary><c>/erp/session</c> (chuẩn 5.2), <c>/erp/menu</c> (5.3), <c>/erp/home</c> (trang chủ).</summary>
public static class SessionEndpoints
{
    public static void MapSession(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/erp").RequireAuthorization();
        g.MapGet("/session", Session);
        g.MapGet("/menu", Menu);
        g.MapGet("/home", Home);
    }

    private static IResult Session(HttpContext ctx, IOptions<WebErpOptions> options, TimeProvider time)
    {
        if (Guard(ctx, time) is { } stop) return stop;
        var s = ErpSession.From(ctx)!;
        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), FindZone(options.Value.TimeZone));
        var items = new List<SessionItem> { new("company", "Công ty", s.Tenant.Name) };
        if (!string.IsNullOrWhiteSpace(s.Tenant.TaxCode)) items.Add(new("tax-code", "MST", s.Tenant.TaxCode));
        if (!string.IsNullOrWhiteSpace(s.BranchId)) items.Add(new("branch", "Chi nhánh", s.BranchId, s.BranchId));
        // WinForms đặt kỳ = tháng hiện tại khi đăng nhập (tài liệu 12 §2.4). Đổi kỳ trên web làm sau.
        items.Add(new("period", "Kỳ", local.ToString("MM/yyyy", CultureInfo.InvariantCulture), local.ToString("yyyyMM", CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(s.Tenant.DatabaseLabel)) items.Add(new("database", "Dữ liệu", s.Tenant.DatabaseLabel));
        items.Add(new("user", "Người dùng", s.FullName, s.UserName));
        items.Add(new("language", "Ngôn ngữ", "Tiếng Việt", MenuService.Language));

        var urls = ErpUrls.Of(ctx);
        return HtmlResults.Page(SessionRenderer.Render(new SessionModel(items, [new SessionLink("logout", "Đăng xuất", urls.Logout)])));
    }

    private static async Task<IResult> Menu(HttpContext ctx, MenuService menu, TimeProvider time)
    {
        if (Guard(ctx, time) is { } stop) return stop;
        var tree = await menu.GetTreeAsync(ErpSession.From(ctx)!, ctx.RequestAborted);
        return HtmlResults.Page(MenuRenderer.Render(tree, ErpUrls.Of(ctx)));
    }

    private static IResult Home(HttpContext ctx, TimeProvider time)
    {
        if (Guard(ctx, time) is { } stop) return stop;
        var s = ErpSession.From(ctx)!;
        return HtmlResults.Page(BasicScreens.Home(s.FullName, s.Tenant.Name));
    }

    /// <summary>Chặn khi phiên không khớp host (đã kiểm ở cookie) hoặc công ty đang bảo trì.</summary>
    internal static IResult? Guard(HttpContext ctx, TimeProvider time)
    {
        var s = ErpSession.From(ctx);
        if (s is null)
            return HtmlResults.Error(StatusCodes.Status401Unauthorized, "Chưa đăng nhập", ErpMessage.Warning("Phiên làm việc đã hết. Vui lòng đăng nhập lại."));
        var now = time.GetUtcNow();
        if (s.Tenant.InMaintenance(now))
            return HtmlResults.Page(BasicScreens.Error("Đang bảo trì", ErpMessage.Warning("Dữ liệu của công ty đang bảo trì. Vui lòng thử lại sau ít phút.")),
                StatusCodes.Status503ServiceUnavailable, TenantResolution.RetryAfter(s.Tenant.MaintenanceUntil!.Value, now));
        return null;
    }

    private static TimeZoneInfo FindZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }
}
