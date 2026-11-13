using System.Globalization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Errors;
using WebErp.Core.Metadata;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Metadata;
using WebErp.Erp.Query;
using WebErp.Erp.Security;

namespace WebErp.Erp.Screens;

/// <summary>
/// Màn hình dựng từ metadata ERP (M2 danh sách, M3 xem chứng từ). CHỈ ĐỌC: mọi câu chạy trong một giao dịch luôn rollback,
/// đoạn SQL của metadata qua kiểm chỉ đọc, giá trị là tham số. Lọc ngầm như WinForms (tài liệu 10 §3.6): chứng từ của chính
/// người dùng nếu không phải Manager/Admin, chi nhánh của người dùng, FTR/FTX của slot. Không chạy hook (ESL…) và nút lệnh.
/// <para>
/// Mở màn: như WinForms (<c>MainFrm.MenuClick</c> không kiểm <c>Permiss.View</c>), cửa chặn là MENU của người dùng — Host chỉ mở màn có
/// trong <c>{Module}_GetUserMenuFnc</c> (user <c>Admin</c> thấy mọi menu; menu <c>isNotCheckPermission</c>). Cờ quyền chỉ quyết định phạm vi
/// dữ liệu (người tạo, chỉ Thêm ⇒ không thấy gì, ẩn tiền, cột/tab theo Manager/Admin).
/// </para>
/// </summary>
public sealed partial class ErpScreens(ErpConnectionFactory connections, ErpMetadata metadata, ErpPermissions permissions, TimeProvider time)
    : IErpScreens
{
    public const int ListMax = 1000;
    public const int DetailMax = 2000;
    public const int OptionsMax = 500;

    /// <summary>
    /// Cột lưới WinForms luôn ẩn khi dựng lưới (<c>GridX.SetGrid</c>, ReBuildColumn): khóa kỹ thuật và cột người/ngày tạo, sửa.
    /// Web để <c>data-hidden</c> (người dùng vẫn bật lại được bằng nút Cột).
    /// </summary>
    private static readonly string[] SystemHidden = ["UserAutoID", "UID", "ParentID", "isSyn", "UserCreate", "UserUpdate", "DateCreate", "DateUpdate"];
    private static readonly string[] DateColumns = ["DocumentDate", "PeriodID", "QuarterID", "Quy", "YearID", "Nam"];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,99}$")]
    private static partial Regex Identifier();

    /// <summary>
    /// Cột bí mật KHÔNG BAO GIỜ gửi ra trình duyệt, dù cấu hình form nào (form người dùng đọc thẳng <c>SY_User</c>, form thiết lập kết nối
    /// API có khóa/token): mật khẩu (hash kiểu cũ), mật mã, token, khóa API, chuỗi kết nối; và nội dung tệp đính kèm dạng chữ
    /// (<c>Base64Content</c>…: nặng, có thể là tài liệu riêng — tải tệp là việc của đầu API đính kèm, có kiểm quyền).
    /// </summary>
    [GeneratedRegex(@"^(pass|pwd|matkhau|mat_khau)$|password|passwd|secret|token|api_?key|private_?key|connection_?string|connstr|^base64",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretColumn();

    internal static bool IsSecret(string column) => SecretColumn().IsMatch(column);

    /// <summary>Form viết tay chưa có tệp mô tả (<see cref="FormOverlays"/>): web chưa dựng được.</summary>
    private static bool IsHandCodedWithoutOverlay(string formName) => HandCodedForms.Names.Contains(formName) && !FormOverlays.Has(formName);

    public async Task<ListView> OpenListAsync(ScreenRequest req, CancellationToken ct)
    {
        var form = await metadata.GetFormAsync(req.Tenant, req.FormName, req.RuntimeName, ct);
        if (form is { IsDialog: true, Master: null } && !IsHandCodedWithoutOverlay(req.FormName))
        {
            // Hộp thoại không có dữ liệu để xem (sao lưu, phục hồi…): chỉ thông tin + nút khóa.
            return new ListView(ScreenStatus.Ok, form);
        }
        if (form?.Master is not { } slot || IsHandCodedWithoutOverlay(req.FormName)) return new ListView(ScreenStatus.NotSupported, form);
        var ctx = await ContextAsync(req, form, ct);
        var src = await metadata.GetColumnsAsync(req.Tenant, slot.Source, ct);

        return await RunAsync(req.Tenant, async (c, tx) =>
        {
            var q = new SlotQuery(slot, ctx.Resolve);
            var filters = new List<ViewFilter>();
            DateFilter(ctx, form, q, src, filters);
            await ToolbarFiltersAsync(ctx, form, q, src, c, tx, filters, ct);
            await ImplicitFiltersAsync(ctx, form, q, src, c, tx, includeToolbarFilter: true, ct);
            Search(ctx, q, src, filters);

            // Phân trang (chuẩn 11.5): mỗi trang ListMax dòng. Slot có TRS thì như WinForms chỉ lấy top N, không phân trang.
            var paged = slot.Top is null;
            var take = Math.Min(ListMax, slot.Top ?? ListMax);
            var page = paged && int.TryParse(req.Query.GetValueOrDefault("page"), NumberStyles.None, CultureInfo.InvariantCulture, out var pg) ? Math.Clamp(pg, 1, 100_000) : 1;
            var data = await SqlReader.ReadAsync(c, tx, paged ? q.PageSql((page - 1) * take, take) : q.ListSql(take), q.Parameters, ctx.Timeout, take, ct);
            var cols = Columns(ctx, slot, "F0", data.Names, data.Types);
            var sumCols = cols.Where(x => x.Col.Sum && src.Contains(x.Col.Field)).Select(x => x.Col.Field).ToList();
            var total = (page - 1) * take + data.Rows.Count;
            var sums = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            if (data.Rows.Count >= take || page > 1 || sumCols.Count > 0)
            {
                var countSql = q.CountSql(slot.Distinct ? [] : sumCols);
                await using var r = await c.ExecuteReaderAsync(new CommandDefinition(countSql, q.Parameters, tx, ctx.Timeout, cancellationToken: ct));
                if (await r.ReadAsync(ct))
                {
                    total = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture);
                    for (var i = 0; !slot.Distinct && i < sumCols.Count; i++)
                        sums[sumCols[i]] = r.IsDBNull(i + 1) ? 0 : Convert.ToDecimal(r.GetValue(i + 1), CultureInfo.InvariantCulture);
                }
            }
            // Trang vượt quá (bộ lọc vừa đổi, số dòng ít đi): về trang cuối.
            if (page > 1 && data.Rows.Count == 0 && total > 0)
            {
                page = (total + take - 1) / take;
                data = await SqlReader.ReadAsync(c, tx, q.PageSql((page - 1) * take, take), q.Parameters, ctx.Timeout, take, ct);
            }
            foreach (var x in cols.Where(x => x.Col.Sum && !sums.ContainsKey(x.Col.Field))) sums[x.Col.Field] = SumRows(data.Rows, x.Index);
            cols = await WithNamesAsync(ctx, slot, cols, data.Rows, null, c, tx, ct);

            var keyIndex = Array.FindIndex(data.Names, n => n.Equals(slot.PrimaryKey, StringComparison.OrdinalIgnoreCase));
            var tabs = await IndependentTabsAsync(ctx, form, ct);
            return new ListView(ScreenStatus.Ok, form, cols.Select(x => x.Col).ToList(),
                data.Rows.Select(row => cols.Select(x => row[x.Index]).ToArray()).ToList(),
                data.Rows.Select(row => keyIndex < 0 ? "" : Invariant(row[keyIndex])).ToList(),
                total, sums, filters, keyIndex >= 0 ? slot.PrimaryKey : null, form.IsEdit && keyIndex >= 0,
                Page: page, PageSize: paged ? take : 0, GroupBy: GroupBy(slot, cols), Tabs: tabs);
        }, e => new ListView(ScreenStatus.ConfigError, form, Note: e), ct);
    }

    public async Task<RecordView> OpenRecordAsync(ScreenRequest req, string key, CancellationToken ct)
    {
        var form = await metadata.GetFormAsync(req.Tenant, req.FormName, req.RuntimeName, ct);
        if (form?.Master is not { } slot || !form.IsEdit || IsHandCodedWithoutOverlay(req.FormName)) return new RecordView(ScreenStatus.NotSupported, form);
        var ctx = await ContextAsync(req, form, ct);
        var src = await metadata.GetColumnsAsync(req.Tenant, slot.Source, ct);

        return await RunAsync(req.Tenant, async (c, tx) =>
        {
            var master = await MasterRowAsync(ctx, form, slot, src, key, c, tx, ct);
            if (master is null) return new RecordView(ScreenStatus.NotFound, form, key);
            var tabs = await RecordTabsAsync(ctx, form, slot, master, c, tx, ct);
            var visible = new List<SlotMeta>();
            foreach (var d in form.Details.Where(d => !d.Independent && DetailVisible(ctx, form, d)))
            {
                // Tab tùy chọn: bảng không có trong DB của khách thì bỏ (WinForms kiểm sysobjects trước khi nạp).
                if (d.Optional && (await metadata.GetColumnsAsync(req.Tenant, d.Source, ct)).Count == 0) continue;
                visible.Add(d);
            }
            var details = visible.Select((d, i) => new ViewDetailTab(d.Id, d.Caption ?? (i == 0 ? "Chi tiết" : $"Chi tiết {i + 1}"))).ToList();
            return new RecordView(ScreenStatus.Ok, form, key, slot.PrimaryKey, tabs, details);
        }, e => new RecordView(ScreenStatus.ConfigError, form, key, Note: e), ct);
    }

    public async Task<DetailView> OpenDetailAsync(ScreenRequest req, string key, string slotId, CancellationToken ct)
    {
        var form = await metadata.GetFormAsync(req.Tenant, req.FormName, req.RuntimeName, ct);
        var detail = form?.Details.FirstOrDefault(d => d.Id.Equals(slotId, StringComparison.OrdinalIgnoreCase));
        // Tab độc lập (IND) mở được cả ở form danh sách, không cần chứng từ.
        if (form?.Master is not { } slot || !form.IsEdit && detail?.Independent != true || IsHandCodedWithoutOverlay(req.FormName))
            return new DetailView(ScreenStatus.NotSupported);
        if (detail is null) return new DetailView(ScreenStatus.NotFound);
        var ctx = await ContextAsync(req, form, ct);
        if (!DetailVisible(ctx, form, detail)) return new DetailView(ScreenStatus.Forbidden);
        var src = await metadata.GetColumnsAsync(req.Tenant, slot.Source, ct);

        return await RunAsync(req.Tenant, async (c, tx) =>
        {
            var master = detail.Independent ? null : await MasterRowAsync(ctx, form, slot, src, key, c, tx, ct);
            if (master is null && !detail.Independent) return new DetailView(ScreenStatus.NotFound);
            var q = new SlotQuery(detail, ctx.Resolve);
            if (master is not null)
            {
                // Nối master–detail: MUL (mặc định PK master) = MDL (mặc định PK master) (tài liệu 10 §4.3).
                var masterField = detail.MasterField ?? slot.PrimaryKey;
                var linkIndex = Array.FindIndex(master.Names, n => n.Equals(masterField, StringComparison.OrdinalIgnoreCase));
                if (linkIndex < 0) return new DetailView(ScreenStatus.ConfigError, Note: $"Bảng chính không có cột nối {masterField}.");
                q.Where.Add($"{q.Col(detail.DetailField ?? slot.PrimaryKey)} = {q.Param(master.Rows[0][linkIndex])}");
            }
            if (detail.Independent)
            {
                // Tab độc lập: lọc ngầm như một danh sách (quyền chỉ Thêm, người tạo, chi nhánh, FTW/FTR/FTX của slot).
                await ImplicitFiltersAsync(ctx, form, q, await metadata.GetColumnsAsync(req.Tenant, detail.Source, ct), c, tx, includeToolbarFilter: true, ct);
            }
            else
            {
                if (detail.FilterWeb is not null) q.Where.Add(q.Fragment(detail.FilterWeb, $"{detail.Id}.FTW"));
                if (detail.Filter is not null) q.Where.Add(q.Fragment(detail.Filter, $"{detail.Id}.FTR"));
                if (detail.FilterEx is not null) q.Where.Add(q.Fragment(detail.FilterEx, $"{detail.Id}.FTX"));
            }
            var data = await SqlReader.ReadAsync(c, tx, q.ListSql(Math.Min(DetailMax, detail.Top ?? DetailMax)), q.Parameters, ctx.Timeout, DetailMax, ct);
            var cols = Columns(ctx, detail, FormatKey(detail.Id), data.Names, data.Types);
            cols = await WithNamesAsync(ctx, detail, cols, data.Rows, master is null ? null : MasterValue(master), c, tx, ct);
            var keyIndex = Array.FindIndex(data.Names, n => n.Equals(detail.PrimaryKey, StringComparison.OrdinalIgnoreCase));
            return new DetailView(ScreenStatus.Ok, detail.Id, cols.Select(x => x.Col).ToList(),
                data.Rows.Select(row => cols.Select(x => row[x.Index]).ToArray()).ToList(),
                data.Rows.Select(row => keyIndex < 0 ? "" : Invariant(row[keyIndex])).ToList(), data.Rows.Count,
                cols.Where(x => x.Col.Sum).ToDictionary(x => x.Col.Field, x => SumRows(data.Rows, x.Index), StringComparer.OrdinalIgnoreCase),
                keyIndex >= 0 ? detail.PrimaryKey : null, GroupBy: GroupBy(detail, cols));
        }, e => new DetailView(ScreenStatus.ConfigError, Note: e), ct);
    }

    // ---------------------------------------------------------------- ngữ cảnh, token

    private sealed class Ctx(ScreenRequest req, FormMeta form, FormPermission perm, FieldFormats formats, IReadOnlyDictionary<string, object?> user, DateTime today, int timeout)
    {
        public ScreenRequest Req { get; } = req;
        public FormMeta Form { get; } = form;
        public FormPermission Perm { get; } = perm;
        public FieldFormats Formats { get; } = formats;
        public DateTime Today { get; } = today;
        public int Timeout { get; } = timeout;
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }

        /// <summary>Token phiên (<c>SystemPara.ReplaceSourceSQL</c>, tài liệu 10 §7.2), không phân biệt hoa thường.</summary>
        public (bool, object?) Resolve(string token) => token.ToUpperInvariant() switch
        {
            "USER" or "USERID" or "USERNAME" or "@USER" or "@USERID" => (true, Req.UserName),
            "USERFULLNAME" => (true, user.GetValueOrDefault("HoTen") ?? Req.UserName),
            "BRANCHID" or "@BRANCHID" => (true, Req.BranchId ?? ""),
            "PERIODID" => (true, Today.ToString("yyyyMM", CultureInfo.InvariantCulture)),
            "ISMANAGER" => (true, Perm.SeesAll ? 1 : 0),
            "ISADMIN" => (true, Perm.Admin ? 1 : 0),
            "FROMDATE" when FromDate is { } f => (true, f),
            "TODATE" when ToDate is { } t => (true, t),
            _ => Lookup(token),
        };

        private (bool, object?) Lookup(string token)
        {
            if (user.TryGetValue(token, out var v)) return (true, v);
            var hit = user.FirstOrDefault(p => p.Key.Equals(token, StringComparison.OrdinalIgnoreCase));
            return hit.Key is null ? (false, null) : (true, hit.Value);
        }

        /// <summary>Giá trị mặc định có token (vd <c>{BranchID}</c>) → chuỗi.</summary>
        public string Expand(string text) => TokenPattern().Replace(text, m => Resolve(m.Groups[1].Value) is (true, var v) ? Invariant(v) : m.Value);
    }

    [GeneratedRegex(@"\{@?([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex TokenPattern();

    private async Task<Ctx> ContextAsync(ScreenRequest req, FormMeta form, CancellationToken ct)
    {
        var perm = await permissions.GetAsync(req.Tenant, req.UserName, req.RuntimeName, ct);
        var formats = await metadata.GetFormatsAsync(req.Tenant, ct);
        var user = await metadata.GetUserTokensAsync(req.Tenant, req.UserName, ct);
        return new Ctx(req, form, perm, formats, user, time.GetLocalNow().Date, connections.Options.CommandTimeoutSeconds);
    }

    // ---------------------------------------------------------------- lọc

    /// <summary>Bộ lọc thời gian trên toolbar (key <c>SP</c>, tài liệu 10 §4.4; 11 §3.1).</summary>
    private static void DateFilter(Ctx ctx, FormMeta form, SlotQuery q, IReadOnlySet<string> src, List<ViewFilter> filters)
    {
        var sp = form.SettingInt("SP", 0);
        if (sp == 1) sp = 9;
        if (sp is not (9 or 12 or 13 or 14 or 15)) return;
        var col = form.Setting("DFP") is { } dfp && src.Contains(dfp) ? dfp : DateColumns.FirstOrDefault(src.Contains);
        if (col is null || col is "QuarterID" or "Quy") return;

        var today = ctx.Today;
        var (from, to) = sp switch
        {
            12 => (today, today),
            13 => (today.AddDays(-form.SettingInt("SPA", 7)), today),
            14 => (new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1, 0, 0, 0, DateTimeKind.Unspecified), default(DateTime)),
            15 => (new DateTime(today.Year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified), new DateTime(today.Year, 12, 31, 0, 0, 0, DateTimeKind.Unspecified)),
            _ => (new DateTime(today.Year, today.Month, 1, 0, 0, 0, DateTimeKind.Unspecified), default(DateTime)),
        };
        if (to == default) to = sp == 14 ? from.AddMonths(3).AddDays(-1) : from.AddMonths(1).AddDays(-1);
        if (ParseDate(ctx.Req.Query.GetValueOrDefault("FromDate")) is { } f) from = f;
        if (ParseDate(ctx.Req.Query.GetValueOrDefault("ToDate")) is { } t) to = t;
        if (to < from) (from, to) = (to, from);
        ctx.FromDate = from;
        ctx.ToDate = to;

        if (col.Equals("PeriodID", StringComparison.OrdinalIgnoreCase))
            q.Where.Add($"{q.Col(col)} BETWEEN {q.Param(from.ToString("yyyyMM", CultureInfo.InvariantCulture))} AND {q.Param(to.ToString("yyyyMM", CultureInfo.InvariantCulture))}");
        else if (col is "YearID" or "Nam")
            q.Where.Add($"{q.Col(col)} BETWEEN {q.Param(from.Year)} AND {q.Param(to.Year)}");
        else
            q.Where.Add($"{q.Col(col)} >= {q.Param(from)} AND {q.Col(col)} < {q.Param(to.AddDays(1))}");
        filters.Add(new ViewFilter("FromDate", "Từ ngày", "date", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        filters.Add(new ViewFilter("ToDate", "Đến ngày", "date", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
    }

    /// <summary>Ô lọc của <c>SY_FrmFltTbl</c>: text, ngày, số, combo, checkbox (các kiểu khác chưa hỗ trợ, bỏ qua).</summary>
    private static async Task ToolbarFiltersAsync(Ctx ctx, FormMeta form, SlotQuery q, IReadOnlySet<string> src, SqlConnection c, SqlTransaction tx,
        List<ViewFilter> filters, CancellationToken ct)
    {
        foreach (var f in form.Filters)
        {
            if (f.Type is not (0 or 1 or 2 or 3 or 5)) continue;
            var name = Identifier().IsMatch(f.Column) ? f.Column : $"flt{f.Key}";
            var value = ctx.Req.Query.TryGetValue(name, out var v) ? v.Trim() : f.DefaultValue is null ? "" : ctx.Expand(f.DefaultValue);
            var caption = f.Caption ?? ctx.Formats.Caption(f.Column, form.RuntimeName, form.FormName) ?? f.Column;
            IReadOnlyList<(string, string)>? options = null;
            if (f.Type == 3)
            {
                if (f.Source is null) continue;
                options = await OptionsAsync(q, f, c, tx, ctx.Timeout, ct);
            }
            var control = f.Type switch { 1 => "date", 2 => "number", 3 => "select", 5 => "checkbox", _ => "text" };
            filters.Add(new ViewFilter(name, caption, control, value, options));
            if (value.Length == 0) continue;

            object? param = f.Type switch
            {
                1 => ParseDate(value),
                2 => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) && d != 0 ? d : null,
                5 => value == "true" ? 1 : null,
                _ => f.Operator == 1 ? $"%{value}%" : value,
            };
            if (param is null) continue;
            if (f.WhereString is not null)
            {
                // Slot có join (vd tên đối tượng từ CF_ObjectTbl): cột trần của bảng nguồn trong mẫu lọc ghi kèm tên bảng.
                var where = q.Slot.Joins.Count > 0 && q.Slot.FromOverride is null ? QualifyColumns(f.WhereString, q.Slot.Source, src) : f.WhereString;
                q.Where.Add(q.Fragment(where, $"lọc {name}", t => t == "0" || t.Equals(f.Column, StringComparison.OrdinalIgnoreCase) ? (true, param) : (false, null)));
                continue;
            }
            SqlTemplate.EnsureReadOnly(f.Column, $"lọc {name}");
            // Cột của bảng nguồn thì ghi kèm tên bảng: slot có join tới bảng cùng tên cột (vd BranchID) không bị "Ambiguous column name".
            var target = src.Contains(f.Column) && q.Slot.FromOverride is null ? q.Col(f.Column) : f.Column;
            var column = f.Type == 1 ? $"CAST({target} AS date)" : target;
            var op = f.Type == 5 ? "=" : f.Operator switch { 1 => "LIKE", 2 => ">", 3 => ">=", 4 => "<", 5 => "<=", 6 => "<>", _ => "=" };
            q.Where.Add($"{column} {op} {q.Param(param)}");
        }
    }

    /// <summary>
    /// Ghi kèm tên bảng nguồn cho cột "trần" của bảng nguồn trong một đoạn SQL lọc (vd <c>BranchID = '{BranchID}'</c> →
    /// <c>Bảng.[BranchID] = …</c>), để slot có join tới bảng cùng tên cột không lỗi "Ambiguous column name". Bỏ qua chuỗi, token
    /// <c>{…}</c>, tên đã có tiền tố / hậu tố <c>.</c>, tên hàm (theo sau là <c>(</c>), kiểu sau <c>AS</c>, tên trong <c>[…]</c>.
    /// </summary>
    internal static string QualifyColumns(string sql, string source, IReadOnlySet<string> columns)
    {
        var sb = new System.Text.StringBuilder(sql.Length + 32);
        var i = 0;
        var lastWord = "";
        while (i < sql.Length)
        {
            var ch = sql[i];
            if (ch == '\'')
            {
                var j = i + 1;
                while (j < sql.Length && !(sql[j] == '\'' && (j + 1 >= sql.Length || sql[j + 1] != '\''))) j += sql[j] == '\'' ? 2 : 1;
                j = Math.Min(j + 1, sql.Length);
                sb.Append(sql, i, j - i);
                i = j;
                continue;
            }
            if (ch is '[' or '{')
            {
                var end = sql.IndexOf(ch == '[' ? ']' : '}', i);
                end = end < 0 ? sql.Length - 1 : end;
                sb.Append(sql, i, end - i + 1);
                i = end + 1;
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                var j = i;
                while (j < sql.Length && (char.IsLetterOrDigit(sql[j]) || sql[j] is '_' or '#' or '$')) j++;
                var word = sql[i..j];
                var k = j;
                while (k < sql.Length && char.IsWhiteSpace(sql[k])) k++;
                var bare = (i == 0 || sql[i - 1] is not ('.' or '@')) && (j >= sql.Length || sql[j] != '.') && (k >= sql.Length || sql[k] != '(')
                    && !lastWord.Equals("as", StringComparison.OrdinalIgnoreCase);
                sb.Append(bare && columns.Contains(word) ? $"{source}.[{word}]" : word);
                lastWord = word;
                i = j;
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    /// <summary>Danh sách chọn của combo lọc: chạy câu nguồn (đã kiểm chỉ đọc), lấy tối đa <see cref="OptionsMax"/> dòng.</summary>
    private static async Task<IReadOnlyList<(string, string)>> OptionsAsync(SlotQuery q, FilterMeta f, SqlConnection c, SqlTransaction tx, int timeout, CancellationToken ct)
    {
        var sql = q.Fragment(f.Source!, $"nguồn lọc {f.Column}");
        var data = await SqlReader.ReadAsync(c, tx, sql, q.Parameters, timeout, OptionsMax, ct);
        var vi = Array.FindIndex(data.Names, n => n.Equals(f.ValueColumn ?? f.Column, StringComparison.OrdinalIgnoreCase));
        if (vi < 0) vi = 0;
        var di = f.DisplayColumn is null
            ? Array.FindIndex(data.Names, n => n.EndsWith("Name", StringComparison.OrdinalIgnoreCase) && !n.Equals(data.Names[vi], StringComparison.OrdinalIgnoreCase))
            : Array.FindIndex(data.Names, n => n.Equals(f.DisplayColumn, StringComparison.OrdinalIgnoreCase));
        var options = new List<(string, string)> { ("", "(Tất cả)") };
        foreach (var row in data.Rows)
        {
            var value = Invariant(row[vi]);
            var text = di < 0 ? value : Invariant(row[di]);
            options.Add((value, text.Length == 0 || text == value ? value : $"{value} — {text}"));
        }
        return options;
    }

    /// <summary>
    /// Lọc theo chi nhánh của người dùng như WinForms: chỉ form ĐỘNG tự lọc (<c>FrmFormList</c>, <c>FrmFormListEdit</c>,
    /// <c>FrmMasterDetail</c> gọi <c>Connector.GetBranchFilter</c>), trừ khi SPA3 = 1. Form viết tay không gọi hàm này (chủ dự án chốt
    /// 2026-11-13: giống ERP), nên chỉ lọc khi tệp mô tả đặt key web <c>WBR</c> = 1 cho form tự lọc chi nhánh trong mã
    /// (vd theo dõi phiếu nhập/xuất kho, phân bổ / khấu hao theo kỳ có ô chi nhánh khóa theo người dùng).
    /// </summary>
    internal static bool BranchFiltered(FormMeta form) =>
        HandCodedForms.Names.Contains(form.FormName) ? form.Setting("WBR") == "1" : form.Setting("SPA3") != "1";

    /// <summary>Lọc ngầm (tài liệu 10 §3.6): quyền chỉ Thêm, chứng từ của chính mình, chi nhánh, FTR, FTX (FTXS).</summary>
    private static async Task ImplicitFiltersAsync(Ctx ctx, FormMeta form, SlotQuery q, IReadOnlySet<string> src, SqlConnection c, SqlTransaction tx,
        bool includeToolbarFilter, CancellationToken ct)
    {
        var slot = q.Slot;
        if (ctx.Perm.AddOnly) q.Where.Add("1=0");
        if (src.Contains("UserCreate") && !ctx.Perm.SeesAll && form.Setting("SPA2") != "1")
            q.Where.Add($"{q.Col("UserCreate")} = {q.Param(ctx.Req.UserName)}");
        var branches = (ctx.Req.BranchId ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (src.Contains("BranchID") && branches.Length > 0 && BranchFiltered(form))
            q.Where.Add($"{q.Col("BranchID")} IN ({string.Join(", ", branches.Select(b => q.Param(b)))}) OR {q.Col("BranchID")} IS NULL");
        if (slot.FilterWeb is not null) q.Where.Add(q.Fragment(slot.FilterWeb, $"{slot.Id}.FTW"));
        if (includeToolbarFilter && slot.Filter is not null) q.Where.Add(q.Fragment(slot.Filter, $"{slot.Id}.FTR"));
        if (slot.FilterEx is null) return;
        if (slot.FilterExCheck is not null)
        {
            var check = q.Fragment(slot.FilterExCheck, $"{slot.Id}.FTXS");
            var r = await c.ExecuteScalarAsync(new CommandDefinition(check, q.Parameters, tx, ctx.Timeout, cancellationToken: ct));
            if (!(r is not null && r is not DBNull && Convert.ToDecimal(r, CultureInfo.InvariantCulture) > 0)) return;
        }
        q.Where.Add(q.Fragment(slot.FilterEx, $"{slot.Id}.FTX"));
    }

    /// <summary>Ô "Tìm nhanh": LIKE trên khóa và các cột hiện theo GD2 của bảng nguồn.</summary>
    private static void Search(Ctx ctx, SlotQuery q, IReadOnlySet<string> src, List<ViewFilter> filters)
    {
        var text = ctx.Req.Query.GetValueOrDefault("q")?.Trim() ?? "";
        filters.Add(new ViewFilter("q", "Tìm nhanh", "search", text));
        if (text.Length == 0) return;
        var cols = new[] { q.Slot.PrimaryKey }.Concat(q.Slot.Order).Where(c => src.Contains(c) && !IsSecret(c)).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        if (cols.Count == 0) return;
        var p = q.Param($"%{text.Replace("[", "[[]", StringComparison.Ordinal)}%");
        q.Where.Add(string.Join(" OR ", cols.Select(col => $"{q.Col(col)} LIKE {p}")));
    }

    // ---------------------------------------------------------------- chứng từ

    private static async Task<SqlRows?> MasterRowAsync(Ctx ctx, FormMeta form, SlotMeta slot, IReadOnlySet<string> src, string key,
        SqlConnection c, SqlTransaction tx, CancellationToken ct)
    {
        var q = new SlotQuery(slot, ctx.Resolve);
        q.Where.Add($"{q.Col(slot.PrimaryKey)} = {q.Param(key)}");
        // Cùng lọc ngầm như danh sách: mở thẳng bằng URL không xem được chứng từ của người khác / chi nhánh khác.
        await ImplicitFiltersAsync(ctx, form, q, src, c, tx, includeToolbarFilter: false, ct);
        var data = await SqlReader.ReadAsync(c, tx, q.ListSql(1), q.Parameters, ctx.Timeout, 1, ct);
        return data.Rows.Count == 0 ? null : data;
    }

    private static async Task<List<ViewTab>> RecordTabsAsync(Ctx ctx, FormMeta form, SlotMeta slot, SqlRows master, SqlConnection c, SqlTransaction tx,
        CancellationToken ct)
    {
        var row = master.Rows[0];
        var value = MasterValue(master);
        var index = master.Names.Select((n, i) => (n, i)).ToDictionary(x => x.n, x => x.i, StringComparer.OrdinalIgnoreCase);
        var removed = Removed(ctx, slot);
        var tabs = new List<ViewTab>();
        if (form.Layout.Count > 0)
        {
            string[] order = ["1", "2", "3", "A", "B", "C"];
            string[] keys = ["T1V", "T2V", "T3V", "TAV", "TBV", "TCV"];
            foreach (var g in form.Layout.GroupBy(l => l.Tab).OrderBy(g => Array.IndexOf(order, g.Key)))
            {
                var fields = new List<ViewField>();
                foreach (var l in g)
                {
                    if (l.ControlType is 22 or 23 or 24 or 26 or 30) continue; // nút, nhóm, HTML: không có ở chế độ xem
                    if (l.VisiblePermiss == 7 && !ctx.Perm.SeesAll || l.VisiblePermiss == 9 && !ctx.Perm.Admin) continue;
                    if (removed.Contains(l.Field) || IsSecret(l.Field) || !index.TryGetValue(l.Field, out var i)) continue;
                    // Caption của SY_FrmDrdwTbl đè nhãn LYT (FormControler.SetEditorArr, khi ngôn ngữ VN).
                    var dd = form.MasterDropdown(l.Field);
                    var caption = dd?.Caption ?? l.Caption ?? ctx.Form.Captions.GetValueOrDefault($"C:{l.Field}") ?? Title(ctx, "F0", l.Field);
                    fields.Add(await LookupFieldAsync(ctx, slot, dd, Field(ctx, l.Field, caption, l.ControlType, master.Types[i], row[i], l.Span, l.Rows, l.RowStart),
                        l.ControlType, value, c, tx, ct));
                }
                var k = Array.IndexOf(order, g.Key);
                if (fields.Count > 0)
                    tabs.Add(new ViewTab(g.Key, form.Setting(keys[k]) ?? (k == 0 ? "Thông tin chung" : $"Thông tin {k + 1}"), fields));
            }
        }
        if (tabs.Count == 0)
        {
            // Không có LYT: hiện mọi cột (như form chưa cấu hình bố cục), mỗi ô 1/4 dòng.
            var fields = new List<ViewField>();
            var hidden = new HashSet<string>(slot.Hidden.Concat(SystemHidden), StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < master.Names.Length; i++)
            {
                var n = master.Names[i];
                if (removed.Contains(n) || hidden.Contains(n) || master.Types[i] == typeof(byte[]) || IsSecret(n)) continue;
                var dd = form.MasterDropdown(n);
                var caption = dd?.Caption ?? ctx.Form.Captions.GetValueOrDefault($"C:{n}") ?? Title(ctx, "F0", n);
                fields.Add(await LookupFieldAsync(ctx, slot, dd, Field(ctx, n, caption, 0, master.Types[i], row[i], 10, 1, false), 0, value, c, tx, ct));
            }
            tabs.Add(new ViewTab("1", "Thông tin chung", fields));
        }
        return tabs;
    }

    private static ViewField Field(Ctx ctx, string field, string caption, int controlType, Type type, object? value, int span, int rows, bool rowStart)
    {
        var (kind, decimals) = Kind(ctx, field, type, false);
        var control = kind switch
        {
            "bool" => "checkbox",
            "date" or "datetime" => kind,
            "number" or "money" or "quantity" or "percent" => kind,
            _ => controlType == 28 ? "label" : rows > 1 || value is string { Length: > 200 } ? "textarea" : "text",
        };
        return new ViewField(field, caption, control, value, span, rows, rowStart, decimals);
    }

    /// <summary>
    /// Ô có danh sách chọn / tra cứu (<c>SY_FrmDrdwTbl</c>): thành ô <c>lookup</c> (mã + chữ kèm), trừ ô ngày, có/không, chọn nhiều
    /// (ControlType 3, DropCheck), nhãn và nhiều dòng.
    /// </summary>
    private static async Task<ViewField> LookupFieldAsync(Ctx ctx, SlotMeta slot, DropdownMeta? d, ViewField f, int controlType, Func<string, object?> master,
        SqlConnection c, SqlTransaction tx, CancellationToken ct)
    {
        if (d is null || !d.CanLookup || controlType == 3 || f.Control is "checkbox" or "date" or "datetime" or "label" or "textarea") return f;
        if (!d.IsValueList && LookupSource(d) is null) return f;
        var (text, paras) = await MasterLookupAsync(ctx, slot, d, f.Value, master, c, tx, ct);
        return f with { Control = "lookup", Text = text, LookupParas = paras, Decimals = null };
    }

    /// <summary>Tab độc lập (IND) của form: hiện dưới lưới danh sách; tab tùy chọn (OPT) bỏ nếu bảng không có ở DB của khách.</summary>
    private async Task<List<ViewDetailTab>> IndependentTabsAsync(Ctx ctx, FormMeta form, CancellationToken ct)
    {
        var tabs = new List<ViewDetailTab>();
        foreach (var d in form.Details.Where(d => d.Independent && DetailVisible(ctx, form, d)))
        {
            if (d.Optional && (await metadata.GetColumnsAsync(ctx.Req.Tenant, d.Source, ct)).Count == 0) continue;
            tabs.Add(new ViewDetailTab(d.Id, d.Caption ?? d.Id));
        }
        return tabs;
    }

    /// <summary>Giá trị một cột của dòng chứng từ (null nếu không có cột).</summary>
    private static Func<string, object?> MasterValue(SqlRows master) =>
        name => Array.FindIndex(master.Names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? master.Rows[0][i] : null;

    private static bool DetailVisible(Ctx ctx, FormMeta form, SlotMeta detail)
    {
        // TPV1…TPV5: tab chi tiết 1…5 — 0 mọi người, 1 Manager, 4 Admin.
        var n = Array.IndexOf(FormMeta.SlotIds.ToArray(), detail.Id);
        var p = n is >= 1 and <= 5 ? form.SettingInt($"TPV{n}", 0) : 0;
        if (detail.HideOnHideAmount && ctx.Perm.HideAmount) return false;
        return p switch { 1 => ctx.Perm.SeesAll, 4 => ctx.Perm.Admin, _ => true };
    }

    // ---------------------------------------------------------------- cột

    /// <summary>Cột bị loại khỏi dữ liệu trả về vì quyền: HDE2 (User), HDE3 (User + Manager), ACP khi ẩn tiền (QĐ-03 ngoại lệ 5).</summary>
    private static HashSet<string> Removed(Ctx ctx, SlotMeta slot)
    {
        var removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!ctx.Perm.SeesAll) removed.UnionWith(slot.HiddenForUser);
        if (!ctx.Perm.Admin) removed.UnionWith(slot.HiddenForManager);
        if (ctx.Perm.HideAmount) removed.UnionWith(slot.AmountColumns);
        return removed;
    }

    private static List<(int Index, ViewColumn Col)> Columns(Ctx ctx, SlotMeta slot, string formatKey, string[] names, Type[] types)
    {
        var removed = Removed(ctx, slot);
        var hidden = new HashSet<string>(slot.Hidden.Concat(SystemHidden), StringComparer.OrdinalIgnoreCase);
        var sum = new HashSet<string>(slot.SumColumns, StringComparer.OrdinalIgnoreCase);
        var amount = new HashSet<string>(slot.AmountColumns, StringComparer.OrdinalIgnoreCase);
        var gd2 = slot.Order.Select(o => Array.FindIndex(names, n => n.Equals(o, StringComparison.OrdinalIgnoreCase))).Where(i => i >= 0).ToList();
        // Bộ cột mặc định của web (khác WinForms, nơi GD2 chỉ sắp thứ tự và bộ cột gọn nằm trong tệp .dat của từng máy): có GD2 thì
        // chỉ hiện các cột GD2 + khóa + cột có tổng (SUM); cột khác ẩn sẵn (data-hidden), người dùng bật lại bằng nút "Cột".
        var shown = gd2.Count == 0 ? null
            : new HashSet<string>(gd2.Select(i => names[i]).Append(slot.PrimaryKey).Concat(slot.SumColumns), StringComparer.OrdinalIgnoreCase);
        var result = new List<(int, ViewColumn)>();
        foreach (var i in gd2.Concat(Enumerable.Range(0, names.Length)).Distinct())
        {
            var n = names[i];
            if (removed.Contains(n) || types[i] == typeof(byte[]) || IsSecret(n)) continue;
            var (kind, decimals) = Kind(ctx, n, types[i], amount.Contains(n));
            // Tiêu đề: "Ngôn ngữ form" của khách (SY_FrmCtrTbl G) → Caption của SY_FrmDrdwTbl (SetDropdownFromConfig) → F-key → SY_FmtFldTbl.
            // isInvisible của SY_FrmDrdwTbl ẩn cột.
            var dd = ctx.Form.GridDropdown(slot, n);
            var own = FormMeta.GridNames(slot).Select(g => ctx.Form.Captions.GetValueOrDefault($"G:{g};{n}")).FirstOrDefault(x => x is not null);
            result.Add((i, new ViewColumn(n, own ?? dd?.Caption ?? Title(ctx, formatKey, n), kind, decimals,
                hidden.Contains(n) || shown?.Contains(n) == false || dd?.Invisible == true,
                sum.Contains(n) && kind is "number" or "money" or "quantity" or "percent")));
        }
        return result;
    }

    private static string Title(Ctx ctx, string formatKey, string field) =>
        ctx.Form.Captions.GetValueOrDefault($"{formatKey}:{field}") ?? ctx.Formats.Caption(field, ctx.Form.RuntimeName, ctx.Form.FormName) ?? field;

    /// <summary>Kiểu hiển thị theo kiểu dữ liệu + <c>SY_FmtFldTbl.FormatID</c> (tài liệu 11 §1).</summary>
    private static (string Kind, int? Decimals) Kind(Ctx ctx, string field, Type type, bool amount)
    {
        var f = ctx.Formats.Get(field, ctx.Form.RuntimeName, ctx.Form.FormName);
        var id = f?.FormatId?.ToUpperInvariant() ?? "";
        if (type == typeof(bool)) return ("bool", null);
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return (id == "DT" ? "datetime" : "date", null);
        var integral = type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte);
        if (!integral && type != typeof(decimal) && type != typeof(double) && type != typeof(float)) return ("text", null);
        if (id is "NS" or "Y") return ("text", null);
        if (id is "B" or "S" || amount) return ("money", f?.Decimals ?? 0);
        if (id.StartsWith('Q')) return ("quantity", f?.Decimals ?? (integral ? 0 : 2));
        if (id.StartsWith('P')) return ("percent", f?.Decimals ?? 0);
        return ("number", f?.Decimals ?? (integral ? 0 : 2));
    }

    /// <summary>Cột nhóm mặc định (GCA) có trong lưới, nối bằng dấu phẩy (chuẩn 11.1 <c>data-group-by</c>).</summary>
    private static string? GroupBy(SlotMeta slot, List<(int Index, ViewColumn Col)> cols)
    {
        var g = MetadataCompiler.List(slot.GroupBy).Select(x => cols.FirstOrDefault(c => c.Col.Field.Equals(x, StringComparison.OrdinalIgnoreCase)).Col?.Field)
            .OfType<string>().ToList();
        return g.Count == 0 ? null : string.Join(",", g);
    }

    private static string FormatKey(string slotId) =>
        slotId.Length == 2 && char.IsAsciiDigit(slotId[1]) ? "F" + slotId[1] : "F-";

    // ---------------------------------------------------------------- tiện ích

    private static decimal SumRows(List<object?[]> rows, int index) =>
        rows.Sum(r => r[index] is null ? 0m : Convert.ToDecimal(r[index], CultureInfo.InvariantCulture));

    private static DateTime? ParseDate(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Invariant(object? v) => v switch
    {
        null => "",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>Chạy trong giao dịch LUÔN rollback: dù đoạn SQL metadata nào lọt qua kiểm chỉ đọc cũng không ghi được gì.</summary>
    private async Task<T> RunAsync<T>(Tenant tenant, Func<SqlConnection, SqlTransaction, Task<T>> work, Func<string, T> configError, CancellationToken ct)
    {
        await using var c = await connections.OpenAsync(tenant, ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            return await work(c, tx);
        }
        catch (UnsafeMetadataSqlException ex)
        {
            return configError(ex.Message);
        }
        catch (SqlException ex) when (ErpConnectionFactory.IsConnectivity(ex))
        {
            throw new ErpUnavailableException(tenant.Key, ex);
        }
        catch (SqlException ex)
        {
            return configError($"Câu truy vấn dựng từ cấu hình form bị lỗi (SQL {ex.Number}): {ex.Message}");
        }
        finally
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { } catch (SqlException) { }
        }
    }
}
