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
public static class FormOverlays
{
    internal sealed record Overlay(string Form, string FormType, string Caption, bool UseDbConfig,
        IReadOnlyList<FrmCfgRow> Base, IReadOnlyDictionary<string, IReadOnlyList<FrmCfgRow>> Para, IReadOnlyList<FrmFltRow> Filters);

    private static readonly Lazy<IReadOnlyDictionary<string, Overlay>> All = new(Load);

    public static bool Has(string formName) => All.Value.ContainsKey(formName);

    public static IReadOnlyCollection<string> Names => All.Value.Keys.ToList();

    /// <summary>Dòng <c>SY_FrmLstTbl</c> + dòng cấu hình (của Para trước, rồi dòng chung) cho form; null nếu không có tệp mô tả.</summary>
    public static (FrmLstRow Lst, IReadOnlyList<FrmCfgRow> Rows, bool UseDbConfig, IReadOnlyList<FrmFltRow> Filters)? Get(string formName, string? para)
    {
        if (!All.Value.TryGetValue(formName, out var o)) return null;
        var rows = new List<FrmCfgRow>();
        if (o.Para.TryGetValue(para ?? "", out var p)) rows.AddRange(p);
        rows.AddRange(o.Base);
        return (new FrmLstRow(o.Form, o.FormType, o.Caption, null, null, null, null, null, null, null), rows, o.UseDbConfig, o.Filters);
    }

    private static Dictionary<string, Overlay> Load()
    {
        var asm = typeof(FormOverlays).Assembly;
        var result = new Dictionary<string, Overlay>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("WebErp.Overlays.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var o = Parse(doc.RootElement, name);
            result[o.Form] = o;
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
