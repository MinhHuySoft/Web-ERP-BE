using System.Globalization;
using WebErp.Core.Metadata;

namespace WebErp.Erp.Metadata;

/// <summary>Một dòng <c>SY_FrmCfg</c> (cột đã kiểm trên MedData 2026-11-12).</summary>
public sealed record FrmCfgRow(string KeyID, string? SubID, string? KeyValue, string? SubValue);

/// <summary>Các cột <c>SY_FrmLstTbl</c> web dùng (đã kiểm trên MedData 2026-11-12).</summary>
public sealed record FrmLstRow(string FormID, string? FormType, string? CaptionVN, string? TableName, string? PrimaryKey,
    string? FilterEx, string? HideColumnArr, string? SummaryColumnArr, string? TableDetailLeftJoinField, string? TableDetailSelectFrom);

/// <summary>Các cột <c>SY_FrmFltTbl</c> web dùng.</summary>
public sealed record FrmFltRow(string? KeyID, string? ColumnID, string? Caption, int? Type, string? Source, string? ValueColumn,
    string? DisplayColumn, int? Operator, string? WhereString, string? DefaultValue, bool? IsDisable);

/// <summary>Các cột <c>SY_FrmDrdwTbl</c> web dùng (cột kiểm trên MedData 2026-11-13; DB cũ có thể thiếu DisplayMember2/isInvisible).</summary>
public sealed record FrmDrdwRow(string? GridName, string? ColumnID, string? Type, string? Source, string? ValueColumn, string? DisplayColumn,
    string? DisplayMember2, string? ColumnArr, string? ParaArr, string? Caption, bool? IsInvisible);

/// <summary>
/// Dòng metadata → <see cref="FormMeta"/> (tài liệu 10 §4). Không chạm DB. Dòng trùng khóa: lấy dòng đầu; giá trị rỗng = mặc định.
/// </summary>
public static class MetadataCompiler
{
    private static readonly string[] MasterTabs = ["1", "2", "3", "A", "B", "C"];

    public static FormMeta Compile(string formName, string runtimeName, FrmLstRow? lst, IReadOnlyList<FrmCfgRow> cfg, IReadOnlyList<FrmFltRow> filters)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var slotKeys = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var captions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in cfg)
        {
            var value = r.KeyValue ?? "";
            if (value.Length == 0) continue;
            var sub = r.SubID ?? "";
            if (sub.Length == 0) { settings.TryAdd(r.KeyID, value); continue; }
            if (IsSlotId(r.KeyID))
            {
                if (!slotKeys.TryGetValue(r.KeyID, out var d)) slotKeys[r.KeyID] = d = new(StringComparer.OrdinalIgnoreCase);
                d.TryAdd(sub, value);
            }
            else if ((r.KeyID.Length == 2 && r.KeyID[0] is 'F' or 'f' && char.IsAsciiDigit(r.KeyID[1]) || r.KeyID == "C")
                     && string.Equals(r.SubValue, "CV", StringComparison.OrdinalIgnoreCase))
            {
                captions.TryAdd($"{r.KeyID.ToUpperInvariant()}:{sub}", value);
            }
        }

        var slots = new Dictionary<string, SlotMeta>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, keys) in slotKeys)
        {
            if (Slot(id, keys) is { } s) slots[id] = s;
        }
        // LIST/LISTEDIT không có T0: dùng SY_FrmLstTbl (FrmFormList, tài liệu 10 §3.1). EDIT bắt buộc có T0.
        var formType = string.IsNullOrWhiteSpace(lst?.FormType) ? "LIST" : lst!.FormType!.Trim().ToUpperInvariant();
        if (!slots.ContainsKey("T0") && formType != "EDIT" && !string.IsNullOrWhiteSpace(lst?.TableName) && !string.IsNullOrWhiteSpace(lst.PrimaryKey))
        {
            var table = lst.TableName!.Trim();
            var extra = string.IsNullOrWhiteSpace(lst.TableDetailLeftJoinField) ? null : lst.TableDetailLeftJoinField!.Trim();
            slots["T0"] = new SlotMeta("T0", table, lst.PrimaryKey!.Trim(), View: null,
                Select: extra is null ? null : $"{table}.*, {extra}", Expression: null, Joins: [],
                Sort: null, Distinct: false, Top: null, Filter: null, FilterEx: Blank(lst.FilterEx), FilterExCheck: null,
                MasterField: null, DetailField: null, Order: [], Hidden: List(lst.HideColumnArr), HiddenForUser: [], HiddenForManager: [],
                AmountColumns: [], SumColumns: List(lst.SummaryColumnArr), Caption: null)
            {
                FromOverride = Blank(lst.TableDetailSelectFrom),
            };
        }

        return new FormMeta(formName, runtimeName, formType, lst?.CaptionVN?.Trim() ?? formName, slots, settings,
            Filters(filters), Layout(cfg), captions);
    }

    internal static bool IsSlotId(string keyId) => keyId.Length == 2 && FormMeta.SlotIds.Contains(keyId.ToUpperInvariant());

    private static SlotMeta? Slot(string id, Dictionary<string, string> k)
    {
        string? Get(string key) => k.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var table = Get("TN");
        var pk = Get("PK");
        if (table is null || pk is null) return null; // isValid(): có TN và PK

        var source = Get("TV") ?? table;
        var joins = new List<JoinMeta>();
        string[] aliases = ["A", "B", "C", "D"];
        for (var n = 1; n <= 4; n++)
        {
            var jt = Get($"TN{n}");
            var fka = Get($"FKA{n}");
            var fkb = Get($"FKB{n}");
            if (jt is null || fka is null || fkb is null) break; // join n chỉ xét khi join n−1 đủ
            joins.Add(new JoinMeta(jt, Get($"TA{n}") ?? aliases[n - 1], n == 1 ? null : Get($"TM{n}"), fka, fkb,
                Get($"IJ{n}") == "1", Get($"JC{n}"), Get($"SE{n}")));
        }
        return new SlotMeta(id.ToUpperInvariant(), table, pk, Get("TV"), Get("SE"), Get("EX"), joins, Get("SO"), Get("SDD") == "1",
            int.TryParse(Get("TRS"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var top) && top > 0 ? top : null,
            Get("FTR"), Get("FTX"), Get("FTXS"), Get("MUL"), Get("MDL"),
            List(Get("GD2")), List(Get("HDE")), List(Get("HDE2")), List(Get("HDE3")), List(Get("ACP")), List(Get("SUM")), Get("DCP"))
        {
            FilterWeb = Get("FTW"),
            HideOnHideAmount = Get("HHA") == "1",
            Optional = Get("OPT") == "1",
            GridName = Get("GN"),
            FromOverride = Get("FRM"),
            Independent = Get("IND") == "1",
            GroupBy = Get("GCA"),
        };
    }

    /// <summary>Dòng <c>SY_FrmDrdwTbl</c> (đã lọc IsDisable, dòng của tên form chạy trước) → tra cứu; trùng (lưới, cột) lấy dòng đầu.</summary>
    public static IReadOnlyList<DropdownMeta> Dropdowns(IEnumerable<FrmDrdwRow> rows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DropdownMeta>();
        foreach (var r in rows)
        {
            var column = Blank(r.ColumnID);
            if (column is null || !seen.Add($"{r.GridName?.Trim()}|{column}")) continue;
            result.Add(new DropdownMeta(r.GridName?.Trim() ?? "", column, r.Type?.Trim() ?? "", Blank(r.Source), Blank(r.ValueColumn),
                Blank(r.DisplayColumn), Blank(r.DisplayMember2), Blank(r.ColumnArr), Blank(r.ParaArr), Blank(r.Caption), r.IsInvisible == true));
        }
        return result;
    }

    private static List<FilterMeta> Filters(IReadOnlyList<FrmFltRow> rows) =>
        rows.Where(r => r.IsDisable != true && !string.IsNullOrWhiteSpace(r.ColumnID))
            .Select(r => new FilterMeta(r.KeyID?.Trim() ?? "", r.ColumnID!.Trim(), Blank(r.Caption), r.Type ?? 0, Blank(r.Source),
                Blank(r.ValueColumn), Blank(r.DisplayColumn), r.Operator ?? 0, Blank(r.WhereString), Blank(r.DefaultValue)))
            .ToList();

    /// <summary>
    /// <c>LYT{tab}</c> của các tab master 1,2,3,A,B,C (tài liệu 10 §4.7): chuỗi 21 vị trí tách bằng <c>;</c>, trong đó <c>:</c> thay cho <c>;</c>.
    /// Ô trong nhóm (<c>LYT{tab}{Nhóm}</c>) được trải phẳng vào chỗ của nhóm.
    /// </summary>
    private static List<LayoutFieldMeta> Layout(IReadOnlyList<FrmCfgRow> cfg)
    {
        var byKey = cfg.Where(r => r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(r.SubID))
            .GroupBy(r => r.KeyID[3..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.GroupBy(r => r.SubID!, StringComparer.OrdinalIgnoreCase).Select(x => x.First())
                .OrderBy(r => r.SubValue ?? "", StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
        var result = new List<LayoutFieldMeta>();
        foreach (var tab in MasterTabs)
        {
            if (!byKey.TryGetValue(tab, out var rows)) continue;
            Add(tab, tab, rows, 0);
        }
        return result;

        void Add(string tab, string key, List<FrmCfgRow> rows, int depth)
        {
            foreach (var r in rows)
            {
                var item = Item(tab, r);
                if (item is null) continue;
                if (item.ControlType == 23)
                {
                    if (depth < 3 && byKey.TryGetValue(key + r.SubID, out var children)) Add(tab, key + r.SubID, children, depth + 1);
                    continue;
                }
                result.Add(item);
            }
        }
    }

    private static LayoutFieldMeta? Item(string tab, FrmCfgRow r)
    {
        var p = (r.KeyValue ?? "").Split(';');
        string At(int i) => i < p.Length ? p[i] : "";
        int Int(int i, int fallback) => int.TryParse(At(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        if (At(18) == "1") return null; // isDisableOrHidden
        var sizeX = Int(1, 3);
        var span = sizeX is 99 or 0 or > 40 ? 40 : Math.Max(sizeX, 2);
        var caption = At(7).Replace(':', ';').Trim();
        return new LayoutFieldMeta(tab, r.SubID!, r.SubValue ?? "", Int(0, 1), span, Math.Clamp(Int(2, 1), 1, 16), At(3) == "1", At(4) == "1",
            caption.Length == 0 ? null : caption, false, Int(9, 0));
    }

    internal static IReadOnlyList<string> List(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
