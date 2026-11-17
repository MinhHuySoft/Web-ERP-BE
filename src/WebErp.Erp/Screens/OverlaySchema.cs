using System.Text;
using System.Text.RegularExpressions;
using WebErp.Core.Metadata;

namespace WebErp.Erp.Screens;

/// <summary>
/// Cột mà một slot của tệp mô tả form viết tay (<c>Metadata/Overlays</c>) cần có trong DB. Tệp mô tả được viết theo DB của một khách
/// (vd Medcom); DB khách khác có thể thiếu cột (TinPhong: <c>NguoiDungFrm</c> báo SQL 207). Kiểm trước bằng <c>sys.columns</c>.
/// <list type="bullet">
/// <item>Cột chỉ để HIỂN THỊ (một mục của <c>SE</c> / <c>SE1…</c> / <c>EX</c>) mà DB không có: bỏ mục đó, như WinForms đọc <c>*</c> khi
/// không có cấu hình (<c>NguoiDungFrm.cs</c> SetData "*"). Bỏ cột chỉ làm bớt dữ liệu hiện ra, không bao giờ lộ thêm.</item>
/// <item>Thiếu bảng, hoặc thiếu cột dùng ở khóa nối / điều kiện lọc / sắp xếp: không chạy, báo "chưa có trên web cho dữ liệu của công ty
/// này" (bỏ điều kiện lọc có thể làm lộ dữ liệu).</item>
/// </list>
/// Chỉ xét tham chiếu có tiền tố <c>Bảng.Cột</c> / <c>BíDanh.Cột</c> với bảng / bí danh của slot (bảng chính, view, bảng nối), cùng khóa
/// nối. Bỏ qua chuỗi trong nháy đơn, token <c>{…}</c>, và tham chiếu tới bảng không thuộc slot (vd truy vấn con).
/// </summary>
public static partial class OverlaySchema
{
    [GeneratedRegex(@"'(?:[^']|'')*'|\{[^}]*\}")]
    private static partial Regex LiteralsAndTokens();

    [GeneratedRegex(@"(?<![A-Za-z0-9_\.\]])\[?([A-Za-z_][A-Za-z0-9_]*)\]?\.\[?([A-Za-z_][A-Za-z0-9_]*)\]?")]
    private static partial Regex Qualified();

    [GeneratedRegex(@"^(dbo\.)?[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.IgnoreCase)]
    private static partial Regex PlainObject();

    /// <summary>Kết quả đối chiếu: slot chạy được (có thể đã bỏ vài cột hiển thị), hoặc mô tả phần thiếu.</summary>
    /// <param name="Dropped">Cột hiển thị đã bỏ vì DB không có (<c>Bảng.Cột</c>), để ghi log.</param>
    public sealed record Resolution(SlotMeta? Slot, IReadOnlyList<string> Dropped, string? Missing);

    /// <summary>Bảng / view (tên thật, đã bỏ <c>dbo.</c>) → các cột slot cần.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Required(SlotMeta slot) =>
        Analyze(slot).Required.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.OrdinalIgnoreCase);

    private static (Dictionary<string, string> Aliases, Dictionary<string, HashSet<string>> Required) Analyze(SlotMeta slot)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Alias(string? alias, string? table)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(table) || !PlainObject().IsMatch(table)) return;
            aliases[alias] = Bare(table);
        }
        void Need(string? table, string? column)
        {
            var col = column?.Trim('[', ']', ' ');
            if (table is null || string.IsNullOrEmpty(col) || !PlainObject().IsMatch(table) || !PlainObject().IsMatch(col) || col.Contains('.')) return;
            var t = Bare(table);
            if (!result.TryGetValue(t, out var set)) result[t] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(col);
        }

        // FRM (bảng dẫn xuất đặt bí danh bằng TN): cột của bảng chính là cột của bảng dẫn xuất, không kiểm được bằng sys.columns.
        var mainChecked = slot.FromOverride is null;
        void Table(string? table)
        {
            if (table is not null && PlainObject().IsMatch(table)) result.TryAdd(Bare(table), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        // Bảng chính và bảng nối luôn phải có, kể cả khi slot không nêu cột nào (SE trống = đọc "*").
        if (mainChecked) Table(slot.Source);
        foreach (var j in slot.Joins) Table(j.Table);
        if (mainChecked)
        {
            Alias(slot.Table, slot.Source);
            Alias(Bare(slot.Table), slot.Source);
            if (!string.IsNullOrEmpty(slot.View)) Alias(slot.View, slot.View);
        }
        foreach (var j in slot.Joins)
        {
            Alias(j.Alias, j.Table);
            Alias(j.Table, j.Table);
            Need(j.Table, j.RightKey);
            if (mainChecked || j.LeftTable is not null) Need(j.LeftTable is null ? slot.Source : aliases.GetValueOrDefault(j.LeftTable, j.LeftTable), j.LeftKey);
        }

        foreach (var text in Texts(slot))
        {
            foreach (var (table, column) in References(text, aliases)) Need(table, column);
        }
        return (aliases, result);
    }

    private static IEnumerable<string?> Texts(SlotMeta slot) =>
        new[] { slot.Select, slot.Expression, slot.Filter, slot.FilterEx, slot.FilterExCheck, slot.FilterWeb, slot.Sort }
            .Concat(slot.Joins.Select(j => j.Select)).Concat(slot.Joins.Select(j => j.Extra));

    private static IEnumerable<(string Table, string Column)> References(string? text, IReadOnlyDictionary<string, string> aliases)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        var clean = LiteralsAndTokens().Replace(text, " ");
        foreach (Match m in Qualified().Matches(clean))
        {
            if (aliases.TryGetValue(m.Groups[1].Value, out var table)) yield return (table, m.Groups[2].Value);
        }
    }

    /// <summary>
    /// Đối chiếu với cột thật (<paramref name="columnsOf"/>: tên bảng → cột trong DB, rỗng nếu không có bảng). Bỏ mục hiển thị dùng cột
    /// không có; phần thiếu còn lại (bảng, khóa nối, lọc, sắp xếp) thì trả <see cref="Resolution.Missing"/>.
    /// </summary>
    public static async Task<Resolution> ResolveAsync(SlotMeta slot, Func<string, Task<IReadOnlySet<string>>> columnsOf)
    {
        var actual = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
        async Task<IReadOnlySet<string>> Cols(string t) => actual.TryGetValue(t, out var c) ? c : actual[t] = await columnsOf(t).ConfigureAwait(false);

        var (aliases, required) = Analyze(slot);
        var missingTables = new List<string>();
        var missing = new HashSet<(string, string)>();
        foreach (var (table, columns) in required)
        {
            var have = await Cols(table).ConfigureAwait(false);
            if (have.Count == 0) missingTables.Add(table);
            else foreach (var c in columns.Where(c => !have.Contains(c))) missing.Add((table, c));
        }
        if (missingTables.Count > 0) return new Resolution(null, [], Describe(missingTables, missing));
        if (missing.Count == 0) return new Resolution(slot, [], null);

        bool Uses(string item) => References(item, aliases).Any(r => missing.Contains((r.Table, r.Column)));
        var dropped = new List<string>();
        string? Prune(string? list)
        {
            if (string.IsNullOrWhiteSpace(list)) return list;
            var keep = new List<string>();
            foreach (var item in SplitTopLevel(list))
            {
                if (Uses(item)) dropped.Add(item.Trim());
                else keep.Add(item.Trim());
            }
            return keep.Count == 0 ? null : string.Join(", ", keep);
        }
        var select = Prune(slot.Select);
        // Bỏ hết mục của SE thì SlotQuery sẽ đọc "Bảng.*": không cho, coi như thiếu.
        if (slot.Select is not null && select is null) return new Resolution(null, [], Describe([], missing));
        var pruned = slot with
        {
            Select = select,
            Expression = Prune(slot.Expression),
            Joins = slot.Joins.Select(j => j with { Select = Prune(j.Select) }).ToList(),
        };

        // Phần thiếu còn lại nằm ngoài danh sách hiển thị (khóa nối, lọc, sắp xếp): không chạy.
        var still = new HashSet<(string, string)>();
        foreach (var (table, columns) in Analyze(pruned).Required)
        {
            var have = await Cols(table).ConfigureAwait(false);
            foreach (var c in columns.Where(c => !have.Contains(c))) still.Add((table, c));
        }
        if (still.Count > 0) return new Resolution(null, [], Describe([], still));
        var droppedCols = missing.Select(m => $"{m.Item1}.{m.Item2}").Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new Resolution(pruned, droppedCols, null);
    }

    /// <summary>Như <see cref="ResolveAsync"/> nhưng chỉ trả mô tả phần thiếu (null nếu chạy được, kể cả sau khi bỏ cột hiển thị).</summary>
    public static async Task<string?> MissingAsync(SlotMeta slot, Func<string, Task<IReadOnlySet<string>>> columnsOf) =>
        (await ResolveAsync(slot, columnsOf).ConfigureAwait(false)).Missing;

    private static string Describe(IEnumerable<string> tables, IEnumerable<(string Table, string Column)> columns)
    {
        var parts = tables.Order(StringComparer.OrdinalIgnoreCase).Select(t => $"thiếu bảng {t}").ToList();
        var cols = columns.Select(c => $"{c.Table}.{c.Column}").Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (cols.Count > 0) parts.Add("thiếu cột " + string.Join(", ", cols));
        return string.Join("; ", parts);
    }

    /// <summary>Tách danh sách cột theo dấu phẩy ở cấp ngoài cùng (bỏ qua dấu phẩy trong ngoặc, nháy đơn, ngoặc vuông).</summary>
    internal static IEnumerable<string> SplitTopLevel(string list)
    {
        var sb = new StringBuilder();
        var depth = 0;
        var quote = false;
        var bracket = false;
        foreach (var ch in list)
        {
            if (quote) { if (ch == '\'') quote = false; }
            else if (bracket) { if (ch == ']') bracket = false; }
            else if (ch == '\'') quote = true;
            else if (ch == '[') bracket = true;
            else if (ch == '(') depth++;
            else if (ch == ')') depth--;
            else if (ch == ',' && depth == 0)
            {
                if (sb.ToString().Trim().Length > 0) yield return sb.ToString();
                sb.Clear();
                continue;
            }
            sb.Append(ch);
        }
        if (sb.ToString().Trim().Length > 0) yield return sb.ToString();
    }

    private static string Bare(string table) => table.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase) ? table[4..] : table;
}
