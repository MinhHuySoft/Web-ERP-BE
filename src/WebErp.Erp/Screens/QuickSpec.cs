using System.Text;
using System.Text.Json;

namespace WebErp.Erp.Screens;

/// <summary>Một bảng của mô tả "Tạo nhanh": bảng chính (T0) hoặc một tab chi tiết (T1…).</summary>
/// <param name="Columns">Cột hiện trên lưới, theo thứ tự chọn (khóa luôn được đọc kèm).</param>
/// <param name="MasterKey">Tab chi tiết: cột của bảng chính dùng để nối (<c>MUL</c>).</param>
/// <param name="DetailKey">Tab chi tiết: cột của bảng chi tiết trỏ về bảng chính (<c>MDL</c>).</param>
public sealed record QuickSlot(string Table, string PrimaryKey, IReadOnlyList<string> Columns, string? SortColumn = null, bool SortDesc = true,
    string? Caption = null, string? MasterKey = null, string? DetailKey = null);

/// <summary>
/// Mô tả form viết tay dựng bằng chọn / tích (trang quản trị → "Tạo nhanh"), không phải viết JSON. <see cref="ToJson"/> sinh tệp mô tả
/// cùng định dạng <c>Metadata/Overlays/*.json</c>: T0 bảng chính, T1… tab chi tiết nối bằng MUL / MDL.
/// </summary>
public sealed record QuickSpec(string Form, string Caption, string FormType, int Top, QuickSlot Master, IReadOnlyList<QuickSlot> Details)
{
    public string ToJson()
    {
        var cfg = new Dictionary<string, object>(StringComparer.Ordinal) { ["T0"] = Slot(Master, Top) };
        var n = 1;
        foreach (var d in Details) cfg[$"T{n++}"] = Slot(d, 0);
        var root = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["form"] = Form,
            ["formType"] = FormType,
            ["caption"] = Caption,
            ["useDbConfig"] = false,
            ["cfg"] = cfg,
        };
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return "// Tạo bằng trang quản trị → Tạo nhanh.\n" + json + "\n";
    }

    private static Dictionary<string, string> Slot(QuickSlot s, int top)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal) { ["TN"] = s.Table, ["PK"] = s.PrimaryKey };
        if (s.MasterKey is not null && s.DetailKey is not null)
        {
            d["MUL"] = s.MasterKey;
            d["MDL"] = s.DetailKey;
        }
        if (!string.IsNullOrWhiteSpace(s.Caption)) d["DCP"] = s.Caption.Trim();
        var cols = new List<string> { s.PrimaryKey };
        if (s.DetailKey is not null) cols.Add(s.DetailKey);
        cols.AddRange(s.Columns);
        cols = cols.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        d["SE"] = string.Join(", ", cols.Select(c => Qualified(s.Table, c)));
        if (s.SortColumn is not null) d["SO"] = Qualified(s.Table, s.SortColumn) + (s.SortDesc ? " DESC" : "");
        if (top > 0) d["TRS"] = top.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (s.Columns.Count > 0) d["GD2"] = string.Join(";", s.Columns);
        return d;
    }

    /// <summary><c>Bảng.Cột</c>; tên có ký tự lạ thì đặt trong ngoặc vuông.</summary>
    internal static string Qualified(string table, string column) => $"{Name(table)}.{Name(column)}";

    private static string Name(string n) =>
        n.Length > 0 && (char.IsAsciiLetter(n[0]) || n[0] == '_') && n.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')
            ? n : "[" + n.Replace("]", "]]", StringComparison.Ordinal) + "]";
}

/// <summary>Gợi ý ban đầu cho "Tạo nhanh": bảng chính, các bảng chi tiết có thể có (theo tên).</summary>
public sealed record QuickGuess(string? MasterTable, IReadOnlyList<string> DetailTables, IReadOnlyList<string> Candidates);

/// <summary>Đoán bảng theo tên form (quy ước ERP: <c>AR_OrderFrm</c> ↔ <c>AR_OrderTbl</c>, <c>AR_OrderDetailTbl</c>).</summary>
public static class QuickGuesser
{
    /// <summary>Phần gốc của tên form: bỏ đuôi <c>Frm</c> / <c>Form</c> và <c>List</c> (vd <c>AR_OrderListFrm</c> → <c>AR_Order</c>).</summary>
    public static string Base(string form)
    {
        var b = form;
        foreach (var suffix in new[] { "Frm", "Form" })
            if (b.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && b.Length > suffix.Length) { b = b[..^suffix.Length]; break; }
        if (b.EndsWith("List", StringComparison.OrdinalIgnoreCase) && b.Length > 4) b = b[..^4];
        return b;
    }

    /// <summary>Tiền tố module (<c>AR_</c>, <c>CS_</c>…) hoặc rỗng.</summary>
    public static string Prefix(string name)
    {
        var i = name.IndexOf('_', StringComparison.Ordinal);
        return i is > 0 and <= 5 ? name[..(i + 1)] : "";
    }

    public static QuickGuess Guess(string form, IReadOnlyList<string> objects)
    {
        var set = new HashSet<string>(objects, StringComparer.OrdinalIgnoreCase);
        var b = Base(form);
        string? Find(params string[] names) => names.FirstOrDefault(set.Contains) is { } n ? objects.First(o => o.Equals(n, StringComparison.OrdinalIgnoreCase)) : null;
        var master = Find(b + "Tbl", b, b + "sTbl", b + "s", b + "ListTbl");
        var candidates = Rank(form, objects).Take(40).ToList();
        master ??= candidates.FirstOrDefault(c => !c.Contains("Detail", StringComparison.OrdinalIgnoreCase));
        var details = new List<string>();
        if (master is not null)
        {
            var mb = master.EndsWith("Tbl", StringComparison.OrdinalIgnoreCase) ? master[..^3] : master;
            foreach (var o in objects)
            {
                if (o.Equals(master, StringComparison.OrdinalIgnoreCase)) continue;
                if (o.StartsWith(mb + "Detail", StringComparison.OrdinalIgnoreCase) || o.StartsWith(mb + "_Detail", StringComparison.OrdinalIgnoreCase)
                    || o.StartsWith(mb + "Item", StringComparison.OrdinalIgnoreCase) && o.EndsWith("Tbl", StringComparison.OrdinalIgnoreCase))
                    details.Add(o);
            }
        }
        return new QuickGuess(master, details.Take(3).ToList(), candidates);
    }

    /// <summary>Xếp bảng / view theo độ giống tên form (cùng tiền tố module, chung từ trong tên).</summary>
    public static IEnumerable<string> Rank(string form, IReadOnlyList<string> objects)
    {
        var prefix = Prefix(form);
        var words = Words(Base(form)[prefix.Length..]);
        return objects
            .Select(o => (Name: o, Score: Score(o)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score).ThenBy(x => x.Name.Length).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Name);

        int Score(string o)
        {
            var s = 0;
            if (prefix.Length > 0 && o.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s += 2;
            var ow = Words(o[Prefix(o).Length..]);
            s += 3 * words.Count(w => ow.Contains(w));
            if (o.EndsWith("Tbl", StringComparison.OrdinalIgnoreCase)) s += 1;
            return words.Count > 0 && !words.Any(ow.Contains) ? 0 : s;
        }
    }

    /// <summary>Tách từ theo chữ hoa / gạch dưới, bỏ từ chung (Tbl, Frm, View…): <c>HoSoKhachHang</c> → ho, so, khach, hang.</summary>
    internal static HashSet<string> Words(string name)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length >= 2 && !Noise.Contains(sb.ToString())) words.Add(sb.ToString());
            sb.Clear();
        }
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (ch == '_' || char.IsDigit(ch)) { Flush(); continue; }
            if (char.IsUpper(ch) && sb.Length > 0 && !char.IsUpper(name[i - 1])) Flush();
            sb.Append(ch);
        }
        Flush();
        return words;
    }

    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase) { "Tbl", "Frm", "Form", "View", "List", "Fnc", "Stp", "Report" };
}
