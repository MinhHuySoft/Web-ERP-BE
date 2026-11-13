namespace WebErp.Core.Metadata;

/// <summary>
/// Một form metadata của ERP (<c>SY_FrmLstTbl</c> + <c>SY_FrmCfg</c>, tài liệu 10 §4), đã biên dịch. Chỉ chứa phần web chỉ đọc
/// cần (M2/M3); các key hook (ESL, EBS…) và nút lệnh cố ý không đọc.
/// </summary>
/// <param name="FormName">Tên form trong menu (<c>SY_Menu.FormName</c>).</param>
/// <param name="RuntimeName"><c>FormName</c> hoặc <c>FormName_Para</c>: khóa của <c>SY_FrmCfg.FID</c> và của quyền.</param>
/// <param name="FormType"><c>LIST</c>, <c>LISTEDIT</c>, <c>LISTEDIT2</c>, <c>EDIT</c>… (rỗng ⇒ <c>LIST</c>).</param>
/// <param name="Settings">Key cấp form (SubID rỗng): <c>SP</c>, <c>SPA</c>, <c>SPA2</c>, <c>SPA3</c>, <c>DFP</c>, <c>T1V</c>, <c>TPV1</c>…</param>
/// <param name="Captions">Đè tiêu đề cột theo form: khóa <c>F0:Cột</c> (T0) / <c>F1:Cột</c> (T1)…, từ <c>SY_FrmCfg</c> KeyID F0..F9, SubValue CV;
/// khóa <c>C:Cột</c> = nhãn ô chứng từ của form (<c>SY_FrmCtrTbl</c>, ParentCtlName …;Fld&lt;Cột&gt;).</param>
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

    /// <summary>Cấu hình tra cứu / danh sách chọn (<c>SY_FrmDrdwTbl</c>, tài liệu 11 §2.1), dòng của tên form chạy đứng trước.</summary>
    public IReadOnlyList<DropdownMeta> Dropdowns { get; init; } = [];

    /// <summary>Tra cứu của ô trên chứng từ: <c>GridName</c> rỗng, <c>ColumnID</c> = <c>Fld{Cột}</c> hoặc <c>{Cột}</c> (FormControler.SetEditorArr).</summary>
    public DropdownMeta? MasterDropdown(string field) => Dropdowns.FirstOrDefault(d => d.Grid.Length == 0
        && (d.Column.Equals(field, StringComparison.OrdinalIgnoreCase) || d.Column.Equals("Fld" + field, StringComparison.OrdinalIgnoreCase)));

    /// <summary>Tra cứu của một cột lưới của slot (FormControler.SetDropdownFromConfig: lọc theo tên lưới).</summary>
    public DropdownMeta? GridDropdown(SlotMeta slot, string field)
    {
        var grids = GridNames(slot);
        return Dropdowns.FirstOrDefault(d => d.Grid.Length > 0 && d.Column.Equals(field, StringComparison.OrdinalIgnoreCase)
            && grids.Contains(d.Grid, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Tên lưới WinForms của slot: key web <c>GN</c> nếu có; không thì theo form metadata (FrmFormList/FrmFormListEdit: <c>grdList</c>,
    /// FrmMasterDetail/FrmMasterEditTop: <c>grdListDoc</c>; T1 <c>grdChitiet</c>, T2… <c>grdChitiet2</c>…<c>grdChitiet19</c>).
    /// </summary>
    public static IReadOnlyList<string> GridNames(SlotMeta slot)
    {
        if (slot.GridName is { Length: > 0 } gn) return gn.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var i = SlotIds.ToList().IndexOf(slot.Id.ToUpperInvariant());
        return i switch { 0 => ["grdList", "grdListDoc"], 1 => ["grdChitiet"], > 1 => [$"grdChitiet{i}"], _ => [] };
    }

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

    /// <summary>
    /// Điều kiện luôn áp (key web <c>FTW</c> của tệp mô tả form viết tay), vd <c>isBank = 1</c> theo Para. Khác FTX: không bị cấu hình
    /// <c>SY_FrmCfg</c> của khách đè mất, vì form viết tay của WinForms tự thêm điều kiện này ngoài cấu hình.
    /// </summary>
    public string? FilterWeb { get; init; }

    /// <summary>Key web <c>HHA</c> = 1: ẩn cả tab khi người dùng bị ẩn tiền (như lưới "Định khoản" của form viết tay).</summary>
    public bool HideOnHideAmount { get; init; }

    /// <summary>Key web <c>OPT</c> = 1: bảng/view không có trong DB của khách thì bỏ qua tab (vd bảng lịch sử trạng thái).</summary>
    public bool Optional { get; init; }

    /// <summary>Key web <c>GN</c>: tên lưới WinForms của slot (form viết tay đặt tên riêng, vd <c>grdHoaDon</c>), để khớp <c>SY_FrmDrdwTbl.GridName</c>.</summary>
    public string? GridName { get; init; }
}

/// <summary>
/// Một dòng <c>SY_FrmDrdwTbl</c> (tài liệu 11 §2.1). Web chỉ đọc nên dùng để: hiện tên thay mã (cột/ô có <c>DisplayColumn</c> khác
/// <c>ValueColumn</c>, hoặc <c>DisplayMember2</c>), tra cứu F4, đổi tiêu đề (<c>Caption</c>) và ẩn cột (<c>isInvisible</c>).
/// </summary>
/// <param name="Grid">Tên lưới WinForms; rỗng = ô trên chứng từ.</param>
/// <param name="Type">Rỗng/<c>Combo</c>/<c>Dropdown</c>, <c>ValueList</c>/<c>ValueLstCb</c>/<c>LangList</c> (danh sách tĩnh trong Source),
/// <c>DropSelect</c>, <c>DropCheck</c>, <c>ColorSelct</c>…</param>
/// <param name="ParaArr">Tham số <c>{0}</c>, <c>{1}</c>… của Source: <c>Master.Cột</c> hoặc cột cùng dòng, cách nhau <c>;</c>.</param>
public sealed record DropdownMeta(string Grid, string Column, string Type, string? Source, string? ValueColumn, string? DisplayColumn,
    string? DisplayMember2, string? ColumnArr, string? ParaArr, string? Caption, bool Invisible)
{
    public bool IsValueList => Type.Equals("ValueList", StringComparison.OrdinalIgnoreCase) || Type.Equals("ValueLstCb", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("LangList", StringComparison.OrdinalIgnoreCase);

    /// <summary>Danh sách chọn thường (combo/dropdown/danh sách tĩnh): ô hiện chữ của <see cref="DisplayColumn"/>. DropSelect, DropCheck,
    /// chọn màu/ảnh/tệp giữ nguyên giá trị.</summary>
    public bool IsList => Type.Length == 0 || IsValueList || Type.Equals("Combo", StringComparison.OrdinalIgnoreCase)
        || Type.Equals("Dropdown", StringComparison.OrdinalIgnoreCase);

    /// <summary>Có tra cứu F4 được (có cột giá trị và nguồn): danh sách thường hoặc form chọn (DropSelect).</summary>
    public bool CanLookup => ValueColumn is not null && Source is not null
        && (IsList || Type.Equals("DropSelect", StringComparison.OrdinalIgnoreCase));

    /// <summary>Cột chữ thay cho mã trên lưới: <see cref="DisplayColumn"/> khi khác <see cref="ValueColumn"/>.</summary>
    public string? NameColumn => IsList && DisplayColumn is not null && ValueColumn is not null
        && !DisplayColumn.Equals(ValueColumn, StringComparison.OrdinalIgnoreCase) ? DisplayColumn : null;

    /// <summary>Chữ kèm mã trên ô chứng từ: tên thay mã, không thì cột hiển thị thứ hai của ComboX (<c>DisplayMember2</c>).</summary>
    public string? TextColumn => NameColumn ?? (IsList ? DisplayMember2 : null);

    /// <summary>Tên các tham số theo <see cref="ParaArr"/> (bỏ tiền tố <c>Master.</c>).</summary>
    public IReadOnlyList<(string Field, bool Master)> Paras => string.IsNullOrWhiteSpace(ParaArr) ? []
        : ParaArr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.StartsWith("Master.", StringComparison.OrdinalIgnoreCase) ? (p[7..], true) : (p, false)).ToList();
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
