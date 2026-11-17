using System.Globalization;
using WebErp.Config;
using WebErp.Core.Messages;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;
using WebErp.Html.Roles;
using WebErp.Web;

namespace WebErp.Admin.Web;

/// <summary>
/// Mô tả form viết tay (form cứng) sửa trên trang quản trị: danh sách, sửa JSON, kiểm trên DB một công ty, lịch sử, xóa, và liệt kê
/// form trong menu công ty mà web chưa dựng được. Lưu / xóa phải nhập mã TOTP. Host áp dụng trong vài giây, không cần deploy.
/// </summary>
public static class FormEndpoints
{
    public const string RecordId = "rec-form";
    private const int MaxJson = 200_000;

    public static void MapForms(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/forms").RequireAuthorization();
        g.MapGet("", (HttpContext ctx, Deps d) => HtmlResults.Page(List(ctx, d, null)));
        g.MapGet("/edit", (HttpContext ctx, Deps d, string? form, string? scope) => Edit(ctx, d, form, Scope(scope), null));
        g.MapGet("/restore", (HttpContext ctx, Deps d, long id) => Restore(ctx, d, id));
        g.MapPost("/save", SaveAsync);
        g.MapPost("/check", CheckAsync);
        g.MapGet("/history", (HttpContext ctx, Deps d, string form, string? scope) => HtmlResults.Page(History(ctx, d, form, Scope(scope))));
        g.MapGet("/delete", (HttpContext ctx, Deps d, string form, string? scope) =>
            d.Writer(ctx).GetFormOverlay(form, Scope(scope)) is { } row ? HtmlResults.Page(DeleteDialog(ctx, row, null)) : NotFound());
        g.MapPost("/delete", DeleteAsync);
        g.MapGet("/missing", (HttpContext ctx, Deps d) => HtmlResults.Page(MissingDialog(ctx, d, null)));
        g.MapPost("/missing", MissingAsync);
    }

    /// <summary>Dịch vụ của nhóm endpoint (dùng chung với trang công ty).</summary>
    public sealed class Deps(TenantEndpoints.Deps tenants, OverlayProbe probe)
    {
        public TenantEndpoints.Deps Tenants { get; } = tenants;
        public OverlayProbe Probe { get; } = probe;
        public TimeZoneInfo Zone => Tenants.Zone;

        public ConfigWriter Writer(HttpContext ctx) => Tenants.Writer(ctx);
    }

    /// <summary>Giá trị form sửa mô tả (giữ lại khi trả form có lỗi; không bao giờ giữ mã xác thực).</summary>
    private sealed record Values(string Form, string? Scope, string Json, bool Enabled, string? Note, string? Version, string? CheckTenant, string? CheckPara)
    {
        public static Values From(IFormCollection f) =>
            new(f["Form"].ToString().Trim(), FormEndpoints.Scope(f["Scope"].ToString()), f["Json"].ToString(), f["Enabled"].LastOrDefault() == "true",
                f["Note"].ToString(), f["__version"].ToString() is { Length: > 0 } v ? v : null, f["CheckTenant"].ToString(), f["CheckPara"].ToString().Trim());
    }

    /// <summary><c>scope</c> rỗng = mọi công ty.</summary>
    private static string? Scope(string? scope) => string.IsNullOrWhiteSpace(scope) ? null : scope.Trim();

    private static string ScopeText(string? scope) => scope is null ? "Mọi công ty" : scope;

    private static string Query(string form, string? scope) => $"form={Uri.EscapeDataString(form)}&scope={Uri.EscapeDataString(scope ?? "")}";

    private static string List(HttpContext ctx, Deps d, IReadOnlyList<ErpMessage>? messages)
    {
        var runtime = d.Writer(ctx).ListFormOverlays();
        var shadowed = runtime.Where(r => r.TenantKey is null && r.Enabled).Select(r => r.Form).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = new List<GridRow>();
        foreach (var r in runtime)
        {
            var q = Query(r.Form, r.TenantKey);
            rows.Add(new GridRow($"web|{r.Form}|{r.TenantKey}",
            [
                new GridCell(r.Form, r.Form, ctx.Url($"/admin/forms/edit?{q}"), "tab"),
                new GridCell(ScopeText(r.TenantKey), r.TenantKey ?? ""),
                new GridCell("Sửa trên web", "web"),
                r.Enabled ? new GridCell("Đang dùng", "on", StatusTone: "success") : new GridCell("Tắt", "off", StatusTone: "muted"),
                new GridCell(r.Revision.ToString(CultureInfo.InvariantCulture), r.Revision.ToString(CultureInfo.InvariantCulture)),
                GridCell.DateTime(r.UpdatedAt, d.Zone),
                new GridCell(r.UpdatedBy),
                new GridCell(r.Note),
            ]));
        }
        foreach (var name in FormOverlays.Names.Order(StringComparer.OrdinalIgnoreCase))
        {
            var hidden = shadowed.Contains(name);
            rows.Add(new GridRow($"builtin|{name}",
            [
                new GridCell(name, name, ctx.Url($"/admin/forms/edit?{Query(name, null)}"), "tab"),
                new GridCell("Mọi công ty", ""),
                new GridCell(hidden ? "Có sẵn trong BE (đang bị che)" : "Có sẵn trong BE", "builtin"),
                hidden ? new GridCell("Bị che", "shadowed", StatusTone: "muted") : new GridCell("Đang dùng", "on", StatusTone: "success"),
                new GridCell(null),
                new GridCell(null),
                new GridCell(null),
                new GridCell(null),
            ]));
        }
        var grid = new GridModel("T0",
        [
            new GridColumn("Form", "Form", "link", 220, Frozen: true),
            new GridColumn("Scope", "Phạm vi", Width: 120),
            new GridColumn("Source", "Nguồn", Width: 200),
            new GridColumn("Status", "Trạng thái", "status", 100),
            new GridColumn("Revision", "Phiên bản", "number", 80),
            new GridColumn("UpdatedAt", "Sửa lúc", "datetime", 140),
            new GridColumn("UpdatedBy", "Người sửa", Width: 200),
            new GridColumn("Note", "Ghi chú", Width: 260, Wrap: true),
        ], rows, KeyField: "Key", Caption: "Mô tả trên web đứng trước tệp có sẵn trong BE; mô tả riêng công ty đứng trước mô tả chung.");

        return ScreenRenderer.Render(new ScreenModel("AdminForms", FormTypes.List, "AdminForms", "Mô tả form cứng", Messages: messages), w =>
        {
            ToolbarRenderer.Write(w,
            [
                new ToolbarItem("new", "Thêm mô tả", Href: ctx.Url("/admin/forms/edit"), Target: "tab"),
                new ToolbarItem("custom", "Form còn thiếu của công ty", Href: ctx.Url("/admin/forms/missing"), Target: "dialog", Icon: "search"),
                new ToolbarItem("reload", "Nạp lại", Href: ctx.Url("/admin/forms"), Target: "self"),
            ]);
            GridRenderer.Write(w, grid);
        });
    }

    private static IResult Edit(HttpContext ctx, Deps d, string? form, string? scope, IReadOnlyList<ErpMessage>? messages, int status = 200)
    {
        if (string.IsNullOrWhiteSpace(form))
            return HtmlResults.Page(EditScreen(ctx, d, new Values("", null, Template, true, null, null, null, null), isNew: true, existing: false, messages), status);
        if (!ConfigWriter.IsValidFormName(form)) return NotFound();
        var row = d.Writer(ctx).GetFormOverlay(form, scope);
        if (row is not null)
        {
            var v = new Values(row.Form, row.TenantKey, row.Json, row.Enabled, row.Note, row.Revision.ToString(CultureInfo.InvariantCulture), null, null);
            return HtmlResults.Page(EditScreen(ctx, d, v, isNew: false, existing: true, messages), status);
        }
        // Chưa có mô tả trên web: chép tệp có sẵn trong BE (nếu có) ra để sửa.
        var builtIn = FormOverlays.BuiltInJson(form);
        var info = new List<ErpMessage>(messages ?? []);
        info.Add(builtIn is null
            ? ErpMessage.Info($"Chưa có mô tả {form}. Viết JSON theo mẫu rồi bấm \"Kiểm trên DB công ty\" trước khi lưu.")
            : ErpMessage.Info("Đây là tệp có sẵn trong BE. Lưu sẽ tạo mô tả trên web, đứng trước tệp có sẵn cho phạm vi đã chọn."));
        return HtmlResults.Page(EditScreen(ctx, d, new Values(form, scope, builtIn ?? Template.Replace("TenForm", form, StringComparison.Ordinal), true, null, null, null, null),
            isNew: true, existing: false, info, formLocked: true), status);
    }

    private static IResult Restore(HttpContext ctx, Deps d, long id)
    {
        var h = d.Writer(ctx).GetFormOverlayHistory(id);
        if (h?.Json is null) return NotFound();
        var current = d.Writer(ctx).GetFormOverlay(h.Form, h.TenantKey);
        var v = new Values(h.Form, h.TenantKey, h.Json, h.Enabled, h.Note, current?.Revision.ToString(CultureInfo.InvariantCulture), null, null);
        return HtmlResults.Page(EditScreen(ctx, d, v, isNew: current is null, existing: current is not null,
            [ErpMessage.Info($"Đang xem bản {h.Revision} ({AdminScreens.Time(h.At, d.Zone)}, {h.Actor}). Bấm Lưu để khôi phục bản này.")], formLocked: true));
    }

    private static string EditScreen(HttpContext ctx, Deps d, Values v, bool isNew, bool existing, IReadOnlyList<ErpMessage>? messages, bool formLocked = false)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        var tenants = d.Writer(ctx).List().Select(t => (t.Tenant.Key, $"{t.Tenant.Key} — {t.Tenant.Name}")).ToList();
        var locked = existing || formLocked;
        var info = new List<FieldModel>
        {
            new("Form", "Tên form", Value: v.Form, Span: 16, Required: true, ReadOnly: locked, MaxLength: 100, Pattern: "[A-Za-z_][A-Za-z0-9_]{0,99}",
                Hint: locked ? null : "Tên lớp / FormID của WinForms, trùng khóa \"form\" trong JSON (vd UserLogFrm)."),
            existing
                ? new FieldModel("Scope", "Phạm vi", Value: v.Scope ?? "", Span: 14, ReadOnly: true, Hint: v.Scope is null ? "Trống = mọi công ty." : null)
                : new FieldModel("Scope", "Phạm vi", "select", v.Scope ?? "", Span: 14, Options: [("", "Mọi công ty"), .. tenants],
                    Hint: "Riêng một công ty thì đứng trước mô tả chung."),
            new("Enabled", "Đang dùng", "checkbox", v.Enabled ? "true" : "false", Span: 10, Hint: "Bỏ tích để tạm tắt (dùng lại tệp có sẵn nếu có)."),
            new("Json", "Mô tả (JSON)", "textarea", v.Json, Span: 40, Rows: 28, Required: true, MaxLength: MaxJson, RowStart: true, Autocomplete: "off",
                Hint: "Cùng định dạng với tệp src/WebErp.Erp/Metadata/Overlays/*.json: form, formType, caption, useDbConfig, cfg (T0, T1… với TN, PK, SE, EX, FTX, SO…), para, flt. " +
                      "Cho phép chú thích //. {Mod} = mã module của công ty. Web chỉ đọc: SQL có lệnh ghi bị từ chối."),
            new("Note", "Ghi chú", Value: v.Note, Span: 40, MaxLength: 500, RowStart: true, Hint: "Vì sao sửa, nguồn (vd \"UserLogFrm.cs:610\"). Không ghi mật khẩu / dữ liệu khách."),
        };
        var check = new List<FieldModel>
        {
            new("CheckTenant", "Công ty để kiểm", "select", v.CheckTenant ?? v.Scope ?? tenants.FirstOrDefault().Key ?? "", Span: 20, Options: tenants),
            new("CheckPara", "Para (nếu có)", Value: v.CheckPara, Span: 10, MaxLength: 50, Hint: "Kiểm cấu hình theo Para của menu, vd Bank."),
        };
        var confirm = new List<FieldModel> { AccountEndpoints.CodeField() with { Required = false, Span = 20 } };

        var q = Query(v.Form, v.Scope);
        var toolbar = new List<ToolbarItem>
        {
            new("save", "Lưu", Form: RecordId, FormAction: ctx.Url("/admin/forms/save"), Shortcut: "Ctrl+S"),
            new("custom", "Kiểm trên DB công ty", Form: RecordId, FormAction: ctx.Url("/admin/forms/check"), Icon: "system"),
        };
        if (v.Form.Length > 0) toolbar.Add(new ToolbarItem("custom", "Lịch sử", Href: ctx.Url($"/admin/forms/history?{q}"), Target: "tab", Icon: "clock"));
        if (existing) toolbar.Add(new ToolbarItem("delete", "Xóa mô tả", Href: ctx.Url($"/admin/forms/delete?{q}"), Target: "dialog"));
        toolbar.Add(new ToolbarItem("cancel", "Về danh sách", Href: ctx.Url("/admin/forms"), Target: "self"));

        var key = isNew && v.Form.Length == 0 ? "AdminForms:new" : $"AdminForms:{v.Form}:{v.Scope}";
        return ScreenRenderer.Render(new ScreenModel("AdminForms", FormTypes.Edit, key, v.Form.Length == 0 ? "Thêm mô tả form" : $"Mô tả {v.Form} — {ScopeText(v.Scope)}",
            Mode: isNew ? "new" : "edit", RefreshSrc: v.Form.Length == 0 ? null : ctx.Url($"/admin/forms/edit?{q}"), Messages: messages), w =>
        {
            ToolbarRenderer.Write(w, toolbar);
            RecordRenderer.Write(w, new RecordModel(RecordId, ctx.Url("/admin/forms/save"), field, token,
            [
                new FieldsetModel("Mô tả", info),
                new FieldsetModel("Kiểm trên DB công ty", check),
                new FieldsetModel("Xác nhận lưu", confirm),
            ], KeyField: "Form", Key: existing ? v.Form : null, Version: v.Version));
        });
    }

    /// <summary>Kiểm nội dung (không cần DB): JSON đọc được, tên khớp, SQL chỉ đọc. Trả lỗi (gắn ô) hoặc null.</summary>
    private static ErpMessage? Validate(Values v)
    {
        if (!ConfigWriter.IsValidFormName(v.Form)) return ErpMessage.Error("Tên form chỉ gồm chữ, số, '_' (vd UserLogFrm).", "Form");
        if (string.IsNullOrWhiteSpace(v.Json)) return ErpMessage.Error("Thiếu nội dung mô tả (JSON).", "Json");
        if (v.Json.Length > MaxJson) return ErpMessage.Error($"Mô tả quá dài (tối đa {MaxJson:N0} ký tự).", "Json");
        var r = FormOverlays.Validate(v.Json);
        if (r.Error is not null) return ErpMessage.Error(r.Error, "Json");
        if (!string.Equals(r.Form, v.Form, StringComparison.OrdinalIgnoreCase))
            return ErpMessage.Error($"Khóa \"form\" trong JSON là '{r.Form}', khác tên form '{v.Form}'.", "Json");
        return null;
    }

    private static async Task<IResult> SaveAsync(HttpContext ctx, Deps d)
    {
        var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return TenantEndpoints.Expired();
        var v = Values.From(f);
        var writer = d.Writer(ctx);
        var existing = v.Version is not null && writer.GetFormOverlay(v.Form, v.Scope) is not null;
        IResult Fail(params ErpMessage[] m) =>
            HtmlResults.Page(EditScreen(ctx, d, v, isNew: !existing, existing, m, formLocked: true), StatusCodes.Status422UnprocessableEntity);

        if (Validate(v) is { } bad) return Fail(bad);
        if (v.Scope is not null && writer.Get(v.Scope) is null) return Fail(ErpMessage.Error($"Không có công ty '{v.Scope}'.", "Scope"));
        if (!d.Tenants.Accounts.VerifyCode(AdminAuth.UserName(ctx), f["Code"].ToString()))
            return Fail(ErpMessage.Error("Mã xác thực không đúng hoặc đã dùng. Nhập mã mới đang hiện rồi bấm Lưu.", "Code"));
        long revision;
        try
        {
            revision = writer.SaveFormOverlay(v.Form, v.Scope, v.Json, v.Enabled, v.Note,
                v.Version is null ? null : long.Parse(v.Version, NumberStyles.None, CultureInfo.InvariantCulture));
        }
        catch (ConfigConflictException ex)
        {
            return Edit(ctx, d, v.Form, v.Scope, [ErpMessage.Error(ex.Message)], StatusCodes.Status409Conflict);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            return Fail(ErpMessage.Error(ex.Message));
        }
        return Edit(ctx, d, v.Form, v.Scope,
        [
            ErpMessage.Success($"Đã lưu mô tả {v.Form} (phiên bản {revision}) cho {ScopeText(v.Scope).ToLowerInvariant()}. Web áp dụng trong vài giây, mở lại màn để xem."),
        ]);
    }

    private static async Task<IResult> CheckAsync(HttpContext ctx, Deps d)
    {
        var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return TenantEndpoints.Expired();
        var v = Values.From(f);
        var existing = v.Version is not null && d.Writer(ctx).GetFormOverlay(v.Form, v.Scope) is not null;
        IResult Show(int status, params ErpMessage[] m) => HtmlResults.Page(EditScreen(ctx, d, v, isNew: !existing, existing, m, formLocked: v.Form.Length > 0), status);

        if (Validate(v) is { } bad) return Show(StatusCodes.Status422UnprocessableEntity, bad);
        var tenant = string.IsNullOrEmpty(v.CheckTenant) ? null : await d.Tenants.Store.GetAsync(v.CheckTenant, ctx.RequestAborted);
        if (tenant is null) return Show(StatusCodes.Status422UnprocessableEntity, ErpMessage.Error("Chọn công ty để kiểm.", "CheckTenant"));

        OverlayCheck r;
        try
        {
            r = await d.Probe.CheckAsync(tenant, v.Json, string.IsNullOrEmpty(v.CheckPara) ? null : v.CheckPara, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is WebErp.Core.Errors.ErpDatabaseException or Microsoft.Data.SqlClient.SqlException)
        {
            return Show(StatusCodes.Status200OK, ErpMessage.Error($"Không đọc được DB công ty {tenant.Key}: {Describe(ex)}"));
        }
        var messages = new List<ErpMessage>();
        if (r.Error is not null) messages.Add(ErpMessage.Error(r.Error, "Json"));
        foreach (var s in r.Slots)
        {
            if (s.Error is not null) messages.Add(ErpMessage.Error($"{s.Slot} (bảng {s.Source}): {s.Error}", "Json"));
            else
            {
                var cols = s.Columns.Count <= 30 ? string.Join(", ", s.Columns) : string.Join(", ", s.Columns.Take(30)) + $"… (+{s.Columns.Count - 30})";
                messages.Add(ErpMessage.Success($"{s.Slot} (bảng {s.Source}): chạy được, {s.Columns.Count} cột: {cols}."));
            }
            if (s.Dropped.Count > 0) messages.Add(ErpMessage.Warning($"{s.Slot}: bỏ cột DB công ty {tenant.Key} không có: {string.Join(", ", s.Dropped)}."));
        }
        messages.Insert(0, r.Ok
            ? ErpMessage.Success($"Mô tả chạy được trên DB công ty {tenant.Key} ({r.FormType}, \"{r.Caption}\"). Chưa lưu: nhập mã xác thực rồi bấm Lưu.")
            : ErpMessage.Warning($"Mô tả CHƯA chạy được trên DB công ty {tenant.Key}. Sửa các lỗi dưới đây rồi kiểm lại."));
        return Show(StatusCodes.Status200OK, [.. messages]);
    }

    private static string History(HttpContext ctx, Deps d, string form, string? scope)
    {
        var rows = ConfigWriter.IsValidFormName(form) ? d.Writer(ctx).FormOverlayHistory(form, scope) : [];
        var grid = new GridModel("T0",
        [
            new GridColumn("At", "Thời gian", "datetime", 140),
            new GridColumn("Revision", "Phiên bản", "link", 90),
            new GridColumn("Action", "Thao tác", Width: 100),
            new GridColumn("Enabled", "Đang dùng", "bool", 80),
            new GridColumn("Actor", "Người làm", Width: 200),
            new GridColumn("Note", "Ghi chú", Width: 300, Wrap: true),
        ], rows.Select(h => new GridRow(h.Id.ToString(CultureInfo.InvariantCulture),
        [
            GridCell.DateTime(h.At, d.Zone),
            new GridCell(h.Revision.ToString(CultureInfo.InvariantCulture), h.Revision.ToString(CultureInfo.InvariantCulture),
                h.Json is null ? null : ctx.Url($"/admin/forms/restore?id={h.Id.ToString(CultureInfo.InvariantCulture)}"), "tab"),
            new GridCell(h.Action == "delete" ? "Xóa" : "Lưu", h.Action),
            GridCell.Bool(h.Enabled),
            new GridCell(h.Actor),
            new GridCell(h.Note),
        ])).ToList(), KeyField: "Id", Sort: "At:desc", Caption: "Bấm số phiên bản để mở bản đó; bấm Lưu ở đó để khôi phục.");
        return ScreenRenderer.Render(new ScreenModel("AdminForms", FormTypes.List, $"AdminForms:history:{form}:{scope}", $"Lịch sử mô tả {form} — {ScopeText(scope)}"), w =>
        {
            ToolbarRenderer.Write(w,
            [
                new ToolbarItem("reload", "Nạp lại", Href: ctx.Url($"/admin/forms/history?{Query(form, scope)}"), Target: "self"),
                new ToolbarItem("cancel", "Về mô tả", Href: ctx.Url($"/admin/forms/edit?{Query(form, scope)}"), Target: "self"),
            ]);
            GridRenderer.Write(w, grid);
        });
    }

    private static string DeleteDialog(HttpContext ctx, FormOverlayRow row, IReadOnlyList<ErpMessage>? messages)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        var fallback = FormOverlays.Has(row.Form)
            ? "Form sẽ dùng lại tệp có sẵn trong BE."
            : row.TenantKey is not null ? "Form sẽ dùng mô tả chung (nếu có), không thì là \"chưa có trên web\"." : "Form sẽ là \"chưa có trên web\".";
        return DialogRenderer.Render(new DialogModel($"Xóa mô tả {row.Form} — {ScopeText(row.TenantKey)}",
            ctx.Url($"/admin/forms/delete?{Query(row.Form, row.TenantKey)}"), field, token,
            [AccountEndpoints.CodeField()], "Xóa mô tả", SubmitAction: "delete", Size: "md",
            Paragraphs: [$"Xóa mô tả {row.Form} (phiên bản {row.Revision}) khỏi cấu hình web. {fallback}", "Bản này vẫn giữ trong lịch sử để khôi phục."],
            Messages: messages));
    }

    private static async Task<IResult> DeleteAsync(HttpContext ctx, Deps d, string form, string? scope)
    {
        var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return TenantEndpoints.Expired();
        var writer = d.Writer(ctx);
        var s = Scope(scope);
        if (writer.GetFormOverlay(form, s) is not { } row) return NotFound();
        if (!d.Tenants.Accounts.VerifyCode(AdminAuth.UserName(ctx), f["Code"].ToString()))
            return HtmlResults.Page(DeleteDialog(ctx, row, [ErpMessage.Error("Mã xác thực không đúng hoặc đã dùng.", "Code")]), StatusCodes.Status422UnprocessableEntity);
        try
        {
            writer.DeleteFormOverlay(row.Form, row.TenantKey, row.Revision);
        }
        catch (ConfigConflictException ex)
        {
            return HtmlResults.Page(List(ctx, d, [ErpMessage.Error(ex.Message)]), StatusCodes.Status409Conflict);
        }
        return HtmlResults.Page(List(ctx, d, [ErpMessage.Success($"Đã xóa mô tả {row.Form} ({ScopeText(row.TenantKey).ToLowerInvariant()}). Web áp dụng trong vài giây.")]));
    }

    private static string MissingDialog(HttpContext ctx, Deps d, IReadOnlyList<ErpMessage>? messages)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        var tenants = d.Writer(ctx).List().Select(t => (t.Tenant.Key, $"{t.Tenant.Key} — {t.Tenant.Name}")).ToList();
        return DialogRenderer.Render(new DialogModel("Form còn thiếu của công ty", ctx.Url("/admin/forms/missing"), field, token,
            [new FieldModel("Tenant", "Công ty", "select", tenants.FirstOrDefault().Key ?? "", Span: 40, Required: true, Options: tenants)],
            "Xem", SubmitAction: "custom", Size: "md",
            Paragraphs: ["Đọc menu của công ty ({Module}_Menu) và cấu hình form, liệt kê form web chưa dựng được: form viết tay chưa có mô tả, " +
                         "hoặc không có cấu hình nào. Chỉ đọc, không đọc dữ liệu nghiệp vụ."], Messages: messages));
    }

    private static async Task<IResult> MissingAsync(HttpContext ctx, Deps d)
    {
        var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return TenantEndpoints.Expired();
        var tenant = await d.Tenants.Store.GetAsync(f["Tenant"].ToString(), ctx.RequestAborted);
        if (tenant is null)
            return HtmlResults.Page(MissingDialog(ctx, d, [ErpMessage.Error("Chọn công ty.", "Tenant")]), StatusCodes.Status422UnprocessableEntity);
        MenuGaps result;
        try
        {
            result = await d.Probe.MenuGapsAsync(tenant, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is WebErp.Core.Errors.ErpDatabaseException or Microsoft.Data.SqlClient.SqlException)
        {
            return HtmlResults.Page(MissingDialog(ctx, d, [ErpMessage.Error($"Không đọc được DB công ty {tenant.Key}: {Describe(ex)}")]), StatusCodes.Status200OK);
        }
        var grid = new GridModel("T0",
        [
            new GridColumn("Form", "Form", "link", 220, Frozen: true),
            new GridColumn("Caption", "Tên trên menu", Width: 280),
            new GridColumn("Reason", "Tình trạng", "status", 220),
        ], result.Gaps.Select(g => new GridRow(g.Form,
        [
            new GridCell(g.Form, g.Form, ctx.Url($"/admin/forms/edit?{Query(g.Form, null)}"), "tab"),
            new GridCell(g.Caption),
            g.Reason == "handcoded"
                ? new GridCell("Form cứng chưa có mô tả", g.Reason, StatusTone: "warning")
                : new GridCell("Không có cấu hình form", g.Reason, StatusTone: "muted"),
        ])).ToList(), KeyField: "Form",
            Caption: "Bấm tên form để viết mô tả. \"Không có cấu hình form\" thường là form cứng của bản ERP này mà BE chưa có trong danh sách tên lớp.");
        var gaps = result.Gaps;
        var messages = new List<ErpMessage>
        {
            gaps.Count == 0
                ? ErpMessage.Success($"Mọi form trong menu công ty {tenant.Key} đều dựng được trên web.")
                : ErpMessage.Info($"Công ty {tenant.Key}: {gaps.Count} form trong menu web chưa dựng được " +
                                  $"({gaps.Count(g => g.Reason == "handcoded")} form cứng chưa có mô tả, {gaps.Count(g => g.Reason == "noconfig")} không có cấu hình form)."),
        };
        if (result.SkippedReports + result.SkippedCommands > 0)
            messages.Add(ErpMessage.Info($"Không tính {result.SkippedReports} báo cáo (mở bằng trình xem báo cáo, thuộc phần báo cáo) " +
                                         $"và {result.SkippedCommands} mục Thoát / Đăng xuất (web tự xử lý)."));
        return HtmlResults.Page(ScreenRenderer.Render(new ScreenModel("AdminForms", FormTypes.List, $"AdminForms:missing:{tenant.Key}",
            $"Form còn thiếu — {tenant.Key}", Messages: messages), w =>
        {
            ToolbarRenderer.Write(w, [new ToolbarItem("cancel", "Về danh sách mô tả", Href: ctx.Url("/admin/forms"), Target: "self")]);
            GridRenderer.Write(w, grid);
        }));
    }

    /// <summary>Thông điệp lỗi DB không lộ chi tiết máy chủ: lỗi kết nối / quá giờ đã có thông điệp tiếng Việt; lỗi SQL chỉ nêu mã.</summary>
    private static string Describe(Exception ex) => ex is Microsoft.Data.SqlClient.SqlException s ? $"lỗi SQL {s.Number}" : ex.Message;

    private static IResult NotFound() => HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy", ErpMessage.Error("Không có mô tả form này."));

    /// <summary>Mẫu khi thêm mô tả mới.</summary>
    private const string Template = """
        // Mô tả form viết tay. Xem các tệp mẫu trong danh sách (bấm một form "Có sẵn trong BE").
        {
          "form": "TenForm",
          "formType": "LIST",
          "caption": "Tên màn hình",
          "useDbConfig": false,
          "cfg": {
            "T0": {
              "TN": "TenBang",
              "PK": "CotKhoa",
              "SO": "TenBang.CotKhoa DESC",
              "TRS": "500"
            }
          }
        }
        """;
}
