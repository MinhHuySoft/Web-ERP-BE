using System.Globalization;
using WebErp.Config;
using WebErp.Core.Messages;
using WebErp.Core.Tenancy;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;
using WebErp.Html.Roles;
using WebErp.Web;

namespace WebErp.Admin.Web;

/// <summary>
/// "Tạo nhanh" mô tả form cứng: chọn bảng / cột / sắp xếp / tab chi tiết bằng ô chọn và ô tích, hệ thống tự sinh JSON. Có gợi ý tự đoán
/// theo tên, gợi ý bằng AI (Claude hoặc Gemini, chỉ gửi tên bảng / cột), xem thử vài dòng dữ liệu thật, lưu cần mã TOTP.
/// Mọi truy vấn DB công ty đều chỉ đọc.
/// </summary>
public static class QuickEndpoints
{
    public const string RecordId = "rec-quick";
    public const int MaxDetails = 3;
    private const int DefaultColumns = 12;

    public static void MapQuick(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/admin/forms").RequireAuthorization();
        g.MapGet("/quick", StartAsync);
        g.MapPost("/quick/refresh", (HttpContext ctx, Deps d) => PostAsync(ctx, d, Op.Refresh));
        g.MapPost("/quick/guess", (HttpContext ctx, Deps d) => PostAsync(ctx, d, Op.Guess));
        g.MapPost("/quick/ai", (HttpContext ctx, Deps d) => PostAsync(ctx, d, Op.Ai));
        g.MapPost("/quick/preview", (HttpContext ctx, Deps d) => PostAsync(ctx, d, Op.Preview));
        g.MapPost("/quick/json", (HttpContext ctx, Deps d) => PostAsync(ctx, d, Op.Json));
        g.MapPost("/quick/save", (HttpContext ctx, Deps d) => PostAsync(ctx, d, Op.Save));
        g.MapGet("/ai-settings", async (HttpContext ctx, Deps d) => HtmlResults.Page(await AiDialogAsync(ctx, d, null)));
        g.MapPost("/ai-settings", SaveAiAsync);
    }

    public sealed class Deps(FormEndpoints.Deps forms, SchemaExplorer schema, AiAssistant ai)
    {
        public FormEndpoints.Deps Forms { get; } = forms;
        public SchemaExplorer Schema { get; } = schema;
        public AiAssistant Ai { get; } = ai;
        public SqliteTenantStore Store => Forms.Tenants.Store;

        public ConfigWriter Writer(HttpContext ctx) => Forms.Writer(ctx);
    }

    private enum Op { Refresh, Guess, Ai, Preview, Json, Save }

    /// <summary>Lựa chọn cho một bảng (chính hoặc chi tiết). <see cref="Prev"/>: bảng lúc vẽ trang trước, để biết người dùng vừa đổi bảng.</summary>
    private sealed class Pick
    {
        public string Table = "";
        public string? Prev;
        public string? Pk;
        public string? Sort;
        public bool Desc = true;
        public string? Caption;
        public string? MasterKey;
        public string? DetailKey;
        public List<string> Columns = [];
        public IReadOnlyList<DbColumn> Schema = [];
    }

    private sealed class State
    {
        public string Form = "";
        public string Tenant = "";
        public string Caption = "";
        public string FormType = "EDIT";
        public int Top = 500;
        public bool AllCompanies;
        public Pick Master = new();
        public Pick[] Details = [new(), new(), new()];
    }

    private static async Task<IResult> StartAsync(HttpContext ctx, Deps d, string? form, string? tenant)
    {
        if (!ConfigWriter.IsValidFormName(form)) return HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy", ErpMessage.Error("Thiếu tên form."));
        var t = string.IsNullOrEmpty(tenant) ? null : await d.Store.GetAsync(tenant, ctx.RequestAborted);
        if (t is null) return HtmlResults.Page(await ChooseTenantAsync(ctx, d, form!), StatusCodes.Status200OK);
        var s = new State { Form = form!, Tenant = t.Key };
        var messages = new List<ErpMessage>();
        try
        {
            s.Caption = await d.Schema.MenuCaptionAsync(t, s.Form, ctx.RequestAborted) ?? s.Form;
            await GuessAsync(d, t, s, messages, ctx.RequestAborted);
            await LoadAsync(d, t, s, ctx.RequestAborted);
        }
        catch (Exception ex) when (Db(ex))
        {
            messages.Add(ErpMessage.Error($"Không đọc được DB công ty {t.Key}: {Describe(ex)}"));
        }
        return HtmlResults.Page(await ScreenAsync(ctx, d, t, s, messages, []));
    }

    /// <summary>Chưa chọn công ty: trang chọn công ty để đọc cấu trúc DB.</summary>
    private static async Task<string> ChooseTenantAsync(HttpContext ctx, Deps d, string form)
    {
        var tenants = d.Writer(ctx).List();
        await Task.CompletedTask;
        return ScreenRenderer.Render(new ScreenModel("AdminForms", FormTypes.List, $"AdminForms:quick:{form}", $"Tạo nhanh {form} — chọn công ty",
            Messages: [ErpMessage.Info("Chọn công ty để đọc bảng / cột trong DB của công ty đó.")]), w =>
        {
            ToolbarRenderer.Write(w, [new ToolbarItem("cancel", "Về danh sách mô tả", Href: ctx.Url("/admin/forms"), Target: "self")]);
            GridRenderer.Write(w, new GridModel("T0", [new GridColumn("Key", "Công ty", "link", 200), new GridColumn("Name", "Tên", Width: 300)],
                tenants.Select(x => new GridRow(x.Tenant.Key,
                [
                    new GridCell(x.Tenant.Key, x.Tenant.Key, ctx.Url($"/admin/forms/quick?form={Uri.EscapeDataString(form)}&tenant={Uri.EscapeDataString(x.Tenant.Key)}"), "self"),
                    new GridCell(x.Tenant.Name),
                ])).ToList(), KeyField: "Key"));
        });
    }

    private static async Task<IResult> PostAsync(HttpContext ctx, Deps d, Op op)
    {
        var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return TenantEndpoints.Expired();
        var s = Read(f);
        if (!ConfigWriter.IsValidFormName(s.Form)) return HtmlResults.Error(StatusCodes.Status400BadRequest, "Sai dữ liệu", ErpMessage.Error("Tên form không hợp lệ."));
        var t = await d.Store.GetAsync(s.Tenant, ctx.RequestAborted);
        if (t is null) return HtmlResults.Error(StatusCodes.Status404NotFound, "Không tìm thấy", ErpMessage.Error("Không có công ty này."));
        var messages = new List<ErpMessage>();
        IReadOnlyList<QuickPreviewTable> preview = [];
        var status = StatusCodes.Status200OK;
        try
        {
            switch (op)
            {
                case Op.Guess:
                    await GuessAsync(d, t, s, messages, ctx.RequestAborted);
                    break;
                case Op.Ai:
                    await AiAsync(d, t, s, messages, ctx.RequestAborted);
                    break;
            }
            await LoadAsync(d, t, s, ctx.RequestAborted);
            if (op is Op.Preview or Op.Json or Op.Save)
            {
                var (spec, error) = Spec(s);
                if (spec is null)
                {
                    messages.Add(ErpMessage.Error(error!));
                    status = StatusCodes.Status422UnprocessableEntity;
                }
                else if (op == Op.Preview)
                {
                    preview = await d.Schema.PreviewAsync(t, spec, ctx.RequestAborted);
                    messages.Add(preview.All(p => p.Error is null)
                        ? ErpMessage.Success($"Xem thử: dữ liệu thật của {t.Key}, tối đa {SchemaExplorer.PreviewRows} dòng mỗi bảng (chi tiết lấy theo dòng đầu). Đúng thì nhập mã xác thực rồi bấm Lưu.")
                        : ErpMessage.Warning("Có bảng chưa chạy được, xem lỗi dưới từng bảng."));
                }
                else if (op == Op.Json)
                {
                    return FormEndpoints.JsonEditor(ctx, d.Forms, s.Form, s.AllCompanies ? null : t.Key, spec.ToJson(),
                        [ErpMessage.Info("JSON sinh từ Tạo nhanh. Sửa thêm nếu cần (lọc, cột tính toán…), rồi Kiểm và Lưu.")]);
                }
                else
                {
                    return await SaveAsync(ctx, d, f, t, s, spec);
                }
            }
        }
        catch (Exception ex) when (Db(ex))
        {
            messages.Add(ErpMessage.Error($"Không đọc được DB công ty {t.Key}: {Describe(ex)}"));
        }
        return HtmlResults.Page(await ScreenAsync(ctx, d, t, s, messages, preview), status);
    }

    private static async Task<IResult> SaveAsync(HttpContext ctx, Deps d, IFormCollection f, Tenant t, State s, QuickSpec spec)
    {
        var json = spec.ToJson();
        var check = FormOverlays.Validate(json);
        if (check.Error is not null)
            return HtmlResults.Page(await ScreenAsync(ctx, d, t, s, [ErpMessage.Error(check.Error)], []), StatusCodes.Status422UnprocessableEntity);
        if (!d.Forms.Tenants.Accounts.VerifyCode(AdminAuth.UserName(ctx), f["Code"].ToString()))
            return HtmlResults.Page(await ScreenAsync(ctx, d, t, s, [ErpMessage.Error("Mã xác thực không đúng hoặc đã dùng. Nhập mã mới đang hiện rồi bấm Lưu.", "Code")], []),
                StatusCodes.Status422UnprocessableEntity);
        var scope = s.AllCompanies ? null : t.Key;
        try
        {
            var writer = d.Writer(ctx);
            var current = writer.GetFormOverlay(s.Form, scope);
            var revision = writer.SaveFormOverlay(s.Form, scope, json, true, "Tạo nhanh", current?.Revision);
            return FormEndpoints.Edit(ctx, d.Forms, s.Form, scope,
            [
                ErpMessage.Success($"Đã lưu mô tả {s.Form} (phiên bản {revision}) cho {(scope is null ? "mọi công ty" : $"công ty {scope}")}. " +
                                   "Web áp dụng trong vài giây: mở menu của công ty để xem."),
            ]);
        }
        catch (ConfigConflictException ex)
        {
            return HtmlResults.Page(await ScreenAsync(ctx, d, t, s, [ErpMessage.Error(ex.Message)], []), StatusCodes.Status409Conflict);
        }
    }

    /// <summary>Đoán bảng theo tên form, khóa chính, cột nối chi tiết; cột hiển thị chọn sẵn ở <see cref="LoadAsync"/>.</summary>
    private static async Task GuessAsync(Deps d, Tenant t, State s, List<ErpMessage> messages, CancellationToken ct)
    {
        var objects = await d.Schema.ObjectsAsync(t, ct);
        var g = QuickGuesser.Guess(s.Form, objects);
        if (g.MasterTable is null)
        {
            messages.Add(ErpMessage.Warning($"Không đoán được bảng theo tên {s.Form}. Chọn bảng chính trong danh sách, hoặc bấm \"Gợi ý bằng AI\"."));
            return;
        }
        s.Master = new Pick { Table = g.MasterTable };
        s.Details = [new(), new(), new()];
        var i = 0;
        foreach (var dt in g.DetailTables.Take(MaxDetails)) s.Details[i++] = new Pick { Table = dt };
        messages.Add(ErpMessage.Info($"Đoán theo tên: bảng chính {g.MasterTable}" +
                                     (g.DetailTables.Count > 0 ? $", chi tiết {string.Join(", ", g.DetailTables.Take(MaxDetails))}" : ", không thấy bảng chi tiết") +
                                     ". Kiểm lại rồi bấm Xem thử."));
    }

    private static async Task AiAsync(Deps d, Tenant t, State s, List<ErpMessage> messages, CancellationToken ct)
    {
        var objects = await d.Schema.ObjectsAsync(t, ct);
        var names = QuickGuesser.Rank(s.Form, objects).Take(25).ToList();
        foreach (var chosen in new[] { s.Master.Table }.Concat(s.Details.Select(x => x.Table)))
            if (chosen.Length > 0 && !names.Contains(chosen, StringComparer.OrdinalIgnoreCase) && objects.Contains(chosen, StringComparer.OrdinalIgnoreCase)) names.Add(chosen);
        var tables = new List<AiTable>();
        foreach (var n in names) tables.Add(new AiTable(n, await d.Schema.ColumnsAsync(t, n, s.Form, ct)));
        var (suggestion, error) = await d.Ai.SuggestAsync(s.Form, s.Caption, tables, ct);
        if (suggestion?.Master is not { } m)
        {
            messages.Add(ErpMessage.Error(error ?? "AI không gợi ý được."));
            return;
        }
        var (provider, model, _) = await d.Ai.SettingsAsync(ct);
        if (!string.IsNullOrWhiteSpace(suggestion.Caption)) s.Caption = suggestion.Caption.Trim();
        s.Master = new Pick { Table = m.Table, Prev = m.Table, Pk = m.PrimaryKey, Sort = m.SortColumn, Desc = m.SortDesc, Columns = [.. m.Columns] };
        s.Details = [new(), new(), new()];
        var i = 0;
        foreach (var x in suggestion.Details.Take(MaxDetails))
            s.Details[i++] = new Pick { Table = x.Table, Prev = x.Table, Pk = x.PrimaryKey, Caption = x.Caption, MasterKey = x.MasterKey, DetailKey = x.DetailKey, Columns = [.. x.Columns] };
        messages.Add(ErpMessage.Info($"Gợi ý của {AiProviders.Name(provider)} ({model}): bảng chính {m.Table}" +
                                     (suggestion.Details.Count > 0 ? $", chi tiết {string.Join(", ", suggestion.Details.Select(x => x.Table))}" : "") +
                                     (string.IsNullOrWhiteSpace(suggestion.Note) ? "." : $". {suggestion.Note}") + " Kiểm lại rồi bấm Xem thử."));
    }

    /// <summary>Nạp cột của các bảng đã chọn; bảng vừa đổi thì chọn sẵn khóa chính, sắp xếp, cột nối và vài cột hiển thị.</summary>
    private static async Task LoadAsync(Deps d, Tenant t, State s, CancellationToken ct)
    {
        if (s.Master.Table.Length > 0)
        {
            var m = s.Master;
            m.Schema = await d.Schema.ColumnsAsync(t, m.Table, s.Form, ct);
            var names = m.Schema.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = !string.Equals(m.Prev, m.Table, StringComparison.OrdinalIgnoreCase);
            if (changed || m.Pk is null || !names.Contains(m.Pk)) m.Pk = m.Schema.FirstOrDefault(c => c.IsPrimaryKey)?.Name ?? m.Schema.FirstOrDefault()?.Name;
            if (changed || m.Sort is not null && !names.Contains(m.Sort)) m.Sort = SortGuess(m.Schema);
            m.Columns = changed && m.Columns.Count == 0 ? DefaultCols(m.Schema, m.Pk) : m.Columns.Where(names.Contains).ToList();
            m.Prev = m.Table;
        }
        foreach (var x in s.Details)
        {
            if (x.Table.Length == 0) { x.Schema = []; x.Columns = []; continue; }
            x.Schema = await d.Schema.ColumnsAsync(t, x.Table, s.Form, ct);
            var names = x.Schema.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = !string.Equals(x.Prev, x.Table, StringComparison.OrdinalIgnoreCase);
            if (changed || x.Pk is null || !names.Contains(x.Pk)) x.Pk = x.Schema.FirstOrDefault(c => c.IsPrimaryKey)?.Name ?? x.Schema.FirstOrDefault()?.Name;
            if ((changed || x.DetailKey is null) && s.Master.Table.Length > 0
                && await d.Schema.LinkAsync(t, s.Master.Table, x.Table, s.Master.Pk, ct) is { } link)
            {
                x.MasterKey = link.MasterKey;
                x.DetailKey = link.DetailKey;
            }
            if (x.DetailKey is not null && !names.Contains(x.DetailKey)) x.DetailKey = null;
            if (changed && string.IsNullOrWhiteSpace(x.Caption)) x.Caption = "Chi tiết";
            x.Columns = changed && x.Columns.Count == 0 ? DefaultCols(x.Schema, x.Pk, x.DetailKey) : x.Columns.Where(names.Contains).ToList();
            x.Prev = x.Table;
        }
    }

    private static readonly string[] SkipByDefault = ["UserAutoID", "UID", "ParentID", "isSyn", "UserCreate", "UserUpdate", "DateCreate", "DateUpdate", "rowguid"];

    /// <summary>Cột chọn sẵn: ưu tiên cột có tiêu đề tiếng Việt của ERP, bỏ cột kỹ thuật, tối đa <see cref="DefaultColumns"/>.</summary>
    private static List<string> DefaultCols(IReadOnlyList<DbColumn> cols, params string?[] skip) =>
        cols.Where(c => !skip.Contains(c.Name, StringComparer.OrdinalIgnoreCase) && !SkipByDefault.Contains(c.Name, StringComparer.OrdinalIgnoreCase)
                        && c.Type is not ("image" or "varbinary" or "binary" or "timestamp" or "xml"))
            .OrderBy(c => string.IsNullOrWhiteSpace(c.Caption) ? 1 : 0)
            .Take(DefaultColumns)
            .OrderBy(c => cols.ToList().IndexOf(c))
            .Select(c => c.Name).ToList();

    private static string? SortGuess(IReadOnlyList<DbColumn> cols) =>
        cols.FirstOrDefault(c => c.Name.Equals("DocumentDate", StringComparison.OrdinalIgnoreCase))?.Name
        ?? cols.FirstOrDefault(c => c.Type is "date" or "datetime" or "datetime2" or "smalldatetime" && !SkipByDefault.Contains(c.Name, StringComparer.OrdinalIgnoreCase))?.Name;

    private static (QuickSpec? Spec, string? Error) Spec(State s)
    {
        var m = s.Master;
        if (m.Table.Length == 0 || m.Pk is null) return (null, "Chọn bảng chính và khóa chính.");
        if (m.Columns.Count == 0) return (null, "Tích ít nhất một cột hiển thị của bảng chính.");
        var details = new List<QuickSlot>();
        var n = 1;
        foreach (var x in s.Details)
        {
            if (x.Table.Length == 0) { n++; continue; }
            if (x.Pk is null || x.MasterKey is null || x.DetailKey is null) return (null, $"Tab chi tiết {n}: chọn khóa chính và hai cột nối.");
            if (x.Columns.Count == 0) return (null, $"Tab chi tiết {n}: tích ít nhất một cột.");
            details.Add(new QuickSlot(x.Table, x.Pk, x.Columns, null, true, x.Caption, x.MasterKey, x.DetailKey));
            n++;
        }
        var caption = string.IsNullOrWhiteSpace(s.Caption) ? s.Form : s.Caption.Trim();
        return (new QuickSpec(s.Form, caption, details.Count > 0 ? "EDIT" : s.FormType, s.Top,
            new QuickSlot(m.Table, m.Pk, m.Columns, m.Sort, m.Desc), details), null);
    }

    private static State Read(IFormCollection f)
    {
        string? V(string k) => f[k].ToString() is { Length: > 0 } v ? v.Trim() : null;
        bool B(string k) => f[k].LastOrDefault() == "true";
        Pick P(string p)
        {
            var x = new Pick
            {
                Table = V($"{p}.Table") ?? "", Prev = V($"{p}.Prev"), Pk = V($"{p}.Pk"), Sort = V($"{p}.Sort"), Desc = B($"{p}.Desc"),
                Caption = V($"{p}.Caption"), MasterKey = V($"{p}.MK"), DetailKey = V($"{p}.DK"),
            };
            var prefix = $"{p}.Col.";
            x.Columns = f.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && f[k].LastOrDefault() == "true").Select(k => k[prefix.Length..]).ToList();
            return x;
        }
        return new State
        {
            Form = V("Form") ?? "", Tenant = V("Tenant") ?? "", Caption = V("Caption") ?? "", FormType = V("FormType") == "LIST" ? "LIST" : "EDIT",
            Top = int.TryParse(V("Top"), NumberStyles.None, CultureInfo.InvariantCulture, out var top) ? Math.Clamp(top, 0, 5000) : 500,
            AllCompanies = V("Scope") == "all",
            Master = P("M"),
            Details = [P("D1"), P("D2"), P("D3")],
        };
    }

    private static async Task<string> ScreenAsync(HttpContext ctx, Deps d, Tenant t, State s, IReadOnlyList<ErpMessage> messages, IReadOnlyList<QuickPreviewTable> preview)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        IReadOnlyList<string> objects;
        try
        {
            objects = await d.Schema.ObjectsAsync(t, ctx.RequestAborted);
        }
        catch (Exception ex) when (Db(ex))
        {
            objects = [];
        }
        var ranked = QuickGuesser.Rank(s.Form, objects).Take(30).ToList();
        IReadOnlyList<(string, string)> TableOptions(string current, bool blank)
        {
            var list = new List<(string, string)>();
            if (blank) list.Add(("", "(không có)"));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in (current.Length > 0 ? [current] : Array.Empty<string>()).Concat(ranked).Concat(objects))
                if (o.Length > 0 && seen.Add(o)) list.Add((o, o));
            return list;
        }
        static IReadOnlyList<(string, string)> ColOptions(IReadOnlyList<DbColumn> cols, bool blank) =>
            (blank ? new[] { ("", "(không)") } : []).Concat(cols.Select(c => (c.Name, Label(c)))).ToList();

        var head = new List<FieldModel>
        {
            new("Form", "Form", Value: s.Form, Span: 12, ReadOnly: true),
            new("Tenant", "Công ty (đọc cấu trúc DB)", Value: t.Key, Span: 10, ReadOnly: true),
            new("Caption", "Tiêu đề màn", Value: s.Caption, Span: 18, MaxLength: 200),
            new("FormType", "Kiểu", "select", s.FormType, Span: 10, RowStart: true, Options: [("EDIT", "Danh sách + mở chứng từ"), ("LIST", "Chỉ danh sách")],
                Hint: "Có tab chi tiết thì luôn là \"Danh sách + mở chứng từ\"."),
            new("Top", "Số dòng tối đa", "number", s.Top.ToString(CultureInfo.InvariantCulture), Span: 8, Hint: "0 = phân trang, không giới hạn."),
            new("Scope", "Áp cho", "select", s.AllCompanies ? "all" : "tenant", Span: 12, Options: [("tenant", $"Riêng công ty {t.Key}"), ("all", "Mọi công ty")]),
        };
        var m = s.Master;
        var master = new List<FieldModel>
        {
            new("M.Table", "Bảng chính", "select", m.Table, Span: 20, Options: TableOptions(m.Table, true),
                Hint: "Đổi bảng xong bấm \"Nạp lại cột\". Bảng hợp tên form đứng đầu danh sách."),
            new("M.Prev", "Bảng đã nạp", Value: m.Prev, Span: 10, ReadOnly: true),
            new("M.Pk", "Khóa chính", "select", m.Pk ?? "", Span: 10, Options: ColOptions(m.Schema, false)),
            new("M.Sort", "Sắp xếp theo", "select", m.Sort ?? "", Span: 14, RowStart: true, Options: ColOptions(m.Schema, true)),
            new("M.Desc", "Mới nhất / lớn nhất trước", "checkbox", m.Desc ? "true" : "false", Span: 14),
        };
        master.AddRange(Checks("M", m));
        var tabs = new List<FieldsetModel> { new("Thông tin chung", head), new("Bảng chính (T0)", master) };
        for (var i = 0; i < MaxDetails; i++)
        {
            var x = s.Details[i];
            var p = $"D{i + 1}";
            var fields = new List<FieldModel>
            {
                new($"{p}.Table", "Bảng chi tiết", "select", x.Table, Span: 20, Options: TableOptions(x.Table, true)),
                new($"{p}.Prev", "Bảng đã nạp", Value: x.Prev, Span: 10, ReadOnly: true),
                new($"{p}.Caption", "Tên tab", Value: x.Caption, Span: 10, MaxLength: 100),
            };
            if (x.Table.Length > 0)
            {
                fields.Add(new FieldModel($"{p}.Pk", "Khóa chính", "select", x.Pk ?? "", Span: 10, RowStart: true, Options: ColOptions(x.Schema, false)));
                fields.Add(new FieldModel($"{p}.MK", "Cột nối ở bảng chính", "select", x.MasterKey ?? "", Span: 15, Options: ColOptions(m.Schema, true)));
                fields.Add(new FieldModel($"{p}.DK", "Cột nối ở bảng chi tiết", "select", x.DetailKey ?? "", Span: 15, Options: ColOptions(x.Schema, true)));
                fields.AddRange(Checks(p, x));
            }
            if (i == 0 || x.Table.Length > 0 || s.Details[i - 1].Table.Length > 0) tabs.Add(new FieldsetModel($"Tab chi tiết {i + 1} (T{i + 1})", fields));
        }
        tabs.Add(new FieldsetModel("Xác nhận lưu", [AccountEndpoints.CodeField() with { Required = false, Span = 20 }]));

        var (provider, _, hasKey) = await d.Ai.SettingsAsync(ctx.RequestAborted);
        var aiReady = provider is AiProviders.Claude or AiProviders.Gemini && hasKey;
        var toolbar = new List<ToolbarItem>
        {
            new("save", "Lưu", Form: RecordId, FormAction: ctx.Url("/admin/forms/quick/save"), Shortcut: "Ctrl+S"),
            new("custom", "Xem thử", Form: RecordId, FormAction: ctx.Url("/admin/forms/quick/preview"), Icon: "search"),
            new("custom", "Nạp lại cột", Form: RecordId, FormAction: ctx.Url("/admin/forms/quick/refresh"), Icon: "settings"),
            new("custom", "Đoán theo tên", Form: RecordId, FormAction: ctx.Url("/admin/forms/quick/guess"), Icon: "system"),
            new("custom", aiReady ? $"Gợi ý bằng AI ({AiProviders.Name(provider)})" : "Gợi ý bằng AI (chưa cài đặt)", Form: RecordId,
                FormAction: ctx.Url("/admin/forms/quick/ai"), Icon: "system", Disabled: !aiReady,
                Title: aiReady ? "Gửi tên form, tên menu và tên bảng / cột (không có dữ liệu) cho AI." : "Vào Mô tả form cứng → Cài đặt AI."),
            new("custom", "Sửa JSON", Form: RecordId, FormAction: ctx.Url("/admin/forms/quick/json"), Icon: "key"),
            new("cancel", "Về danh sách", Href: ctx.Url("/admin/forms"), Target: "self"),
        };
        if (d.Schema.CaptionsPending(t))
            messages = [.. messages, ErpMessage.Info("Tiêu đề tiếng Việt của cột đang nạp (máy chủ SQL đang chậm), tạm hiện tên cột. Bấm \"Nạp lại cột\" sau ít giây.")];
        return ScreenRenderer.Render(new ScreenModel("AdminForms", FormTypes.Edit, $"AdminForms:quick:{s.Form}:{t.Key}", $"Tạo nhanh {s.Form} — {t.Key}",
            Mode: "new", Messages: messages), w =>
        {
            ToolbarRenderer.Write(w, toolbar);
            RecordRenderer.Write(w, new RecordModel(RecordId, ctx.Url("/admin/forms/quick/preview"), field, token, tabs));
            var n = 0;
            foreach (var p in preview)
            {
                var cols = p.Columns.Select((c, i) => new GridColumn($"c{i}", c, Width: 140)).ToList();
                GridRenderer.Write(w, new GridModel($"P{n++}", cols,
                    p.Rows.Select((r, i) => new GridRow(i.ToString(CultureInfo.InvariantCulture), r.Select(v => new GridCell(v)).ToList())).ToList(),
                    Caption: p.Error is null ? $"Xem thử {p.Slot} — {p.Table}: {p.Rows.Count} dòng" : $"{p.Slot} — {p.Table}: {p.Error}"));
            }
        });
    }

    private static IEnumerable<FieldModel> Checks(string prefix, Pick p)
    {
        var chosen = p.Columns.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var first = true;
        foreach (var c in p.Schema)
        {
            yield return new FieldModel($"{prefix}.Col.{c.Name}", Label(c), "checkbox", chosen.Contains(c.Name) ? "true" : "false", Span: 10, RowStart: first);
            first = false;
        }
    }

    private static string Label(DbColumn c) =>
        (string.IsNullOrWhiteSpace(c.Caption) ? c.Name : $"{c.Name} — {c.Caption}") + (c.IsPrimaryKey ? " (khóa)" : "");

    private static bool Db(Exception ex) => ex is WebErp.Core.Errors.ErpDatabaseException or Microsoft.Data.SqlClient.SqlException;

    private static string Describe(Exception ex) => ex switch
    {
        Microsoft.Data.SqlClient.SqlException { Number: -2 } => "quá thời gian chờ (máy chủ SQL đang bận), thử lại sau ít phút",
        Microsoft.Data.SqlClient.SqlException e => $"lỗi SQL {e.Number}",
        _ => ex.Message,
    };

    // ---- Cài đặt AI ----

    private static async Task<string> AiDialogAsync(HttpContext ctx, Deps d, IReadOnlyList<ErpMessage>? messages)
    {
        var (field, token) = AdminAuth.Tokens(ctx);
        var (provider, model, hasKey) = await d.Ai.SettingsAsync(ctx.RequestAborted);
        return DialogRenderer.Render(new DialogModel("Cài đặt AI", ctx.Url("/admin/forms/ai-settings"), field, token,
        [
            new FieldModel("Provider", "Nhà cung cấp", "select", provider, Span: 20,
                Options: [("", "Tắt"), (AiProviders.Claude, "Anthropic Claude"), (AiProviders.Gemini, "Google Gemini")]),
            new FieldModel("Model", "Model", Value: provider.Length == 0 ? "" : model, Span: 20, MaxLength: 100,
                Placeholder: $"{AiProviders.DefaultModel(AiProviders.Claude)} / {AiProviders.DefaultModel(AiProviders.Gemini)}",
                Hint: "Để trống = model mặc định của nhà cung cấp."),
            new FieldModel("ApiKey", "Khóa API", "password", Span: 40, Autocomplete: "off", RowStart: true,
                Hint: hasKey ? "Đã có khóa. Để trống = giữ khóa cũ. Khóa được mã hóa, không hiện lại." : "Chưa có khóa. Khóa được mã hóa, không hiện lại."),
            AccountEndpoints.CodeField(),
        ], "Lưu", Size: "md",
            Paragraphs:
            [
                "Dùng cho nút \"Gợi ý bằng AI\" ở trang Tạo nhanh. Mỗi lần bấm, BE gửi tên form, tên menu và tên bảng / cột / kiểu / tiêu đề cột của DB công ty " +
                "(KHÔNG gửi dữ liệu) tới nhà cung cấp đã chọn. Chi phí tính vào tài khoản API của khóa này.",
            ], Messages: messages));
    }

    private static async Task<IResult> SaveAiAsync(HttpContext ctx, Deps d)
    {
        var f = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
        if (!await AdminAuth.ValidAntiforgeryAsync(ctx)) return TenantEndpoints.Expired();
        var provider = f["Provider"].ToString().Trim();
        if (provider is not ("" or AiProviders.Claude or AiProviders.Gemini))
            return HtmlResults.Page(await AiDialogAsync(ctx, d, [ErpMessage.Error("Nhà cung cấp không hợp lệ.", "Provider")]), StatusCodes.Status422UnprocessableEntity);
        var model = f["Model"].ToString().Trim();
        if (model.Length > 0 && !model.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '.' or '_' or ':'))
            return HtmlResults.Page(await AiDialogAsync(ctx, d, [ErpMessage.Error("Tên model chỉ gồm chữ, số, '-', '.', '_'.", "Model")]), StatusCodes.Status422UnprocessableEntity);
        var key = f["ApiKey"].ToString().Trim();
        if (!d.Forms.Tenants.Accounts.VerifyCode(AdminAuth.UserName(ctx), f["Code"].ToString()))
            return HtmlResults.Page(await AiDialogAsync(ctx, d, [ErpMessage.Error("Mã xác thực không đúng hoặc đã dùng.", "Code")]), StatusCodes.Status422UnprocessableEntity);
        var writer = d.Writer(ctx);
        writer.SetSetting(AiSettingNames.Provider, provider.Length == 0 ? null : provider);
        writer.SetSetting(AiSettingNames.Model, model.Length == 0 ? null : model);
        if (key.Length > 0) writer.SetSetting(AiSettingNames.Key, key);
        return FormEndpoints.ListPage(ctx, d.Forms,
        [
            ErpMessage.Success(provider.Length == 0 ? "Đã tắt gợi ý bằng AI." : $"Đã lưu cài đặt AI: {AiProviders.Name(provider)}, model {(model.Length == 0 ? AiProviders.DefaultModel(provider) : model)}" +
                                                       (key.Length > 0 ? ", khóa mới." : ", giữ khóa cũ.")),
        ]);
    }
}
