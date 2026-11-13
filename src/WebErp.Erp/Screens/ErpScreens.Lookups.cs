using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Metadata;
using WebErp.Erp.Connection;
using WebErp.Erp.Query;

namespace WebErp.Erp.Screens;

/// <summary>
/// Danh sách chọn / tra cứu của <c>SY_FrmDrdwTbl</c> (tài liệu 11 §2): hiện tên thay mã trên lưới và ô chứng từ, tra cứu F4.
/// Nguồn là câu SELECT của metadata: qua kiểm chỉ đọc, token và <c>{0}</c>… thành tham số, chạy trong giao dịch luôn rollback như
/// mọi câu khác của màn. Nguồn lỗi thì giữ nguyên mã (màn vẫn mở được), không báo lỗi cấu hình cả màn.
/// </summary>
public sealed partial class ErpScreens
{
    /// <summary>Số dòng tối đa một lần tra cứu (chuẩn 10: khoảng 50; WinForms SearchTopRecord lấy 18).</summary>
    public const int LookupMax = 50;

    /// <summary>Số mã tối đa trong một câu <c>IN (…)</c> khi tra tên (giới hạn 2.100 tham số của SQL Server).</summary>
    private const int NameChunk = 500;

    public async Task<LookupView> LookupAsync(ScreenRequest req, string field, string text, IReadOnlyList<string> paras, CancellationToken ct)
    {
        var form = await metadata.GetFormAsync(req.Tenant, req.FormName, req.RuntimeName, ct);
        if (form?.Master is not { } slot || IsHandCodedWithoutOverlay(req.FormName)) return new LookupView(ScreenStatus.NotSupported);
        var d = form.MasterDropdown(field);
        if (d is null || !d.CanLookup) return new LookupView(ScreenStatus.NotFound);
        var ctx = await ContextAsync(req, form, ct);
        text = text.Trim();

        if (d.IsValueList)
        {
            var list = ValueListRows(d);
            var names = list.Count == 0 ? [] : list[0].Keys.Where(n => !IsSecret(n)).ToList();
            var hits = list.Where(r => text.Length == 0 || r.Values.Any(v => v.Contains(text, StringComparison.CurrentCultureIgnoreCase))).ToList();
            var cols = names.Select(n => new ViewColumn(n, ctx.Formats.Caption(n, form.RuntimeName, form.FormName) ?? n, "text", null, false, false)).ToList();
            var shown = hits.Take(LookupMax).ToList();
            return new LookupView(ScreenStatus.Ok, cols, shown.Select(r => names.Select(n => (object?)r[n]).ToArray()).ToList(),
                shown.Select(r => r.GetValueOrDefault(d.ValueColumn!) ?? "").ToList(),
                shown.Select(r => d.TextColumn is { } t ? r.GetValueOrDefault(t) ?? "" : "").ToList(), hits.Count);
        }

        var source = LookupSource(d);
        if (source is null) return new LookupView(ScreenStatus.NotSupported);
        object? Para(int i) => i < paras.Count ? paras[i] : "";
        return await RunAsync(req.Tenant, async (c, tx) =>
        {
            var probe = new SlotQuery(slot, ctx.Resolve);
            var schema = await SqlReader.ReadAsync(c, tx, $"SELECT TOP (0) * FROM ({probe.Fragment(source, $"tra cứu {d.Column}", Paras(d, Para))}) A",
                probe.Parameters, ctx.Timeout, 0, ct);
            string? Has(string? n) => n is null ? null : schema.Names.FirstOrDefault(x => x.Equals(n, StringComparison.OrdinalIgnoreCase));
            var value = Has(d.ValueColumn);
            if (value is null) return new LookupView(ScreenStatus.ConfigError, Note: $"Nguồn tra cứu của {field} không có cột {d.ValueColumn}.");
            var textColumn = Has(d.TextColumn);
            var listed = (d.ColumnArr ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Has).OfType<string>().ToList();
            if (listed.Count == 0) listed = new[] { value, textColumn }.OfType<string>().ToList();
            var columns = listed.Concat([value]).Concat(textColumn is null ? [] : [textColumn]).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(n => !IsSecret(n) || n.Equals(value, StringComparison.OrdinalIgnoreCase)).ToList();
            if (IsSecret(value) || textColumn is not null && IsSecret(textColumn)) return new LookupView(ScreenStatus.NotSupported);

            var q = new SlotQuery(slot, ctx.Resolve);
            var from = q.Fragment(source, $"tra cứu {d.Column}", Paras(d, Para));
            var where = "";
            if (text.Length > 0)
            {
                var like = columns.Where(n => schema.Types[Array.FindIndex(schema.Names, x => x.Equals(n, StringComparison.OrdinalIgnoreCase))] == typeof(string)).ToList();
                if (like.Count == 0) like = [value];
                var p = q.Param($"%{text.Replace("[", "[[]", StringComparison.Ordinal)}%");
                where = " WHERE " + string.Join(" OR ", like.Select(n => $"CAST(A.{Quote(n)} AS nvarchar(4000)) LIKE {p}"));
            }
            var data = await SqlReader.ReadAsync(c, tx,
                $"SELECT TOP ({LookupMax + 1}) {string.Join(", ", columns.Select(n => "A." + Quote(n)))} FROM ({from}) A{where} ORDER BY A.{Quote(value)}",
                q.Parameters, ctx.Timeout, LookupMax + 1, ct);
            var total = data.Rows.Count;
            if (total > LookupMax)
                total = await c.ExecuteScalarAsync<int>(new CommandDefinition($"SELECT COUNT(*) FROM ({from}) A{where}", q.Parameters, tx, ctx.Timeout, cancellationToken: ct));
            var rows = data.Rows.Take(LookupMax).ToList();
            var vi = columns.IndexOf(value);
            var ti = textColumn is null ? -1 : columns.IndexOf(textColumn);
            var cols = columns.Select(n => new ViewColumn(n, ctx.Formats.Caption(n, form.RuntimeName, form.FormName) ?? n, "text", null,
                !listed.Contains(n, StringComparer.OrdinalIgnoreCase), false)).ToList();
            return new LookupView(ScreenStatus.Ok, cols, rows, rows.Select(r => Invariant(r[vi])).ToList(),
                rows.Select(r => ti < 0 ? "" : Invariant(r[ti])).ToList(), total);
        }, e => new LookupView(ScreenStatus.ConfigError, Note: e), ct);
    }

    /// <summary>
    /// Tên thay mã cho các cột lưới có danh sách chọn (<c>DisplayColumn</c> khác <c>ValueColumn</c>), như GridEX hiện DisplayMember.
    /// Cột có tổng giữ số. Tham số <c>Master.Cột</c> lấy từ chứng từ đang xem (<paramref name="master"/>); tham số theo từng dòng thì bỏ.
    /// </summary>
    private static async Task<List<(int Index, ViewColumn Col)>> WithNamesAsync(Ctx ctx, SlotMeta slot, List<(int Index, ViewColumn Col)> cols,
        List<object?[]> rows, Func<string, object?>? master, SqlConnection c, SqlTransaction tx, CancellationToken ct)
    {
        if (rows.Count == 0 || ctx.Form.Dropdowns.Count == 0) return cols;
        var result = new List<(int, ViewColumn)>(cols.Count);
        foreach (var (i, col) in cols)
        {
            var d = ctx.Form.GridDropdown(slot, col.Field);
            if (d?.NameColumn is not { } name || col.Sum || col.Type is "bool" or "date" or "datetime"
                || d.Paras.Any(p => !p.Master || master is null))
            {
                result.Add((i, col));
                continue;
            }
            var codes = rows.Select(r => r[i]).Where(v => ViewColumn.CodeKey(v) is { Length: > 0 })
                .DistinctBy(v => ViewColumn.CodeKey(v)!, StringComparer.OrdinalIgnoreCase).Select(v => v!).ToList();
            var map = IsSecret(name) ? null : await NamesAsync(ctx, slot, d, name, codes, k => master!(d.Paras[k].Field), c, tx, ct);
            result.Add((i, map is { Count: > 0 } ? col with { Type = "text", Decimals = null, Names = map } : col));
        }
        return result;
    }

    /// <summary>
    /// Mã → chữ của cột <paramref name="textColumn"/> trong nguồn danh sách chọn. Null nếu nguồn không chạy được trên web (thủ tục, WITH,
    /// SQL lỗi, lệnh ghi): ô giữ nguyên mã như khi WinForms không nạp được danh sách.
    /// </summary>
    private static async Task<Dictionary<string, string>?> NamesAsync(Ctx ctx, SlotMeta slot, DropdownMeta d, string textColumn, IReadOnlyList<object> codes,
        Func<int, object?> para, SqlConnection c, SqlTransaction tx, CancellationToken ct)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (codes.Count == 0 || d.ValueColumn is null) return map;
        if (d.IsValueList)
        {
            var wanted = new HashSet<string>(codes.Select(v => ViewColumn.CodeKey(v)!), StringComparer.OrdinalIgnoreCase);
            foreach (var r in ValueListRows(d))
            {
                if (r.TryGetValue(d.ValueColumn, out var v) && wanted.Contains(v.TrimEnd()) && r.TryGetValue(textColumn, out var t)) map.TryAdd(v.TrimEnd(), t);
            }
            return map;
        }
        var source = LookupSource(d);
        if (source is null) return null;
        try
        {
            foreach (var chunk in codes.Chunk(NameChunk))
            {
                var q = new SlotQuery(slot, ctx.Resolve);
                var from = q.Fragment(source, $"tra cứu {d.Column}", Paras(d, para));
                var v = Quote(d.ValueColumn);
                var sql = $"SELECT A.{v}, A.{Quote(textColumn)} FROM ({from}) A WHERE A.{v} IN ({string.Join(", ", chunk.Select(q.Param))})";
                var data = await SqlReader.ReadAsync(c, tx, sql, q.Parameters, ctx.Timeout, chunk.Length * 4, ct);
                foreach (var row in data.Rows)
                {
                    if (ViewColumn.CodeKey(row[0]) is { Length: > 0 } k) map.TryAdd(k, Invariant(row[1]));
                }
            }
            return map;
        }
        catch (UnsafeMetadataSqlException)
        {
            return null;
        }
        catch (SqlException ex) when (!ErpConnectionFactory.IsConnectivity(ex))
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // Giao dịch đã bị SQL Server hủy bởi một lỗi trước đó: bỏ tên, giữ mã.
            return null;
        }
    }

    /// <summary>Ô chứng từ có danh sách chọn: chữ kèm mã (tên thay mã / DisplayMember2) và giá trị tham số cho URL tra cứu.</summary>
    private static async Task<(string? Text, IReadOnlyList<string> Paras)> MasterLookupAsync(Ctx ctx, SlotMeta slot, DropdownMeta d, object? value,
        Func<string, object?> master, SqlConnection c, SqlTransaction tx, CancellationToken ct)
    {
        var paras = d.Paras.Select(p => Invariant(master(p.Field))).ToList();
        if (d.TextColumn is not { } textColumn || IsSecret(textColumn) || ViewColumn.CodeKey(value) is not { Length: > 0 } key) return (null, paras);
        var map = await NamesAsync(ctx, slot, d, textColumn, [value!], i => i < paras.Count ? paras[i] : "", c, tx, ct);
        return (map?.GetValueOrDefault(key), paras);
    }

    /// <summary>Câu nguồn chạy được trên web: SELECT (bỏ ORDER BY cuối vì không đặt được trong bảng dẫn xuất); thủ tục, WITH thì không.</summary>
    internal static string? LookupSource(DropdownMeta d)
    {
        if (d.Source is null || d.IsValueList) return null;
        var s = d.Source.Trim();
        return s.StartsWith("select", StringComparison.OrdinalIgnoreCase) && s.Length > 6 && !char.IsAsciiLetterOrDigit(s[6]) ? StripTrailingOrderBy(s) : null;
    }

    /// <summary>Cắt <c>ORDER BY</c> ở cấp ngoài cùng (ngoài ngoặc, chuỗi, chú thích) nếu là mệnh đề cuối câu.</summary>
    internal static string StripTrailingOrderBy(string sql)
    {
        var depth = 0;
        var cut = -1;
        for (var i = 0; i < sql.Length; i++)
        {
            var ch = sql[i];
            if (ch == '\'')
            {
                i++;
                while (i < sql.Length && !(sql[i] == '\'' && (i + 1 >= sql.Length || sql[i + 1] != '\''))) i += sql[i] == '\'' ? 2 : 1;
                continue;
            }
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-') { var e = sql.IndexOf('\n', i); i = e < 0 ? sql.Length : e; continue; }
            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*') { var e = sql.IndexOf("*/", i + 2, StringComparison.Ordinal); i = e < 0 ? sql.Length : e + 1; continue; }
            if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (depth == 0 && (ch is 'o' or 'O') && (i == 0 || !IsWord(sql[i - 1])) && string.Compare(sql, i, "order", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var j = i + 5;
                if (j < sql.Length && IsWord(sql[j])) continue;
                while (j < sql.Length && char.IsWhiteSpace(sql[j])) j++;
                if (string.Compare(sql, j, "by", 0, 2, StringComparison.OrdinalIgnoreCase) == 0 && (j + 2 >= sql.Length || !IsWord(sql[j + 2]))) cut = i;
            }
        }
        return cut < 0 ? sql : sql[..cut].TrimEnd();

        static bool IsWord(char ch) => char.IsLetterOrDigit(ch) || ch is '_' or '@' or '#' or '$' or '.' or '[' or ']';
    }

    /// <summary>
    /// Danh sách tĩnh (<c>ValueList</c>/<c>ValueLstCb</c>/<c>LangList</c>): <c>Source</c> tách bằng <c>;</c>, mỗi dòng đủ số cột của
    /// <c>ColumnArr</c> (<c>Functions.String2DataTable</c>).
    /// </summary>
    internal static List<Dictionary<string, string>> ValueListRows(DropdownMeta d)
    {
        var cols = (d.ColumnArr ?? $"{d.ValueColumn};{d.DisplayColumn}").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var parts = (d.Source ?? "").Split(';');
        var rows = new List<Dictionary<string, string>>();
        if (cols.Length == 0) return rows;
        for (var i = 0; i + cols.Length <= parts.Length; i += cols.Length)
        {
            var r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var j = 0; j < cols.Length; j++) r[cols[j]] = parts[i + j].Trim();
            rows.Add(r);
        }
        return rows;
    }

    /// <summary>Token <c>{0}</c>, <c>{1}</c>… của nguồn → tham số theo <c>ParaArr</c>.</summary>
    private static Func<string, (bool, object?)> Paras(DropdownMeta d, Func<int, object?> value) =>
        t => int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var k) && k < d.Paras.Count
            ? (true, value(k) ?? "") : (false, null);

    private static string Quote(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
