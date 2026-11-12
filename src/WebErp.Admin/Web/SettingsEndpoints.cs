using WebErp.Config;
using WebErp.Core.Auth;
using WebErp.Core.Errors;
using WebErp.Core.Messages;
using WebErp.Html.Roles;
using WebErp.Web;

namespace WebErp.Admin.Web;

/// <summary>Cài đặt chung (hằng mật khẩu ERP, danh sách quản trị viên) và nhật ký thao tác quản trị.</summary>
public static class SettingsEndpoints
{
    private const string RecordId = "rec-settings";

    private static readonly Dictionary<string, string> ActionNames = new(StringComparer.Ordinal)
    {
        ["tenant.add"] = "Thêm công ty", ["tenant.update"] = "Sửa công ty", ["tenant.set-connection"] = "Đổi chuỗi kết nối",
        ["tenant.enable"] = "Bật công ty", ["tenant.disable"] = "Tắt công ty", ["tenant.maintenance"] = "Bảo trì",
        ["tenant.logout-all"] = "Đăng xuất mọi người", ["host.add"] = "Gắn domain", ["host.remove"] = "Bỏ domain",
        ["setting.set"] = "Đặt cài đặt", ["setting.clear"] = "Xóa cài đặt", ["admin.create"] = "Tạo quản trị viên", ["admin.remove"] = "Xóa quản trị viên",
    };

    /// <summary>Công ty và tài khoản ERP dùng để thử hằng (giữ lại trên form khi lỗi; mật khẩu thì không).</summary>
    private sealed record KeyTest(string Tenant = "", string User = "");

    public static void MapSettings(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin").RequireAuthorization();
        g.MapGet("/settings", (HttpContext ctx, TenantEndpoints.Deps d) => HtmlResults.Page(Settings(ctx, d, null, new KeyTest())));
        g.MapPost("/settings/save", SaveAsync);
        g.MapGet("/audit", (HttpContext ctx, TenantEndpoints.Deps d) => HtmlResults.Page(Audit(ctx, d)));
    }

    private static async Task<IResult> SaveAsync(HttpContext ctx, TenantEndpoints.Deps d)
    {
        var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return TenantEndpoints.Expired();
        var key = form["LegacyKey"].ToString();
        var test = new KeyTest(form["TestTenant"].ToString(), form["TestUser"].ToString().Trim());
        IResult Fail(ErpMessage m) => HtmlResults.Page(Settings(ctx, d, [m], test), StatusCodes.Status422UnprocessableEntity);
        if (key.Length == 0) return Fail(ErpMessage.Error("Chưa nhập hằng mật khẩu để lưu.", "LegacyKey"));
        if (key.Length != 6) return Fail(ErpMessage.Error("Hằng mật khẩu của ERP gồm đúng 6 ký tự.", "LegacyKey"));
        if (form["__version"].ToString() != d.Writer(ctx).SettingStamp(SettingNames.LegacyPasswordKey))
            return HtmlResults.Page(Settings(ctx, d, [ErpMessage.Error("Cài đặt vừa được người khác đổi. Xem lại rồi lưu lần nữa.")], test), StatusCodes.Status409Conflict);

        // Thử hằng bằng một tài khoản ERP thật TRƯỚC khi lưu: hằng sai thì mọi người dùng của mọi công ty đều bị báo sai mật khẩu,
        // mà trang này không hiện lại hằng đã lưu để soát.
        var tenants = TestTenants(ctx, d);
        string detail;
        var notes = new List<ErpMessage>();
        if (tenants.Count == 0)
        {
            detail = "chưa thử: chưa có công ty";
            notes.Add(ErpMessage.Warning("Chưa có công ty nào có chuỗi kết nối nên chưa thử được hằng. Thêm công ty rồi đăng nhập thử bằng một tài khoản ERP."));
        }
        else
        {
            var tenant = tenants.Any(t => t.Key == test.Tenant) ? await d.Store.GetAsync(test.Tenant, ctx.RequestAborted) : null;
            if (tenant is null) return Fail(ErpMessage.Error("Chọn công ty để thử hằng.", "TestTenant"));
            if (test.User.Length == 0) return Fail(ErpMessage.Error("Nhập một tài khoản ERP của công ty đã chọn để thử hằng.", "TestUser"));
            ErpLoginRecord? record;
            try
            {
                record = await d.Users.FindForLoginAsync(tenant, test.User, ctx.RequestAborted);
            }
            catch (ErpUnavailableException)
            {
                return Fail(ErpMessage.Error($"Không kết nối được DB của {tenant.Key} để thử hằng. Bấm Thử kết nối ở trang công ty.", "TestTenant"));
            }
            if (record is null) return Fail(ErpMessage.Error($"Không có tài khoản '{test.User}' (đang hoạt động) trong DB của {tenant.Key}.", "TestUser"));
            if (!LegacyPassword.Verify(test.User, form["TestPassword"].ToString(), record.PasswordHash, key))
                return Fail(ErpMessage.Error("Hằng hoặc mật khẩu ERP không khớp với tài khoản này nên CHƯA lưu. Kiểm lại hằng (6 ký tự trong mã ERP) " +
                                             "và mật khẩu đang đăng nhập được WinForms.", "LegacyKey"));
            detail = $"đã thử với {tenant.Key}/{record.User.UserName}";
        }

        if (!d.Accounts.VerifyCode(AdminAuth.UserName(ctx), form["Code"].ToString()))
            return Fail(ErpMessage.Error("Mã xác thực không đúng hoặc đã dùng.", "Code"));
        d.Writer(ctx).SetSetting(SettingNames.LegacyPasswordKey, key, detail);
        return HtmlResults.Page(Settings(ctx, d,
            [ErpMessage.Success($"Đã lưu hằng mật khẩu ({detail}). Host áp dụng trong vài giây."), .. notes], new KeyTest(test.Tenant)));
    }

    /// <summary>Công ty đang bật và có chuỗi kết nối: thử hằng được.</summary>
    private static List<WebErp.Core.Tenancy.Tenant> TestTenants(HttpContext ctx, TenantEndpoints.Deps d) =>
        d.Writer(ctx).List().Where(t => t.Tenant.Enabled && t.HasConnection).Select(t => t.Tenant).ToList();

    private static string Settings(HttpContext ctx, TenantEndpoints.Deps d, IReadOnlyList<ErpMessage>? messages, KeyTest test)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        var isSet = d.Writer(ctx).HasSetting(SettingNames.LegacyPasswordKey);
        var admins = d.Accounts.List();
        var tenants = TestTenants(ctx, d);
        var keyFields = new List<FieldModel>
        {
            new("LegacyKeyStatus", "Hằng mật khẩu ERP", "label", isSet ? "Đã đặt" : "CHƯA ĐẶT — người dùng ERP chưa đăng nhập được", Span: 40),
            new("LegacyKey", isSet ? "Đặt lại hằng mật khẩu" : "Hằng mật khẩu", "password", Span: 20, MaxLength: 6, Autocomplete: "off", RowStart: true,
                Hint: "6 ký tự trong thuật toán mật khẩu của ERP (tài liệu 12 §2.5). Lưu mã hóa, không hiện lại."),
        };
        if (tenants.Count > 0)
        {
            var options = new List<(string, string)>();
            if (tenants.Count > 1) options.Add(("", "— Chọn công ty —"));
            options.AddRange(tenants.Select(t => (t.Key, $"{t.Key} — {t.Name}")));
            keyFields.Add(new FieldModel("TestTenant", "Thử với công ty", "select", tenants.Count == 1 ? tenants[0].Key : test.Tenant, Span: 20, Required: true,
                Options: options,
                Hint: "Trước khi lưu, BE thử hằng với một tài khoản ERP thật để tránh lưu nhầm."));
            keyFields.Add(new FieldModel("TestUser", "Tài khoản ERP để thử", Value: test.User, Span: 20, Required: true, MaxLength: 100, Autocomplete: "off", RowStart: true));
            keyFields.Add(new FieldModel("TestPassword", "Mật khẩu ERP của tài khoản đó", "password", Span: 20, Autocomplete: "off",
                Hint: "Chỉ dùng để thử, không lưu."));
        }
        keyFields.Add(AccountEndpoints.CodeField() with { Span = 20, Required = false, RowStart = true });

        return ScreenRenderer.Render(new ScreenModel("AdminSettings", FormTypes.Edit, "AdminSettings", "Cài đặt chung", Mode: "edit",
            RefreshSrc: ctx.Url("/admin/settings"), Messages: messages), w =>
        {
            ToolbarRenderer.Write(w, [new ToolbarItem("save", "Lưu", Form: RecordId, FormAction: ctx.Url("/admin/settings/save"), Shortcut: "Ctrl+S")]);
            RecordRenderer.Write(w, new RecordModel(RecordId, ctx.Url("/admin/settings/save"), field, token,
                [new FieldsetModel("Đăng nhập bằng tài khoản ERP", keyFields)],
                Version: d.Writer(ctx).SettingStamp(SettingNames.LegacyPasswordKey)));
            GridRenderer.Write(w, new GridModel("Admins",
            [
                new GridColumn("UserName", "Quản trị viên", Width: 160),
                new GridColumn("CreatedAt", "Tạo lúc", "datetime", 140),
                new GridColumn("LastLoginAt", "Đăng nhập lần cuối", "datetime", 140),
            ], admins.Select(a => new GridRow(a.UserName,
                [new GridCell(a.UserName), GridCell.DateTime(a.CreatedAt, d.Zone), GridCell.DateTime(a.LastLoginAt, d.Zone)])).ToList(),
                KeyField: "UserName", Caption: "Tài khoản quản trị (thêm / xóa bằng WebErp.Admin.exe trên VPS)"));
        });
    }

    private static string Audit(HttpContext ctx, TenantEndpoints.Deps d)
    {
        var entries = d.Writer(ctx).ReadAudit(300);
        return ScreenRenderer.Render(new ScreenModel("AdminAudit", FormTypes.List, "AdminAudit", "Nhật ký thao tác"), w =>
        {
            ToolbarRenderer.Write(w, [new ToolbarItem("reload", "Nạp lại", Href: ctx.Url("/admin/audit"), Target: "self")]);
            GridRenderer.Write(w, new GridModel("T0",
            [
                new GridColumn("At", "Thời gian", "datetime", 140),
                new GridColumn("Actor", "Người làm", Width: 220),
                new GridColumn("Action", "Thao tác", Width: 160),
                new GridColumn("Target", "Đối tượng", Width: 140),
                new GridColumn("Detail", "Chi tiết", Width: 320, Wrap: true),
            ], entries.Select(e => new GridRow(e.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [
                GridCell.DateTime(e.At, d.Zone), new GridCell(e.Actor), new GridCell(ActionNames.GetValueOrDefault(e.Action, e.Action), e.Action),
                new GridCell(e.Target), new GridCell(e.Detail),
            ])).ToList(), KeyField: "Id", Sort: "At:desc"));
        });
    }
}
