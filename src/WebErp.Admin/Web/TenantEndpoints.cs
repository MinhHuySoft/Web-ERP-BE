using System.Globalization;
using Microsoft.Extensions.Options;
using WebErp.Config;
using WebErp.Core.Auth;
using WebErp.Core.Messages;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Diagnostics;
using WebErp.Html.Roles;
using WebErp.Html.Screens;
using WebErp.Web;

namespace WebErp.Admin.Web;

/// <summary>
/// Công ty: danh sách, thêm, sửa, thử kết nối, đổi chuỗi kết nối, bảo trì, đăng xuất mọi người.
/// Thao tác nguy hiểm (đặt chuỗi kết nối, đăng xuất mọi người) phải nhập lại mã TOTP (QĐ-07 hệ quả 7).
/// </summary>
public static class TenantEndpoints
{
    public static void MapTenants(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/tenants").RequireAuthorization();
        g.MapGet("", (HttpContext ctx, Deps d) => HtmlResults.Page(AdminScreens.TenantList(ctx, d.Writer(ctx).List(), d.Time)));
        g.MapGet("/new", (HttpContext ctx, Deps d) => HtmlResults.Page(AdminScreens.TenantForm(ctx, TenantFormValues.Empty, true, null, d.Zone, d.Time)));
        g.MapPost("/new", CreateAsync);
        g.MapGet("/{key}", (HttpContext ctx, string key, Deps d) => Edit(ctx, key, d, null));
        g.MapPost("/{key}/save", SaveAsync);
        g.MapPost("/{key}/test", TestAsync);
        g.MapGet("/{key}/connection", (HttpContext ctx, string key, Deps d) => d.Writer(ctx).Get(key) is null ? NotFound() : HtmlResults.Page(ConnectionDialog(ctx, key, null)));
        g.MapPost("/{key}/connection", SetConnectionAsync);
        g.MapGet("/{key}/maintenance", (HttpContext ctx, string key, Deps d) => d.Writer(ctx).Get(key) is null ? NotFound() : HtmlResults.Page(MaintenanceDialog(ctx, key)));
        g.MapPost("/{key}/maintenance", SetMaintenanceAsync);
        g.MapGet("/{key}/logout-all", (HttpContext ctx, string key, Deps d) => d.Writer(ctx).Get(key) is null ? NotFound() : HtmlResults.Page(LogoutDialog(ctx, key, null)));
        g.MapPost("/{key}/logout-all", LogoutAllAsync);
    }

    /// <summary>Các dịch vụ dùng chung của nhóm endpoint.</summary>
    public sealed class Deps(ConfigDatabase db, SecretProtector protector, AdminAccountStore accounts, SqliteTenantStore store, ErpOptions erp,
        TimeProvider time, IOptions<AdminOptions> options, IErpUsers users)
    {
        public AdminAccountStore Accounts { get; } = accounts;
        public IErpUsers Users { get; } = users;
        public SqliteTenantStore Store { get; } = store;
        public ErpOptions Erp { get; } = erp;
        public TimeProvider Time { get; } = time;
        public TimeZoneInfo Zone { get; } = AdminScreens.Zone(options.Value);

        public ConfigWriter Writer(HttpContext ctx) => new(db, protector, Time, AdminAuth.Actor(ctx));
    }

    private static IResult Edit(HttpContext ctx, string key, Deps d, IReadOnlyList<ErpMessage>? messages, int status = 200)
    {
        var detail = d.Writer(ctx).Get(key);
        return detail is null ? NotFound()
            : HtmlResults.Page(AdminScreens.TenantForm(ctx, TenantFormValues.From(detail), false, detail, d.Zone, d.Time, messages), status);
    }

    private static async Task<IResult> CreateAsync(HttpContext ctx, Deps d)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return Expired();
        var v = TenantFormValues.From(form);
        IResult Fail(params ErpMessage[] m) => HtmlResults.Page(AdminScreens.TenantForm(ctx, v, true, null, d.Zone, d.Time, m), StatusCodes.Status422UnprocessableEntity);

        var writer = d.Writer(ctx);
        if (!TenantNames.IsValidKey(v.Key)) return Fail(ErpMessage.Error("Mã công ty chỉ gồm chữ, số, '_' hoặc '-', tối đa 40 ký tự.", "Key"));
        if (writer.Get(v.Key) is not null) return Fail(ErpMessage.Error($"Đã có công ty mã '{v.Key}'.", "Key"));
        IReadOnlyList<string> hosts;
        try
        {
            hosts = ConfigWriter.NormalizeHosts(v.HostList);
        }
        catch (ArgumentException ex)
        {
            return Fail(ErpMessage.Error(ex.Message, "Hosts"));
        }
        if (Normalize(form["Connection"].ToString()) is not { } conn || conn.Error is not null)
            return Fail(ErpMessage.Error(Normalize(form["Connection"].ToString())?.Error ?? "Thiếu chuỗi kết nối.", "Connection"));
        if (!d.Accounts.VerifyCode(AdminAuth.UserName(ctx), form["Code"].ToString()))
            return Fail(ErpMessage.Error("Mã xác thực không đúng hoặc đã dùng. Nhập mã mới đang hiện.", "Code"));

        try
        {
            writer.AddTenant(v.Key, v.Name, string.IsNullOrWhiteSpace(v.Module) ? "SY" : v.Module, v.TaxCode, v.DbLabel);
            writer.Update(v.Key, v.Name, string.IsNullOrWhiteSpace(v.Module) ? "SY" : v.Module, v.TaxCode, v.DbLabel, v.Enabled, hosts, null);
            writer.SetConnection(v.Key, conn.Value!.ConnectionString);
        }
        catch (ArgumentException ex)
        {
            return Fail(ErpMessage.Error(ex.Message));
        }
        return Edit(ctx, v.Key, d, [ErpMessage.Success($"Đã tạo công ty {v.Key}. Bấm \"Thử kết nối\" để kiểm."), .. Changes(conn.Value!)]);
    }

    private static async Task<IResult> SaveAsync(HttpContext ctx, string key, Deps d)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return Expired();
        var writer = d.Writer(ctx);
        var detail = writer.Get(key);
        if (detail is null) return NotFound();
        var v = TenantFormValues.From(form, key);
        try
        {
            writer.Update(key, v.Name, v.Module, v.TaxCode, v.DbLabel, v.Enabled, ConfigWriter.NormalizeHosts(v.HostList), v.Version);
        }
        catch (ConfigConflictException ex)
        {
            return Edit(ctx, key, d, [ErpMessage.Error(ex.Message)], StatusCodes.Status409Conflict);
        }
        catch (ArgumentException ex)
        {
            return HtmlResults.Page(AdminScreens.TenantForm(ctx, v with { Version = detail.Revision }, false, detail, d.Zone, d.Time, [ErpMessage.Error(ex.Message)]),
                StatusCodes.Status422UnprocessableEntity);
        }
        return Edit(ctx, key, d, [ErpMessage.Success("Đã lưu. Host áp dụng trong vài giây.")]);
    }

    private static async Task<IResult> TestAsync(HttpContext ctx, string key, Deps d)
    {
        await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return Expired();
        var tenant = await d.Store.GetAsync(key, ctx.RequestAborted);
        if (tenant is null) return NotFound();
        var r = await new ErpProbe(new ErpConnectionFactory(d.Store, d.Erp)).ProbeAsync(tenant, ctx.RequestAborted);
        if (!r.Ok) return Edit(ctx, key, d, [ErpMessage.Error($"Không kết nối được ({r.RoundTripMs} ms): {r.Error}")]);

        var raw = await d.Store.GetConnectionStringAsync(key, ctx.RequestAborted);
        var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(raw ?? "");
        var messages = new List<ErpMessage>
        {
            ErpMessage.Success($"Kết nối được: DB {r.Database}, SQL Server {r.ServerVersion}, khứ hồi {r.RoundTripMs} ms. " +
                               $"Mã hóa: {csb.Encrypt}{(csb.TrustServerCertificate ? " (chưa kiểm chứng chỉ máy chủ)" : "")}."),
        };
        if (!r.HasUserTable) messages.Add(ErpMessage.Warning("DB này không có bảng dbo.SY_User: kiểm lại tên DB."));
        if (!r.HasMenuFunction) messages.Add(ErpMessage.Warning($"DB này không có hàm dbo.{tenant.ModuleId}_GetUserMenuFnc: menu sẽ không đọc được."));
        return Edit(ctx, key, d, messages);
    }

    private static async Task<IResult> SetConnectionAsync(HttpContext ctx, string key, Deps d)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return Expired();
        var writer = d.Writer(ctx);
        if (writer.Get(key) is null) return NotFound();
        var conn = Normalize(form["Connection"].ToString());
        if (conn?.Error is not null || conn is null)
            return HtmlResults.Page(ConnectionDialog(ctx, key, [ErpMessage.Error(conn?.Error ?? "Thiếu chuỗi kết nối.", "Connection")]), StatusCodes.Status422UnprocessableEntity);
        if (!d.Accounts.VerifyCode(AdminAuth.UserName(ctx), form["Code"].ToString()))
            return HtmlResults.Page(ConnectionDialog(ctx, key, [ErpMessage.Error("Mã xác thực không đúng hoặc đã dùng.", "Code")]), StatusCodes.Status422UnprocessableEntity);
        writer.SetConnection(key, conn.Value!.ConnectionString);
        return Edit(ctx, key, d, [ErpMessage.Success("Đã đổi chuỗi kết nối. Bấm \"Thử kết nối\" để kiểm."), .. Changes(conn.Value!)]);
    }

    private static async Task<IResult> SetMaintenanceAsync(HttpContext ctx, string key, Deps d)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return Expired();
        var writer = d.Writer(ctx);
        if (writer.Get(key) is null) return NotFound();
        var choice = form["Minutes"].ToString();
        DateTimeOffset? until = choice == "off" ? null
            : int.TryParse(choice, NumberStyles.None, CultureInfo.InvariantCulture, out var m) && m is > 0 and <= 1440 ? d.Time.GetUtcNow().AddMinutes(m) : null;
        if (choice != "off" && until is null) return HtmlResults.Page(MaintenanceDialog(ctx, key), StatusCodes.Status422UnprocessableEntity);
        writer.SetMaintenance(key, until);
        return Edit(ctx, key, d, [ErpMessage.Success(until is null ? "Đã tắt bảo trì." : $"Bảo trì đến {AdminScreens.Time(until, d.Zone)}. Người dùng nhận thông báo bảo trì.")]);
    }

    private static async Task<IResult> LogoutAllAsync(HttpContext ctx, string key, Deps d)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return Expired();
        var writer = d.Writer(ctx);
        if (writer.Get(key) is null) return NotFound();
        if (!d.Accounts.VerifyCode(AdminAuth.UserName(ctx), form["Code"].ToString()))
            return HtmlResults.Page(LogoutDialog(ctx, key, [ErpMessage.Error("Mã xác thực không đúng hoặc đã dùng.", "Code")]), StatusCodes.Status422UnprocessableEntity);
        writer.LogoutAll(key);
        return Edit(ctx, key, d, [ErpMessage.Success("Mọi phiên của công ty bị từ chối trong vài giây tới.")]);
    }

    private static string ConnectionDialog(HttpContext ctx, string key, IReadOnlyList<ErpMessage>? messages)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        return DialogRenderer.Render(new DialogModel($"Đổi chuỗi kết nối — {key}", ctx.Url($"/admin/tenants/{Uri.EscapeDataString(key)}/connection"), field, token,
        [
            new FieldModel("Connection", "Chuỗi kết nối SQL Server", "password", Span: 40, Required: true, Autocomplete: "off",
                Hint: "Phải có Database=<tên DB>. Máy chủ ở xa thì BE tự bật mã hóa. Chuỗi được mã hóa, không hiện lại."),
            AccountEndpoints.CodeField(),
        ], "Lưu chuỗi kết nối", Size: "md", Messages: messages));
    }

    private static string MaintenanceDialog(HttpContext ctx, string key)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        return DialogRenderer.Render(new DialogModel($"Bảo trì — {key}", ctx.Url($"/admin/tenants/{Uri.EscapeDataString(key)}/maintenance"), field, token,
        [
            new FieldModel("Minutes", "Chế độ bảo trì", "select", "30", Span: 40,
                Options: [("off", "Tắt bảo trì"), ("15", "15 phút"), ("30", "30 phút"), ("60", "1 giờ"), ("120", "2 giờ"), ("240", "4 giờ"), ("480", "8 giờ")]),
        ], "Áp dụng", Paragraphs: ["Trong lúc bảo trì, người dùng của công ty này nhận thông báo \"đang bảo trì\" và không vào được. Các công ty khác vẫn chạy."]));
    }

    private static string LogoutDialog(HttpContext ctx, string key, IReadOnlyList<ErpMessage>? messages)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        return DialogRenderer.Render(new DialogModel($"Đăng xuất mọi người — {key}", ctx.Url($"/admin/tenants/{Uri.EscapeDataString(key)}/logout-all"), field, token,
            [AccountEndpoints.CodeField()], "Đăng xuất mọi người", SubmitAction: "custom",
            Paragraphs: ["Mọi người dùng đang đăng nhập vào công ty này sẽ phải đăng nhập lại."], Messages: messages));
    }

    private sealed record ConnResult(NormalizedConnection? Value, string? Error);

    private static ConnResult? Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            var n = ErpConnectionStrings.Normalize(raw.Trim(), new ErpOptions());
            return n.Database is null ? new ConnResult(null, "Chuỗi kết nối thiếu tên DB: thêm Database=<tên DB>.") : new ConnResult(n, null);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return new ConnResult(null, "Chuỗi kết nối không đọc được (sai cú pháp hoặc từ khóa lạ).");
        }
    }

    private static IEnumerable<ErpMessage> Changes(NormalizedConnection n) => n.Changes.Select(ErpMessage.Info);

    private static IResult NotFound() => HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy", ErpMessage.Error("Không có công ty này."));

    internal static IResult Expired() =>
        HtmlResults.Error(StatusCodes.Status400BadRequest, "Trang hết hạn", ErpMessage.Error("Trang đã hết hạn. Tải lại rồi thử lại."));
}
