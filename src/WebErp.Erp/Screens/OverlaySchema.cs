using System.Text.RegularExpressions;
using WebErp.Core.Metadata;

namespace WebErp.Erp.Screens;

/// <summary>
/// Cột mà một slot của tệp mô tả form viết tay (<c>Metadata/Overlays</c>) cần có trong DB. Tệp mô tả được viết theo DB của một khách
/// (vd Medcom); DB khách khác có thể thiếu cột (TinPhong: <c>NguoiDungFrm</c> báo SQL 207). Kiểm trước bằng <c>sys.columns</c>: thiếu
/// thì báo "chưa có trên web cho dữ liệu của công ty này" và ghi log tên cột, không chạy câu truy vấn chính.
/// <para>
/// Chỉ xét tham chiếu có tiền tố <c>Bảng.Cột</c> / <c>BíDanh.Cột</c> với bảng / bí danh của slot (bảng chính, view, bảng nối), cùng khóa
/// nối. Bỏ qua chuỗi trong nháy đơn, token <c>{…}</c>, và tham chiếu tới bảng không thuộc slot (vd truy vấn con).
/// </para>
/// </summary>
public static partial class OverlaySchema
{
    [GeneratedRegex(@"'(?:[^']|'')*'|\{[^}]*\}")]
    private static partial Regex LiteralsAndTokens();

    [GeneratedRegex(@"(?<![A-Za-z0-9_\.\]])\[?([A-Za-z_][A-Za-z0-9_]*)\]?\.\[?([A-Za-z_][A-Za-z0-9_]*)\]?")]
    private static partial Regex Qualified();

    [GeneratedRegex(@"^(dbo\.)?[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.IgnoreCase)]
    private static partial Regex PlainObject();

    /// <summary>Bảng / view (tên thật, đã bỏ <c>dbo.</c>) → các cột slot cần.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> Required(SlotMeta slot)
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

        foreach (var text in new[] { slot.Select, slot.Expression, slot.Filter, slot.FilterEx, slot.Sort }.Concat(slot.Joins.Select(j => j.Select)).Concat(slot.Joins.Select(j => j.Extra)))
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var clean = LiteralsAndTokens().Replace(text, " ");
            foreach (Match m in Qualified().Matches(clean))
            {
                if (aliases.TryGetValue(m.Groups[1].Value, out var table)) Need(table, m.Groups[2].Value);
            }
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Đối chiếu với cột thật (<paramref name="columnsOf"/>: tên bảng → cột trong DB, rỗng nếu không có bảng). Trả mô tả phần thiếu
    /// ("thiếu bảng X; thiếu cột X.A, X.B"), null nếu đủ.
    /// </summary>
    public static async Task<string?> MissingAsync(SlotMeta slot, Func<string, Task<IReadOnlySet<string>>> columnsOf)
    {
        var missing = new List<string>();
        foreach (var (table, columns) in Required(slot).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var actual = await columnsOf(table).ConfigureAwait(false);
            if (actual.Count == 0)
            {
                missing.Add($"thiếu bảng {table}");
                continue;
            }
            var cols = columns.Where(c => !actual.Contains(c)).Order(StringComparer.OrdinalIgnoreCase).Select(c => $"{table}.{c}").ToList();
            if (cols.Count > 0) missing.Add("thiếu cột " + string.Join(", ", cols));
        }
        return missing.Count == 0 ? null : string.Join("; ", missing);
    }

    private static string Bare(string table) => table.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase) ? table[4..] : table;
}
