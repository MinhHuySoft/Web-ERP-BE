using System.Text.Json;

namespace WebErp.Erp.Metadata;

/// <summary>
/// Tệp mô tả form viết tay (<c>Metadata/Overlays/&lt;FormName&gt;.json</c>, nhúng vào dll). Form viết tay của WinForms nạp dữ liệu bằng
/// code riêng, không có <c>SY_FrmCfg</c>; tệp này mô tả lại cách nạp đó bằng đúng mô hình slot của <c>SY_FrmCfg</c>
/// (T0 bảng chính, T1… tab chi tiết; key TN/TV/PK/SE/join/SO/FTX/MUL/MDL/HDE/SUM/ACP/DCP…) để engine metadata dựng màn như
/// mọi form khác. Key riêng của web: <c>FTW</c> = điều kiện luôn áp (vd <c>isBank = 1</c> theo Para).
/// <code>
/// { "form": "CS_ReceiptFrm", "formType": "EDIT", "caption": "Phiếu thu", "useDbConfig": true,
///   "cfg":  { "SP": "9", "DFP": "DocumentDate", "T0": { "TN": "CS_ReceiptTbl", "PK": "DocumentID" }, "T1": { … } },
///   "para": { "": { "T0": { "FTW": "isBank = 0" } }, "Bank": { "T0": { "FTW": "isBank = 1" } } } }
/// </code>
/// <c>useDbConfig</c>: form viết tay có đọc <c>SY_FrmCfg</c> (vd T0/T1 nếu có) thì cấu hình DB đứng trước, tệp này bù phần thiếu.
/// <c>flt</c>: ô lọc viết cứng trong form (cùng cột với <c>SY_FrmFltTbl</c>), nối sau dòng của DB.
/// Chuỗi <c>{Mod}</c> trong giá trị = mã module của công ty (bảng <c>{Mod}_Menu</c>, <c>{Mod}_UserLog</c>…).
/// </summary>
/// <summary>Kết quả kiểm tệp mô tả (không cần DB): tên form, các bí danh, hoặc lỗi.</summary>
public sealed record OverlayValidation(string? Form, IReadOnlyList<string> Aliases, string? Error);

public static class FormOverlays
{
    internal sealed record Overlay(string Form, string FormType, string Caption, bool UseDbConfig,
        IReadOnlyList<FrmCfgRow> Base, IReadOnlyDictionary<string, IReadOnlyList<FrmCfgRow>> Para, IReadOnlyList<FrmFltRow> Filters)
    {
        /// <summary>Dòng <c>SY_FrmLstTbl</c> + dòng cấu hình (của Para trước, rồi dòng chung).</summary>
        public (FrmLstRow Lst, IReadOnlyList<FrmCfgRow> Rows, bool UseDbConfig, IReadOnlyList<FrmFltRow> Filters) Resolve(string? para)
        {
            var rows = new List<FrmCfgRow>();
            if (Para.TryGetValue(para ?? "", out var p)) rows.AddRange(p);
            rows.AddRange(Base);
            return (new FrmLstRow(Form, FormType, Caption, null, null, null, null, null, null, null), rows, UseDbConfig, Filters);
        }
    }

    private static readonly JsonDocumentOptions JsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static readonly Lazy<IReadOnlyDictionary<string, Overlay>> All = new(Load);

    /// <summary>Tệp mô tả nhúng trong BE (theo tên form, gồm cả bí danh).</summary>
    internal static IReadOnlyDictionary<string, Overlay> BuiltIn => All.Value;

    public static bool Has(string formName) => All.Value.ContainsKey(formName);

    public static IReadOnlyCollection<string> Names => All.Value.Keys.ToList();

    /// <summary>Dòng <c>SY_FrmLstTbl</c> + dòng cấu hình (của Para trước, rồi dòng chung) cho form; null nếu không có tệp mô tả.</summary>
    public static (FrmLstRow Lst, IReadOnlyList<FrmCfgRow> Rows, bool UseDbConfig, IReadOnlyList<FrmFltRow> Filters)? Get(string formName, string? para) =>
        All.Value.TryGetValue(formName, out var o) ? o.Resolve(para) : null;

    /// <summary>
    /// Nội dung JSON gốc của tệp mô tả nhúng (để trang quản trị chép ra sửa); null nếu form không có tệp nhúng. Bí danh trả tệp của form gốc.
    /// </summary>
    public static string? BuiltInJson(string formName)
    {
        if (!All.Value.TryGetValue(formName, out var o)) return null;
        var asm = typeof(FormOverlays).Assembly;
        foreach (var name in Resources(asm))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            var text = r.ReadToEnd();
            using var doc = JsonDocument.Parse(text, JsonOptions);
            if (doc.RootElement.TryGetProperty("form", out var f) && f.ValueKind == JsonValueKind.String
                && string.Equals(f.GetString(), Origin(o, doc.RootElement), StringComparison.OrdinalIgnoreCase))
                return text;
        }
        return null;

        static string Origin(Overlay o, JsonElement root) =>
            root.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Array
            && al.EnumerateArray().Any(x => string.Equals(x.GetString(), o.Form, StringComparison.OrdinalIgnoreCase))
                ? root.GetProperty("form").GetString()!
                : o.Form;
    }

    /// <summary>
    /// Đọc một tệp mô tả (JSON, cho phép chú thích <c>//</c>): trả form chính và các bí danh. Ném <see cref="InvalidDataException"/>
    /// (thông điệp tiếng Việt, không chứa dữ liệu) nếu JSON sai hoặc thiếu khóa bắt buộc.
    /// </summary>
    internal static IReadOnlyList<Overlay> ParseText(string json, string source)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source}: JSON sai ở dòng {(ex.LineNumber ?? 0) + 1}, vị trí {(ex.BytePositionInLine ?? 0) + 1}.");
        }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException($"{source}: tệp mô tả phải là một đối tượng JSON {{ … }}.");
            try
            {
                return Expand(doc.RootElement, source);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException or KeyNotFoundException)
            {
                throw new InvalidDataException($"{source}: sai kiểu dữ liệu ({ex.Message}).");
            }
        }
    }

    /// <summary>
    /// Kiểm một tệp mô tả trước khi lưu (không cần DB): đọc được, có <c>form</c> / <c>formType</c> / <c>caption</c>, và mọi đoạn SQL của
    /// mọi slot (chung và theo Para) qua kiểm chỉ đọc như lúc Host chạy. Trả tên form + bí danh, hoặc lỗi.
    /// </summary>
    public static OverlayValidation Validate(string json)
    {
        IReadOnlyList<Overlay> parsed;
        try
        {
            parsed = ParseText(json, "Mô tả");
        }
        catch (InvalidDataException ex)
        {
            return new OverlayValidation(null, [], ex.Message);
        }
        var o = parsed[0];
        try
        {
            foreach (var para in o.Para.Keys.Prepend(""))
            {
                var r = o.Resolve(para);
                var form = MetadataCompiler.Compile(o.Form, o.Form, r.Lst, r.Rows, r.Filters);
                foreach (var slot in form.Slots.Values)
                {
                    var texts = new[] { slot.Source, slot.FromOverride, slot.Select, slot.Expression, slot.Filter, slot.FilterEx, slot.FilterWeb, slot.Sort }
                        .Concat(slot.Joins.SelectMany(j => new[] { j.Table, j.Select, j.Extra }));
                    foreach (var t in texts) Query.SqlTemplate.EnsureReadOnly(t, $"{(para.Length == 0 ? "" : para + ".")}{slot.Id}");
                }
                foreach (var f in r.Filters)
                {
                    Query.SqlTemplate.EnsureReadOnly(f.Source, $"flt {f.ColumnID}");
                    Query.SqlTemplate.EnsureReadOnly(f.WhereString, $"flt {f.ColumnID}");
                }
            }
        }
        catch (Query.UnsafeMetadataSqlException ex)
        {
            return new OverlayValidation(o.Form, [], ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or ArgumentException)
        {
            return new OverlayValidation(o.Form, [], $"Mô tả không dựng được form ({ex.Message}).");
        }
        return new OverlayValidation(o.Form, parsed.Skip(1).Select(x => x.Form).ToList(), null);
    }

    private static List<Overlay> Expand(JsonElement root, string source)
    {
        var o = Parse(root, source);
        var list = new List<Overlay> { o };
        // "aliases": lớp WinForms kế thừa nguyên form này (vd AR_Invoice2Frm : AR_InvoiceFrm), chỉ khác khóa cấu hình / quyền.
        // Dùng chung mô tả nhưng FormID = tên bí danh, để SY_FrmCfg / SY_FrmLstTbl / quyền đọc theo tên đó như WinForms.
        if (root.TryGetProperty("aliases", out var al) && al.ValueKind == JsonValueKind.Array)
            foreach (var a in al.EnumerateArray().Select(x => x.GetString()).OfType<string>())
                list.Add(o with { Form = a });
        return list;
    }

    private static IEnumerable<string> Resources(System.Reflection.Assembly asm) =>
        asm.GetManifestResourceNames().Where(n => n.StartsWith("WebErp.Overlays.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal));

    private static Dictionary<string, Overlay> Load()
    {
        var asm = typeof(FormOverlays).Assembly;
        var result = new Dictionary<string, Overlay>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Resources(asm))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(s, JsonOptions);
            foreach (var o in Expand(doc.RootElement, name)) result[o.Form] = o;
        }
        return result;
    }

    internal static Overlay Parse(JsonElement root, string source)
    {
        string Str(string p) => root.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException($"{source}: thiếu '{p}'.");
        var para = new Dictionary<string, IReadOnlyList<FrmCfgRow>>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("para", out var pe))
            foreach (var p in pe.EnumerateObject()) para[p.Name] = Rows(p.Value);
        var filters = new List<FrmFltRow>();
        if (root.TryGetProperty("flt", out var fe))
        {
            foreach (var f in fe.EnumerateArray())
            {
                string? S(string n) => f.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null ? Text(v) : null;
                int? I(string n) => f.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
                filters.Add(new FrmFltRow(S("KeyID"), S("ColumnID"), S("Caption"), I("Type"), S("Source"), S("ValueColumn"), S("DisplayColumn"),
                    I("Operator"), S("WhereString"), S("DefaultValue"), false));
            }
        }
        return new Overlay(Str("form"), Str("formType"), Str("caption"),
            root.TryGetProperty("useDbConfig", out var u) && u.ValueKind == JsonValueKind.True,
            root.TryGetProperty("cfg", out var c) ? Rows(c) : [], para, filters);
    }

    /// <summary><c>{ "SP": "9", "T0": { "TN": "X" } }</c> → dòng <c>(SP, "", 9)</c>, <c>(T0, TN, X)</c>.</summary>
    private static List<FrmCfgRow> Rows(JsonElement cfg)
    {
        var rows = new List<FrmCfgRow>();
        foreach (var k in cfg.EnumerateObject())
        {
            // F0…F9 / C: tiêu đề cột / nhãn ô (MetadataCompiler đọc dòng có SubValue "CV").
            var caption = k.Name == "C" || k.Name.Length == 2 && k.Name[0] is 'F' && char.IsAsciiDigit(k.Name[1]);
            if (k.Value.ValueKind == JsonValueKind.Object)
                foreach (var s in k.Value.EnumerateObject()) rows.Add(new FrmCfgRow(k.Name, s.Name, Text(s.Value), caption ? "CV" : null));
            else rows.Add(new FrmCfgRow(k.Name, "", Text(k.Value), null));
        }
        return rows;
    }

    private static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!,
        JsonValueKind.Array => string.Join(";", v.EnumerateArray().Select(x => x.GetString())),
        _ => v.GetRawText(),
    };
}
