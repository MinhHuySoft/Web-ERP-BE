using System.Globalization;
using Microsoft.Extensions.Options;
using WebErp.Config;
using WebErp.Core.Messages;
using WebErp.Html.Roles;
using WebErp.Web;

namespace WebErp.Admin.Web;

/// <summary>Một domain trên trang "Domain &amp; chứng chỉ": mong muốn (tenant_host) ghép với trạng thái agent (managed_host).</summary>
public sealed record DomainRow(string Host, IReadOnlyList<string> Tenants, ManagedHost? Managed)
{
    /// <summary>Đã bỏ khỏi mọi công ty, agent chưa gỡ xong (và chưa có trạng thái gỡ riêng).</summary>
    private bool AwaitingRemoval => Tenants.Count == 0 && Managed?.State is not (HostStates.Removing or HostStates.RemovalBlocked or HostStates.Error);

    public string State => AwaitingRemoval ? "to_remove" : Managed?.State ?? "waiting";

    public string StateText => AwaitingRemoval ? "Chờ gỡ" : HostStates.Label(Managed?.State);

    /// <summary>Một dòng mô tả cho ô nhãn trên form công ty.</summary>
    public string Describe(TimeZoneInfo zone)
    {
        var parts = new List<string> { StateText };
        if (Managed?.CertExpires is { } exp) parts.Add("chứng chỉ đến " + AdminScreens.Time(exp, zone)[..10]);
        if (Managed?.LastError is { Length: > 0 } e) parts.Add(e);
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// Trạng thái đồng bộ IIS (agent WebErp.HostSync): xem từng domain, "Đồng bộ lại ngay", "Thử lại", xác nhận gỡ hàng loạt.
/// Trang quản trị không sửa IIS; chỉ ghi yêu cầu vào config.db cho agent.
/// </summary>
public static class DomainEndpoints
{
    /// <summary>Agent không báo về lâu hơn chừng này thì coi như không chạy.</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromMinutes(3);

    public static void MapDomains(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/domains").RequireAuthorization();
        g.MapGet("", (HttpContext ctx, Deps d) => HtmlResults.Page(Page(ctx, d, Tenant(ctx), null)));
        g.MapGet("/sync", (HttpContext ctx) => HtmlResults.Page(SyncDialog(ctx, Tenant(ctx))));
        g.MapPost("/sync", SyncAsync);
        g.MapGet("/retry", (HttpContext ctx, Deps d) => HtmlResults.Page(RetryDialog(ctx, d, Tenant(ctx), ctx.Request.Query["host"].ToString(), null)));
        g.MapPost("/retry", RetryAsync);
        g.MapGet("/approve", (HttpContext ctx, Deps d) => HtmlResults.Page(ApproveDialog(ctx, d, null)));
        g.MapPost("/approve", ApproveAsync);
    }

    public sealed class Deps(HostSyncStore store, TenantEndpoints.Deps tenants, IOptions<AdminOptions> options)
    {
        public HostSyncStore Store { get; } = store;
        public TenantEndpoints.Deps Tenants { get; } = tenants;
        public AdminOptions Options { get; } = options.Value;
    }

    /// <summary>Mọi domain mong muốn + mọi domain agent đang quản lý (kể cả đang gỡ).</summary>
    public static IReadOnlyList<DomainRow> Rows(HostSyncStore store, string? tenant = null)
    {
        var desired = store.Desired().ToDictionary(h => h.Host, StringComparer.OrdinalIgnoreCase);
        var managed = store.Managed().ToDictionary(m => m.Host, StringComparer.OrdinalIgnoreCase);
        return desired.Keys.Union(managed.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(h => new DomainRow(h, desired.TryGetValue(h, out var d) ? d.TenantKeys : [], managed.GetValueOrDefault(h)))
            .Where(r => tenant is null || r.Tenants.Contains(tenant, StringComparer.OrdinalIgnoreCase))
            .OrderBy(r => r.Host, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Thông báo về agent: chưa chạy, không còn chạy, lỗi vòng gần nhất, có bản mới chờ cài.</summary>
    public static IReadOnlyList<ErpMessage> AgentMessages(HostSyncMeta meta, DateTimeOffset now, TimeZoneInfo zone)
    {
        var list = new List<ErpMessage>();
        if (meta.HeartbeatAt is null)
            list.Add(ErpMessage.Warning("Agent đồng bộ IIS (WebErp.HostSync) chưa chạy lần nào: domain mới chưa được thêm vào IIS. Cài bằng setup-hostsync.ps1 trên VPS."));
        else if (now - meta.HeartbeatAt > HeartbeatTimeout)
            list.Add(ErpMessage.Error($"Agent đồng bộ IIS không chạy từ {AdminScreens.Time(meta.HeartbeatAt, zone)}: kiểm service WebErpHostSync trên VPS."));
        if (meta.LastError is { Length: > 0 } e) list.Add(ErpMessage.Warning("Vòng đồng bộ gần nhất lỗi: " + e));
        if (meta.StagedVersion is { Length: > 0 } staged && staged != meta.AgentVersion)
            list.Add(ErpMessage.Info($"Có bản agent mới {staged} (đang chạy {meta.AgentVersion ?? "?"}): chạy C:\\WebErp\\setup\\setup-hostsync.ps1 trên VPS để cài."));
        return list;
    }

    private static string? Tenant(HttpContext ctx) => ctx.Request.Query["tenant"].ToString() is { Length: > 0 } t ? t : null;

    private static string Q(string? tenant) => tenant is null ? "" : "?tenant=" + Uri.EscapeDataString(tenant);

    private static string Page(HttpContext ctx, Deps d, string? tenant, IReadOnlyList<ErpMessage>? messages)
    {
        var zone = d.Tenants.Zone;
        var rows = Rows(d.Store, tenant);
        var meta = d.Store.Meta();
        var all = new List<ErpMessage>(messages ?? []);
        all.AddRange(AgentMessages(meta, d.Tenants.Time.GetUtcNow(), zone));
        if (meta.HeartbeatAt is { } beat) all.Add(ErpMessage.Info($"Agent {meta.AgentVersion} chạy vòng gần nhất lúc {AdminScreens.Time(beat, zone)}."));

        var blocked = rows.Count(r => r.Managed?.State == HostStates.RemovalBlocked);
        var toolbar = new List<ToolbarItem>
        {
            new("custom", "Đồng bộ lại ngay", Href: ctx.Url("/admin/domains/sync" + Q(tenant)), Target: "dialog", Icon: "settings"),
            new("custom", "Thử lại domain", Href: ctx.Url("/admin/domains/retry" + Q(tenant)), Target: "dialog", Icon: "clock"),
        };
        if (blocked > 0) toolbar.Add(new ToolbarItem("custom", $"Xác nhận gỡ ({blocked})", Href: ctx.Url("/admin/domains/approve"), Target: "dialog", Icon: "lock"));
        toolbar.Add(new ToolbarItem("reload", "Nạp lại", Href: ctx.Url("/admin/domains" + Q(tenant)), Target: "self"));

        var grid = new GridModel("T0",
        [
            new GridColumn("Host", "Domain", Width: 220, Frozen: true),
            new GridColumn("Tenants", "Công ty", Width: 140, Wrap: true),
            new GridColumn("State", "Trạng thái", "status", 130),
            new GridColumn("CertExpires", "Chứng chỉ hết hạn", "datetime", 140),
            new GridColumn("LastError", "Lỗi / ghi chú gần nhất", Width: 340, Wrap: true),
            new GridColumn("LastChecked", "Kiểm lúc", "datetime", 140),
        ], rows.Select(r => new GridRow(r.Host,
        [
            new GridCell(r.Host),
            new GridCell(string.Join(", ", r.Tenants)),
            new GridCell(r.StateText, r.State, StatusTone: r.Managed is null ? "muted" : HostStates.Tone(r.Managed.State)),
            GridCell.DateTime(r.Managed?.CertExpires, zone),
            new GridCell(r.Managed?.LastError),
            GridCell.DateTime(r.Managed?.LastCheckedAt, zone),
        ])).ToList(), KeyField: "Host", Selectable: "single");

        var title = tenant is null ? "Domain & chứng chỉ" : $"Domain & chứng chỉ — {tenant}";
        return ScreenRenderer.Render(new ScreenModel("AdminDomains", FormTypes.List, "AdminDomains" + (tenant is null ? "" : ":" + tenant), title, Messages: all), w =>
        {
            ToolbarRenderer.Write(w, toolbar);
            GridRenderer.Write(w, grid);
        });
    }

    private static string SyncDialog(HttpContext ctx, string? tenant)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        return DialogRenderer.Render(new DialogModel("Đồng bộ IIS ngay", ctx.Url("/admin/domains/sync" + Q(tenant)), field, token, [], "Đồng bộ ngay",
            SubmitAction: "custom",
            Paragraphs:
            [
                "Agent trên VPS sẽ chạy một vòng đồng bộ trong khoảng 15 giây: thêm domain mới (nếu DNS đã đúng), gỡ domain đã bỏ.",
                "Nếu có binding cần thêm hoặc gỡ, web của tất cả công ty sẽ khởi động lại khoảng 30 giây.",
            ]));
    }

    private static async Task<IResult> SyncAsync(HttpContext ctx, Deps d)
    {
        await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return TenantEndpoints.Expired();
        d.Store.RequestSync(AdminAuth.Actor(ctx));
        return HtmlResults.Page(Page(ctx, d, Tenant(ctx), [ErpMessage.Success("Đã gửi yêu cầu đồng bộ. Bấm \"Nạp lại\" sau khoảng 30 giây để xem kết quả.")]));
    }

    private static string RetryDialog(HttpContext ctx, Deps d, string? tenant, string? host, IReadOnlyList<ErpMessage>? messages)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        var hosts = Rows(d.Store, tenant).Where(r => r.Managed is not null).Select(r => r.Host).ToList();
        var fields = hosts.Count == 0 ? new List<FieldModel>()
            : [new FieldModel("Host", "Domain", "select", hosts.Contains(host ?? "", StringComparer.OrdinalIgnoreCase) ? host : hosts[0], Span: 40,
                Options: hosts.Select(h => (h, h)).ToList())];
        return DialogRenderer.Render(new DialogModel("Thử lại domain", ctx.Url("/admin/domains/retry" + Q(tenant)), field, token, fields, "Thử lại",
            SubmitAction: "custom", Messages: messages,
            Paragraphs: hosts.Count == 0
                ? ["Chưa có domain nào để thử lại (agent chưa nhận domain nào)."]
                : ["Bỏ thời gian chờ và thử lại ngay ở vòng kế tiếp: kiểm DNS, xin chứng chỉ, kiểm HTTPS.",
                   "Let's Encrypt giới hạn số lần xác thực lỗi: chỉ thử lại khi đã sửa DNS / nguyên nhân lỗi."]));
    }

    private static async Task<IResult> RetryAsync(HttpContext ctx, Deps d)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return TenantEndpoints.Expired();
        var tenant = Tenant(ctx);
        var host = form["Host"].ToString().Trim().ToLowerInvariant();
        if (host.Length == 0 || !d.Store.Retry(AdminAuth.Actor(ctx), host))
            return HtmlResults.Page(RetryDialog(ctx, d, tenant, host, [ErpMessage.Error("Chọn một domain agent đang quản lý.", "Host")]), StatusCodes.Status422UnprocessableEntity);
        return HtmlResults.Page(Page(ctx, d, tenant, [ErpMessage.Success($"Sẽ thử lại {host} ở vòng đồng bộ kế tiếp (khoảng 15 giây).")]));
    }

    private static string ApproveDialog(HttpContext ctx, Deps d, IReadOnlyList<ErpMessage>? messages)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        var blocked = d.Store.Managed().Where(m => m.State == HostStates.RemovalBlocked).Select(m => m.Host).ToList();
        var paragraphs = blocked.Count == 0
            ? new List<string> { "Không có domain nào đang chờ xác nhận gỡ." }
            : [
                $"Agent cần gỡ {blocked.Count} domain cùng lúc, vượt ngưỡng an toàn nên đang chờ xác nhận:",
                string.Join(", ", blocked),
                "Các domain này sẽ bị gỡ khỏi IIS (binding http + https, ánh xạ SSL), hủy renewal win-acme và mất chứng chỉ. Web của tất cả công ty khởi động lại khoảng 30 giây.",
                "Nếu đây là nhầm lẫn (vd lỡ xóa domain), hãy thêm lại domain cho công ty thay vì xác nhận.",
            ];
        return DialogRenderer.Render(new DialogModel("Xác nhận gỡ domain", ctx.Url("/admin/domains/approve"), field, token,
            blocked.Count == 0 ? [] : [AccountEndpoints.CodeField()], "Gỡ các domain này", SubmitAction: "delete", Size: "md",
            Paragraphs: paragraphs, Messages: messages));
    }

    private static async Task<IResult> ApproveAsync(HttpContext ctx, Deps d)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return TenantEndpoints.Expired();
        var blocked = d.Store.Managed().Where(m => m.State == HostStates.RemovalBlocked).Select(m => m.Host).ToList();
        if (blocked.Count == 0) return HtmlResults.Page(Page(ctx, d, null, [ErpMessage.Info("Không có domain nào đang chờ xác nhận gỡ.")]));
        if (!d.Tenants.Accounts.VerifyCode(AdminAuth.UserName(ctx), form["Code"].ToString()))
            return HtmlResults.Page(ApproveDialog(ctx, d, [ErpMessage.Error("Mã xác thực không đúng hoặc đã dùng.", "Code")]), StatusCodes.Status422UnprocessableEntity);
        var n = d.Store.ApproveRemoval(AdminAuth.Actor(ctx), blocked);
        return HtmlResults.Page(Page(ctx, d, null,
            [ErpMessage.Success($"Đã xác nhận gỡ {n.ToString(CultureInfo.InvariantCulture)} domain. Agent gỡ ở vòng kế tiếp.")]));
    }
}
