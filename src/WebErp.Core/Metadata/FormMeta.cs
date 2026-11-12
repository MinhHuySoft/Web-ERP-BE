namespace WebErp.Core.Metadata;

/// <summary>
/// Một form metadata của ERP (<c>SY_FrmLstTbl</c> + <c>SY_FrmCfg</c>, tài liệu 10 §4), đã biên dịch. Chỉ chứa phần web chỉ đọc
/// cần (M2/M3); các key hook (ESL, EBS…) và nút lệnh cố ý không đọc.
/// </summary>
/// <param name="FormName">Tên form trong menu (<c>SY_Menu.FormName</c>).</param>
/// <param name="RuntimeName"><c>FormName</c> hoặc <c>FormName_Para</c>: khóa của <c>SY_FrmCfg.FID</c> và của quyền.</param>
/// <param name="FormType"><c>LIST</c>, <c>LISTEDIT</c>, <c>LISTEDIT2</c>, <c>EDIT</c>… (rỗng ⇒ <c>LIST</c>).</param>
/// <param name="Settings">Key cấp form (SubID rỗng): <c>SP</c>, <c>SPA</c>, <c>SPA2</c>, <c>SPA3</c>, <c>DFP</c>, <c>T1V</c>, <c>TPV1</c>…</param>
/// <param name="Captions">Đè tiêu đề cột theo form: khóa <c>F0:Cột</c> (T0) / <c>F1:Cột</c> (T1)…, từ <c>SY_FrmCfg</c> KeyID F0..F9, SubValue CV.</param>
public sealed record FormMeta(
    string FormName,
    string RuntimeName,
    string FormType,
    string Caption,
    IReadOnlyDictionary<string, SlotMeta> Slots,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<FilterMeta> Filters,
    IReadOnlyList<LayoutFieldMeta> Layout,
    IReadOnlyDictionary<string, string> Captions)
{
    public bool IsEdit => FormType.Equals("EDIT", StringComparison.OrdinalIgnoreCase);

    public string? Setting(string key) => Settings.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public int SettingInt(string key, int fallback) => int.TryParse(Setting(key), out var v) ? v : fallback;

    /// <summary>Slot bảng chính (danh sách / master).</summary>
    public SlotMeta? Master => Slots.GetValueOrDefault("T0");

    /// <summary>Slot chi tiết hợp lệ T1…T9, TA…TJ theo thứ tự.</summary>
    public IEnumerable<SlotMeta> Details => SlotIds.Skip(1).Select(id => Slots.GetValueOrDefault(id)).OfType<SlotMeta>();

    public static readonly IReadOnlyList<string> SlotIds =
        ["T0", "T1", "T2", "T3", "T4", "T5", "T6", "T7", "T8", "T9", "TA", "TB", "TC", "TD", "TE", "TF", "TG", "TH", "TI", "TJ"];
}

/// <summary>
/// Một slot bảng <c>T0…TJ</c> (tài liệu 10 §4.3, §4.5). Hợp lệ khi có TN và PK.
/// SQL đọc: <c>Select [Distinct] [top TRS] {SE | Source.*} [,EX] [,SE1..4] From Source [joins] Where (FTR) And (FTX) … Order By SO</c>.
/// </summary>
public sealed record SlotMeta(
    string Id,
    string Table,
    string PrimaryKey,
    string? View,
    string? Select,
    string? Expression,
    IReadOnlyList<JoinMeta> Joins,
    string? Sort,
    bool Distinct,
    int? Top,
    string? Filter,
    string? FilterEx,
    string? FilterExCheck,
    string? MasterField,
    string? DetailField,
    IReadOnlyList<string> Order,
    IReadOnlyList<string> Hidden,
    IReadOnlyList<string> HiddenForUser,
    IReadOnlyList<string> HiddenForManager,
    IReadOnlyList<string> AmountColumns,
    IReadOnlyList<string> SumColumns,
    string? Caption)
{
    /// <summary>Bảng/view đọc dữ liệu: TV nếu có, không thì TN.</summary>
    public string Source => string.IsNullOrEmpty(View) ? Table : View;

    /// <summary>Mệnh đề FROM thay thế (form LIST không có T0: <c>SY_FrmLstTbl.TableDetailSelectFrom</c>).</summary>
    public string? FromOverride { get; init; }
}

/// <summary><c>Left|Inner join Table Alias on {Left}.LeftKey = Alias.RightKey Extra</c>; <see cref="Select"/> thêm vào danh sách cột.</summary>
public sealed record JoinMeta(string Table, string Alias, string? LeftTable, string LeftKey, string RightKey, bool Inner, string? Extra, string? Select);

/// <summary>Một ô lọc trên toolbar (<c>SY_FrmFltTbl</c>, tài liệu 11 §3.1).</summary>
/// <param name="Type">0 text, 1 ngày, 2 số, 3 combo, 5 checkbox… (<c>SearchControlTypes</c>).</param>
/// <param name="Operator">0 bằng, 1 like, 2 &gt;, 3 &gt;=, 4 &lt;, 5 &lt;=, 6 &lt;&gt; (<c>OperatorEnums</c>).</param>
public sealed record FilterMeta(string Key, string Column, string? Caption, int Type, string? Source, string? ValueColumn,
    string? DisplayColumn, int Operator, string? WhereString, string? DefaultValue);

/// <summary>Một ô của bố cục chứng từ (<c>SY_FrmCfg</c> KeyID <c>LYT{tab}</c>, tài liệu 10 §5).</summary>
/// <param name="ControlType">1 text, 2 combo, 3 combo nhiều chọn, 4 ngày, 5 số, 6 checkbox, 22/24/26 nút, 23 nhóm, 28 nhãn…</param>
/// <param name="VisiblePermiss">0 mọi người, 7 Manager, 9 Admin.</param>
public sealed record LayoutFieldMeta(string Tab, string Field, string Order, int ControlType, int Span, int Rows, bool RowStart,
    bool ReadOnly, string? Caption, bool Hidden, int VisiblePermiss);
