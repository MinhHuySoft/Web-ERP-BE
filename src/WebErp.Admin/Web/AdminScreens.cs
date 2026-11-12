using System.Globalization;
using WebErp.Config;
using WebErp.Core.Messages;
using WebErp.Html;
using WebErp.Html.Roles;
using WebErp.Web;

namespace WebErp.Admin.Web;

/// <summary>Giá trị form công ty (giữ lại khi trả form có lỗi; không bao giờ giữ chuỗi kết nối hay mã xác thực).</summary>
public sealed record TenantFormValues(string Key, string Name, string Module, string? TaxCode, string? DbLabel, string Hosts, bool Enabled, string? Version)
{
    public static TenantFormValues Empty => new("", "", "SY", null, null, "", true, null);

    public static TenantFormValues From(TenantDetail d) =>
        new(d.Tenant.Key, d.Tenant.Name, d.Tenant.ModuleId, d.Tenant.TaxCode, d.Tenant.DatabaseLabel, string.Join('\n', d.Hosts), d.Tenant.Enabled, d.Revision);

    public static TenantFormValues From(IFormCollection f, string? key = null) =>
        new(key ?? f["Key"].ToString().Trim(), f["Name"].ToString(), f["Module"].ToString().Trim(), f["TaxCode"].ToString(), f["DbLabel"].ToString(),
            f["Hosts"].ToString(), f["Enabled"].LastOrDefault() == "true", f["__version"].ToString() is { Length: > 0 } v ? v : null);

    public IReadOnlyList<string> HostList => Hosts.Split(['\n', '\r', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Dựng các màn ERP-HTML của trang quản trị.</summary>
public static class AdminScreens
{
    public const string RecordId = "rec-tenant";

    public static TimeZoneInfo Zone(AdminOptions o)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(o.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Local;
        }
    }

    public static string Time(DateTimeOffset? t, TimeZoneInfo zone) =>
        t is null ? "" : TimeZoneInfo.ConvertTime(t.Value, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string TenantList(HttpContext ctx, IReadOnlyList<TenantSummary> tenants, TimeProvider time, IReadOnlyList<ErpMessage>? messages = null)
    {
        var now = time.GetUtcNow();
        var grid = new GridModel("T0",
        [
            new GridColumn("Key", "Mã", "link", 120, Frozen: true),
            new GridColumn("Name", "Tên công ty", Width: 260),
            new GridColumn("Hosts", "Domain", Width: 260, Wrap: true),
            new GridColumn("Module", "Module", Width: 70),
            new GridColumn("HasConnection", "Có chuỗi kết nối", "bool", 90),
            new GridColumn("Status", "Trạng thái", "status", 110),
        ], tenants.Select(t => new GridRow(t.Tenant.Key,
        [
            new GridCell(t.Tenant.Key, t.Tenant.Key, ctx.Url($"/admin/tenants/{Uri.EscapeDataString(t.Tenant.Key)}"), "tab"),
            new GridCell(t.Tenant.Name),
            new GridCell(string.Join(", ", t.Hosts)),
            new GridCell(t.Tenant.ModuleId),
            GridCell.Bool(t.HasConnection),
            Status(t.Tenant.Enabled, t.Tenant.InMaintenance(now)),
        ])).ToList(), KeyField: "Key", Selectable: "single");

        return ScreenRenderer.Render(new ScreenModel("AdminTenants", FormTypes.List, "AdminTenants", "Công ty", Messages: messages), w =>
        {
            ToolbarRenderer.Write(w,
            [
                new ToolbarItem("new", "Thêm công ty", Href: ctx.Url("/admin/tenants/new"), Target: "tab"),
                new ToolbarItem("reload", "Nạp lại", Href: ctx.Url("/admin/tenants"), Target: "self"),
            ]);
            GridRenderer.Write(w, grid);
        });
    }

    public static string TenantForm(HttpContext ctx, TenantFormValues v, bool isNew, TenantDetail? detail, TimeZoneInfo zone, TimeProvider time,
        IReadOnlyList<ErpMessage>? messages = null)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        var key = Uri.EscapeDataString(v.Key);
        var basePath = isNew ? "/admin/tenants/new" : $"/admin/tenants/{key}";
        var info = new List<FieldModel>
        {
            new("Key", "Mã công ty", Value: v.Key, Span: 12, Required: true, ReadOnly: !isNew, MaxLength: 40, Pattern: "[A-Za-z0-9_\\-]{1,40}",
                Hint: isNew ? "Chữ, số, '_' hoặc '-'. Không đổi được sau khi tạo." : null),
            new("Name", "Tên công ty", Value: v.Name, Span: 28, Required: true, MaxLength: 200),
            new("Module", "Module ERP", Value: v.Module, Span: 8, Required: true, MaxLength: 5, RowStart: true, Hint: "Thường là SY."),
            new("TaxCode", "Mã số thuế", Value: v.TaxCode, Span: 12, MaxLength: 20),
            new("DbLabel", "Nhãn dữ liệu", Value: v.DbLabel, Span: 20, MaxLength: 100, Hint: "Hiện ở thanh trạng thái, vd tên DB."),
            new("Hosts", "Domain", "textarea", v.Hosts, Span: 40, Rows: 3, RowStart: true,
                Hint: "Mỗi dòng một domain, vd congty.bms7.net. Cần bản ghi DNS và chứng chỉ HTTPS cho từng domain trên VPS."),
            new("Enabled", "Đang bật", "checkbox", v.Enabled ? "true" : "false", Span: 10, RowStart: true),
        };
        var tabs = new List<FieldsetModel> { new("Thông tin công ty", info) };
        if (isNew)
        {
            tabs.Add(new FieldsetModel("Kết nối DB và xác nhận",
            [
                new FieldModel("Connection", "Chuỗi kết nối SQL Server", "password", Span: 40, Required: true, Autocomplete: "off",
                    Hint: "Dán chuỗi kết nối, phải có Database=<tên DB>. Máy chủ ở xa thì BE tự bật mã hóa. Chuỗi được mã hóa, không hiện lại."),
                AccountEndpoints.CodeField(),
            ]));
        }
        else if (detail is not null)
        {
            var now = time.GetUtcNow();
            tabs.Add(new FieldsetModel("Trạng thái",
            [
                new FieldModel("StatusConnection", "Chuỗi kết nối", "label", detail.HasConnection ? "Đã đặt" : "CHƯA ĐẶT", Span: 13),
                new FieldModel("StatusMaintenance", "Bảo trì", "label",
                    detail.Tenant.InMaintenance(now) ? $"Đến {Time(detail.Tenant.MaintenanceUntil, zone)}" : "Không", Span: 13),
                new FieldModel("StatusLogout", "Đăng xuất mọi người lần cuối", "label",
                    detail.Tenant.LogoutBefore is null ? "Chưa lần nào" : Time(detail.Tenant.LogoutBefore, zone), Span: 14),
            ]));
        }

        var toolbar = new List<ToolbarItem> { new("save", isNew ? "Tạo công ty" : "Lưu", Form: RecordId, FormAction: ctx.Url(isNew ? basePath : $"{basePath}/save"), Shortcut: "Ctrl+S") };
        if (!isNew)
        {
            toolbar.Add(new ToolbarItem("custom", "Thử kết nối", Form: RecordId, FormAction: ctx.Url($"{basePath}/test"), Icon: "system"));
            toolbar.Add(new ToolbarItem("custom", "Đổi chuỗi kết nối", Href: ctx.Url($"{basePath}/connection"), Target: "dialog", Icon: "key"));
            toolbar.Add(new ToolbarItem("custom", "Bảo trì", Href: ctx.Url($"{basePath}/maintenance"), Target: "dialog", Icon: "lock"));
            toolbar.Add(new ToolbarItem("custom", "Đăng xuất mọi người", Href: ctx.Url($"{basePath}/logout-all"), Target: "dialog", Icon: "users"));
        }
        toolbar.Add(new ToolbarItem("cancel", "Về danh sách", Href: ctx.Url("/admin/tenants"), Target: "self"));

        var screenKey = isNew ? "AdminTenants:new" : $"AdminTenants:{v.Key}";
        return ScreenRenderer.Render(new ScreenModel("AdminTenants", FormTypes.Edit, screenKey, isNew ? "Thêm công ty" : $"Công ty {v.Key}",
            Mode: isNew ? "new" : "edit", RefreshSrc: isNew ? null : ctx.Url(basePath), Messages: messages), w =>
        {
            ToolbarRenderer.Write(w, toolbar);
            RecordRenderer.Write(w, new RecordModel(RecordId, ctx.Url(isNew ? basePath : $"{basePath}/save"), field, token, tabs,
                KeyField: "Key", Key: isNew ? null : v.Key, Version: isNew ? null : v.Version));
        });
    }

    public static GridCell Status(bool enabled, bool maintenance) =>
        !enabled ? new GridCell("Tắt", "off", StatusTone: "muted")
        : maintenance ? new GridCell("Bảo trì", "maintenance", StatusTone: "warning")
        : new GridCell("Đang chạy", "on", StatusTone: "success");
}
