using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebErp.Erp.Layout;

/// <summary>Một dòng <c>SY_FrmCfg</c> của form (đủ cột để ghi lại nguyên trạng khi khôi phục; kiểm trên MedData 2026-11-13).</summary>
public sealed record CfgRow(string UserAutoID, string KeyID, string? SubID, string? KeyValue, string? SubValue, DateTime? VDate = null, string? PFID = null);

/// <summary>Một ô / control của bố cục: một dòng <c>LYT{tab}{nhóm…}</c>. <see cref="Group"/> = chuỗi tên nhóm cha nối nhau ("" = ngoài nhóm).</summary>
/// <param name="Props">Thuộc tính riêng <c>LYS</c> của ô (mã → giá trị). null = không gửi (giữ nguyên thuộc tính đang có).</param>
/// <param name="From">Tên cũ khi ô được đổi tên trong lần sửa này (giữ UserAutoID và thuộc tính LYS của ô).</param>
public sealed record LayoutItem(string Tab, string Group, string Name, string Cfg, IReadOnlyDictionary<string, string>? Props = null, string? From = null)
{
    public string Zone => Tab + Group;
}

/// <summary>Tiêu đề (<c>T{n}</c> / <c>DCP</c>) và quyền xem (<c>TPV{n}</c>: 0 mọi người, 1 Quản lý, 4 Admin) của trang chi tiết n.</summary>
public sealed record PageSetting(string? Caption, string? Visible);

/// <summary>
/// Mô hình bố cục của <c>LayoutSettingFrm</c> (mã dịch ngược Tools.Layout: LayoutConfigCxtl, LayoutX.SaveConfig, PropertyX), không chạm DB:
/// <list type="bullet">
/// <item>Tab: <c>1,2,3,A,B,C</c> (master) và <c>5…13</c> (trang chi tiết 1…9). Ô trong nhóm (ControlType 23) nằm ở
/// <c>LYT{tab}{Nhóm}</c>, nhóm lồng nối tiếp tên (<c>LYT1GrpBox1GrpBox2</c>).</item>
/// <item>Thứ tự: <c>SubValue</c> = số thứ tự 1..n đệm 3 chữ số ("001"); thiếu = 999; không phải số thì WinForms bỏ dòng.</item>
/// <item>Thuộc tính <c>LYS{vùng}</c> (SubID = tên ô, SubValue = mã) đi theo ô khi ô đổi vùng / đổi tên; bỏ ô thì bỏ thuộc tính của ô.
/// Web chỉ cho sửa các mã không chứa SQL / lệnh (<see cref="PropEditable"/>); mã khác giữ nguyên.</item>
/// <item>Tiêu đề tab master: <c>T1V…TCV</c> (SubID rỗng). Trang chi tiết: <c>T{n}</c> / <c>DCP</c>, quyền xem <c>TPV1…5</c>.</item>
/// </list>
/// Khác WinForms (cố ý, an toàn hơn): chỉ ghi các dòng bố cục, không xóa / ghi lại mọi dòng của FID; dòng LYT / LYS mồ côi (không thuộc
/// vùng nào đọc được) giữ nguyên.
/// </summary>
public static partial class LayoutModel
{
    public const string Schema = "erp-form-layout/1";
    public const int MaxItems = 2000;
    public static readonly string[] Tabs = ["1", "2", "3", "A", "B", "C", "5", "6", "7", "8", "9", "10", "11", "12", "13"];
    public static readonly string[] CaptionKeys = ["T1V", "T2V", "T3V", "TAV", "TBV", "TCV"];
    private static readonly HashSet<int> ControlTypes = [1, 2, 3, 4, 5, 6, 7, 20, 22, 23, 24, 26, 28, 30];
    private static readonly HashSet<string> Visibilities = ["0", "1", "4"];

    /// <summary>
    /// Mã thuộc tính <c>LYS</c> web cho sửa theo loại control (<c>PropertyX</c> của WinForms). Không có mã mang SQL / lệnh
    /// (<c>LBS</c> khóa bằng SQL, <c>VLQ</c> nguồn SQL của nút chọn, mã lệnh của user control C1…, CS4…): những mã đó chỉ sửa trên ERP,
    /// vì trình vẽ của ERP cần mật khẩu chế độ quản trị còn web chỉ cần quyền Admin trên form.
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string[]> EditableProps = new Dictionary<int, string[]>
    {
        [0] = ["FH", "FW", "PT", "PL", "PV1", "PV3", "PV4", "PV5", "PV6", "LBEX", "LBSP"],
        [1] = ["PC"], [30] = ["PC"],
        [4] = ["MVR", "MV", "MXR", "MX"],
        [5] = ["MVR", "MV", "MXR", "MX", "SCB", "SUD", "SUV1", "SUV2"],
        [20] = ["SFB", "SFT", "VLS", "VLC", "VCW"],
        [23] = ["SFT"],
        [24] = ["BI", "BC"], [26] = ["BI", "BC"],
        [28] = ["B0", "B1", "B2", "FS", "FB", "FN", "FC", "F1", "B4", "BC", "TA"],
        [22] = ["BT", "LT"],
    };

    /// <summary>Mã <paramref name="code"/> sửa được trên web với control loại <paramref name="controlType"/>.</summary>
    public static bool PropEditable(int controlType, string code) =>
        EditableProps[0].Contains(code, StringComparer.OrdinalIgnoreCase)
        || EditableProps.TryGetValue(controlType, out var own) && own.Contains(code, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lớp user control WinForms tạo từ tên ô (<c>LayoutX</c>: phần trước "_" đầu tiên; ngắn hơn 4 ký tự thì ghép hai phần đầu).
    /// Cùng một mã LYS mang nghĩa khác nhau theo lớp (vd <c>C4</c> là câu lệnh), nên không cho đổi tên user control sang lớp khác.
    /// </summary>
    public static string UcClass(string name)
    {
        var p = name.Split('_');
        return p[0].Length < 4 && p.Length > 1 ? p[0] + "_" + p[1] : p[0];
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,49}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9]{1,10}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("^T([1-9])$", RegexOptions.IgnoreCase)]
    private static partial Regex PageKey();

    [GeneratedRegex("^TPV([1-5])$", RegexOptions.IgnoreCase)]
    private static partial Regex VisibleKey();

    /// <summary>Dòng thuộc phạm vi trình vẽ (để chụp ảnh / khôi phục): LYT*, LYS*, T?V, T{n}/DCP, TPV{n}.</summary>
    public static bool IsLayoutRow(CfgRow r) =>
        r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) || r.KeyID.StartsWith("LYS", StringComparison.OrdinalIgnoreCase)
        || string.IsNullOrEmpty(r.SubID) && (CaptionKeys.Contains(r.KeyID, StringComparer.OrdinalIgnoreCase) || VisibleKey().IsMatch(r.KeyID))
        || PageKey().IsMatch(r.KeyID) && string.Equals(r.SubID, "DCP", StringComparison.OrdinalIgnoreCase);

    public static int ControlType(string? cfg) =>
        int.TryParse((cfg ?? "").Split(';')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;

    /// <summary>Đọc bố cục: duyệt từ các tab, đi vào nhóm. Trả ô (kèm thuộc tính LYS) theo thứ tự và các vùng (phần sau LYT) đã đọc.</summary>
    public static (List<LayoutItem> Items, HashSet<string> Zones) Parse(IEnumerable<CfgRow> rows)
    {
        var list0 = rows as IReadOnlyCollection<CfgRow> ?? rows.ToList();
        var lyt = list0.Where(r => r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(r.SubID)
                                   && !string.IsNullOrWhiteSpace(r.KeyValue) && Order(r) >= 0)
            .GroupBy(r => r.KeyID[3..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.GroupBy(x => x.SubID!, StringComparer.OrdinalIgnoreCase).Select(x => x.First())
                .OrderBy(Order).ThenBy(x => x.UserAutoID, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
        var lys = list0.Where(r => r.KeyID.StartsWith("LYS", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(r.SubID)
                                   && !string.IsNullOrEmpty(r.SubValue) && !string.IsNullOrEmpty(r.KeyValue))
            .GroupBy(r => r.KeyID[3..] + "|" + r.SubID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<string, string>)g.GroupBy(x => x.SubValue!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.First().KeyValue!, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var items = new List<LayoutItem>();
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in Tabs) Walk(tab, tab, "", 0);
        return (items, zones);

        void Walk(string tab, string zone, string group, int depth)
        {
            if (!lyt.TryGetValue(zone, out var list) || !zones.Add(zone)) return;
            foreach (var r in list)
            {
                items.Add(new LayoutItem(tab, group, r.SubID!, r.KeyValue!.Trim(),
                    lys.GetValueOrDefault(zone + "|" + r.SubID) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
                if (ControlType(r.KeyValue) == 23 && depth < 8) Walk(tab, zone + r.SubID, group + r.SubID, depth + 1);
            }
        }
    }

    /// <summary>Thứ tự trong vùng: rỗng = 999 (cuối); không phải số = -1 (WinForms bỏ dòng khi đọc).</summary>
    private static int Order(CfgRow r) =>
        string.IsNullOrWhiteSpace(r.SubValue) ? 999 : int.TryParse(r.SubValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

    /// <summary>Tiêu đề và quyền xem của các trang chi tiết đang có (n → giá trị).</summary>
    public static (Dictionary<int, string> Captions, Dictionary<int, string> Visible) Pages(IEnumerable<CfgRow> rows)
    {
        var caps = new Dictionary<int, string>();
        var vis = new Dictionary<int, string>();
        foreach (var r in rows)
        {
            if (PageKey().Match(r.KeyID) is { Success: true } m && string.Equals(r.SubID, "DCP", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.KeyValue))
                caps.TryAdd(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), r.KeyValue.Trim());
            else if (VisibleKey().Match(r.KeyID) is { Success: true } v && string.IsNullOrEmpty(r.SubID) && !string.IsNullOrWhiteSpace(r.KeyValue))
                vis.TryAdd(int.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture), r.KeyValue.Trim());
        }
        return (caps, vis);
    }

    /// <summary>Dấu phiên bản của bố cục hiện có (phát hiện người khác / WinForms sửa trong lúc đang vẽ).</summary>
    public static string Revision(IEnumerable<CfgRow> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows.Where(IsLayoutRow).OrderBy(r => r.KeyID.ToUpperInvariant(), StringComparer.Ordinal)
                     .ThenBy(r => (r.SubID ?? "").ToUpperInvariant(), StringComparer.Ordinal).ThenBy(r => r.SubValue ?? "", StringComparer.Ordinal)
                     .ThenBy(r => r.UserAutoID, StringComparer.Ordinal))
            sb.Append(r.KeyID).Append('\u001f').Append(r.SubID).Append('\u001f').Append(r.SubValue).Append('\u001f').Append(r.KeyValue).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Bố cục FE gửi lên (đã kiểm). Lỗi thì <c>Errors</c> có nội dung.</summary>
    public sealed record Input(string? Form, string? Revision, List<LayoutItem> Items, Dictionary<string, string>? Captions, List<string> Errors,
        Dictionary<int, PageSetting>? Pages = null);

    /// <summary>Đọc và kiểm JSON <c>erp-form-layout/1</c> (không tin FE): tên, tab, nhóm, chuỗi cấu hình, thuộc tính, trang, độ dài cột DB.</summary>
    public static Input ReadInput(string json)
    {
        var errors = new List<string>();
        var items = new List<LayoutItem>();
        if (json.Length > 2_000_000) return new Input(null, null, items, null, ["Bố cục quá lớn."]);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException) { return new Input(null, null, items, null, ["Bố cục không phải JSON hợp lệ."]); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "$schema") != Schema)
                return new Input(null, null, items, null, ["Bố cục không đúng định dạng erp-form-layout/1."]);
            Dictionary<string, string>? captions = null;
            if (root.TryGetProperty("captions", out var caps) && caps.ValueKind == JsonValueKind.Object)
            {
                captions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in caps.EnumerateObject())
                {
                    var key = CaptionKeys.FirstOrDefault(k => k.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
                    if (key is null) { errors.Add($"Tiêu đề tab không hợp lệ: {Short(p.Name)}."); continue; }
                    var v = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()!.Trim() : "";
                    if (v.Length > 200) errors.Add($"Tiêu đề tab {key} quá dài (tối đa 200 ký tự).");
                    captions[key] = v;
                }
            }
            Dictionary<int, PageSetting>? pages = null;
            if (root.TryGetProperty("pages", out var pg) && pg.ValueKind == JsonValueKind.Object)
            {
                pages = [];
                foreach (var p in pg.EnumerateObject())
                {
                    if (!int.TryParse(p.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 9 || p.Value.ValueKind != JsonValueKind.Object)
                    { errors.Add($"Trang chi tiết không hợp lệ: {Short(p.Name)}."); continue; }
                    var cap = Str(p.Value, "caption")?.Trim();
                    var vis = Str(p.Value, "visible")?.Trim();
                    if (cap is { Length: > 200 }) errors.Add($"Tiêu đề trang chi tiết {n} quá dài (tối đa 200 ký tự).");
                    if (vis is { Length: > 0 } && (n > 5 || !Visibilities.Contains(vis))) errors.Add($"Quyền xem trang chi tiết {n} không hợp lệ.");
                    pages[n] = new PageSetting(cap, vis);
                }
            }
            if (!root.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new Input(Str(root, "form"), Str(root, "revision"), items, captions, [.. errors, "Bố cục thiếu danh sách ô."], pages);
            if (arr.GetArrayLength() > MaxItems) return new Input(null, null, items, null, [$"Bố cục có quá nhiều ô (tối đa {MaxItems})."]);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var froms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in arr.EnumerateArray())
            {
                var tab = Tabs.FirstOrDefault(t => t.Equals(Str(x, "tab"), StringComparison.OrdinalIgnoreCase));
                var group = Str(x, "group") ?? "";
                var name = Str(x, "name") ?? "";
                var from = Str(x, "from");
                var cfg = (Str(x, "cfg") ?? "").Trim();
                if (tab is null) { errors.Add($"Ô {Short(name)}: tab không hợp lệ."); continue; }
                if (!NamePattern().IsMatch(name)) { errors.Add($"Tên ô không hợp lệ: \"{Short(name)}\"."); continue; }
                if (!names.Add(name)) { errors.Add($"Ô {name} có hai lần trong bố cục."); continue; }
                if (from is not null && (!NamePattern().IsMatch(from) || !froms.Add(from))) { errors.Add($"Ô {name}: tên cũ không hợp lệ."); continue; }
                if (group.Length > 0 && !Regex.IsMatch(group, "^[A-Za-z_][A-Za-z0-9_]*$")) { errors.Add($"Ô {name}: nhóm không hợp lệ."); continue; }
                if (3 + tab.Length + group.Length > 50) { errors.Add($"Ô {name}: tên nhóm lồng quá dài (KeyID tối đa 50 ký tự)."); continue; }
                var p = cfg.Split(';');
                if (cfg.Length is 0 or > 4000 || p.Length > 40) { errors.Add($"Ô {name}: chuỗi cấu hình không hợp lệ."); continue; }
                if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ct) || !ControlTypes.Contains(ct))
                { errors.Add($"Ô {name}: loại control {Short(p[0])} không hợp lệ."); continue; }
                if (p.Length > 1 && p[1].Length > 0 && (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sx) || sx is < 0 or > 40 and not 99))
                { errors.Add($"Ô {name}: độ rộng {Short(p[1])} không hợp lệ (0–40 hoặc 99)."); continue; }
                Dictionary<string, string>? props = null;
                if (x.TryGetProperty("props", out var pr) && pr.ValueKind == JsonValueKind.Object)
                {
                    props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var q in pr.EnumerateObject())
                    {
                        var v = q.Value.ValueKind == JsonValueKind.String ? q.Value.GetString()! : q.Value.ValueKind is JsonValueKind.Null ? "" : null;
                        if (!CodePattern().IsMatch(q.Name) || v is null || v.Length > 1000)
                        { errors.Add($"Ô {name}: thuộc tính {Short(q.Name)} không hợp lệ."); continue; }
                        props[q.Name] = v;
                    }
                    if (props.Count > 60) errors.Add($"Ô {name}: quá nhiều thuộc tính.");
                }
                items.Add(new LayoutItem(tab, group, name, cfg, props, from));
            }
            // Nhóm của mỗi ô phải là một ô nhóm (23) cùng tab, có vùng = nhóm cha + tên.
            var zones = new HashSet<string>(items.Where(i => ControlType(i.Cfg) == 23).Select(i => i.Tab + "|" + i.Group + i.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var i in items.Where(i => i.Group.Length > 0 && !zones.Contains(i.Tab + "|" + i.Group)))
                errors.Add($"Ô {i.Name} thuộc nhóm {i.Group} không có trong tab.");
            return new Input(Str(root, "form"), Str(root, "revision"), items, captions, errors, pages);
        }
    }

    /// <summary>Kế hoạch ghi: xóa / sửa / thêm dòng (theo UserAutoID). Dòng sửa giữ UserAutoID cũ như WinForms.</summary>
    public sealed record Plan(List<CfgRow> Delete, List<CfgRow> Update, List<CfgRow> Insert, List<string>? Errors = null)
    {
        public int Count => Delete.Count + Update.Count + Insert.Count;
    }

    /// <summary>
    /// Dòng cần ghi để bố cục thành <paramref name="items"/> (+ tiêu đề tab, trang chi tiết). Chỉ đụng: LYT của các vùng đọc được và của
    /// vùng mới, LYS của ô (đổi vùng / tên theo ô, sửa mã cho phép, bỏ theo ô), T?V có trong <paramref name="captions"/>, DCP / TPV có
    /// trong <paramref name="pages"/>. Sửa mã thuộc tính không cho phép ⇒ lỗi, không ghi gì.
    /// </summary>
    public static Plan Diff(IReadOnlyList<CfgRow> current, IReadOnlyList<LayoutItem> items, IReadOnlyDictionary<string, string>? captions, Func<string> newId,
        IReadOnlyDictionary<int, PageSetting>? pages = null)
    {
        var errors = new List<string>();
        var (oldItems, oldZones) = Parse(current);
        var oldZoneOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var oldOf = new Dictionary<string, LayoutItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in oldItems) { oldZoneOf.TryAdd(i.Name, i.Zone); oldOf.TryAdd(i.Name, i); }
        // Sửa được một mã khi mã đó được phép ở loại control mới VÀ ở loại cũ (nếu ô đã có): chặn lách bằng cách đổi loại control
        // để sửa một mã mà loại kia dùng làm câu lệnh (vd F1 của ô quét mã), rồi đổi loại lại.
        bool CanEdit(LayoutItem item, string code)
        {
            var nt = ControlType(item.Cfg);
            return PropEditable(nt, code) && (!oldOf.TryGetValue(item.From ?? item.Name, out var o) || PropEditable(ControlType(o.Cfg), code));
        }
        foreach (var item in items)
        {
            if (!oldOf.TryGetValue(item.From ?? item.Name, out var o)) continue;
            int ot = ControlType(o.Cfg), nt = ControlType(item.Cfg);
            if (item.From is not null && (ot == 22 || nt == 22) && !UcClass(item.From).Equals(UcClass(item.Name), StringComparison.OrdinalIgnoreCase)
                && o.Props is { Count: > 0 })
                errors.Add($"Ô {item.From}: không đổi tên user control sang lớp khác ({UcClass(item.Name)}) khi ô có thuộc tính riêng — sửa trên ERP.");
            if (ot != nt)
                foreach (var code in (o.Props ?? new Dictionary<string, string>()).Keys.Where(k => !(PropEditable(ot, k) && PropEditable(nt, k))))
                    errors.Add($"Ô {item.Name}: không đổi loại control khi ô có thuộc tính {code} (chỉ sửa trên ERP).");
        }
        // Tên cũ → ô mới: ô đổi tên mang From; ô không đổi tên giữ tên.
        var byOld = new Dictionary<string, LayoutItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in items.Where(i => i.From is not null)) byOld.TryAdd(i.From!, i);
        foreach (var i in items.Where(i => i.From is null)) byOld.TryAdd(i.Name, i);
        string OldName(LayoutItem i) => i.From ?? i.Name;

        var managed = current.Where(r =>
            // Dòng LYT có thứ tự không phải số: WinForms bỏ khi đọc, web để nguyên (không đoán).
            r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) && oldZones.Contains(r.KeyID[3..]) && Order(r) >= 0
            || r.KeyID.StartsWith("LYS", StringComparison.OrdinalIgnoreCase) && oldZones.Contains(r.KeyID[3..]) && r.SubID is { } s
               && oldZoneOf.TryGetValue(s, out var z) && z.Equals(r.KeyID[3..], StringComparison.OrdinalIgnoreCase)
            || captions is not null && string.IsNullOrEmpty(r.SubID) && captions.ContainsKey(r.KeyID)
            || pages is not null && PageKey().Match(r.KeyID) is { Success: true } m && string.Equals(r.SubID, "DCP", StringComparison.OrdinalIgnoreCase)
               && pages.TryGetValue(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), out var pc) && pc.Caption is not null
            || pages is not null && VisibleKey().Match(r.KeyID) is { Success: true } v && string.IsNullOrEmpty(r.SubID)
               && pages.TryGetValue(int.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture), out var pv) && pv.Visible is not null).ToList();
        var pool = managed.ToList();
        CfgRow? Take(Func<CfgRow, bool> match)
        {
            var hit = pool.FirstOrDefault(match);
            if (hit is not null) pool.Remove(hit);
            return hit;
        }

        var desired = new List<(CfgRow Row, bool New)>();
        // LYT: thứ tự 1..n trong từng vùng.
        foreach (var zone in items.GroupBy(i => i.Zone, StringComparer.OrdinalIgnoreCase))
        {
            var n = 0;
            foreach (var i in zone)
            {
                var key = "LYT" + i.Zone;
                var sub = (++n).ToString("000", CultureInfo.InvariantCulture);
                var was = OldName(i);
                var old = Take(r => r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) && was.Equals(r.SubID, StringComparison.OrdinalIgnoreCase)
                                    && r.KeyID.Equals(key, StringComparison.OrdinalIgnoreCase))
                          ?? Take(r => r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) && was.Equals(r.SubID, StringComparison.OrdinalIgnoreCase));
                desired.Add(old is null
                    ? (new CfgRow(newId(), key, i.Name, i.Cfg, sub), true)
                    : (old with { KeyID = key, SubID = i.Name, KeyValue = i.Cfg, SubValue = sub }, false));
            }
        }
        // LYS: thuộc tính đi theo ô (vùng mới, tên mới); mã cho phép lấy giá trị FE gửi, mã khác giữ nguyên.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in managed.Where(r => r.KeyID.StartsWith("LYS", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (!byOld.TryGetValue(r.SubID!, out var item)) continue; // ô đã bỏ: dòng còn trong pool ⇒ xóa
            pool.Remove(r);
            var code = (r.SubValue ?? "").Trim();
            if (code.Length == 0 || !seen.Add(item.Name + "|" + code)) { desired.Add((r, false)); continue; } // dòng lạ / trùng: để nguyên
            var value = r.KeyValue ?? "";
            if (item.Props is not null && CanEdit(item, code))
            {
                value = item.Props.TryGetValue(code, out var nv) ? nv.Trim() : "";
                if (value.Length == 0) { pool.Add(r); continue; } // để trống = về mặc định ⇒ xóa dòng (như PropertyX)
            }
            else if (item.Props is not null && item.Props.TryGetValue(code, out var nv2) && nv2.Trim() != value.Trim())
                errors.Add($"Ô {item.Name}: thuộc tính {code} chỉ sửa được trên ERP (có SQL / lệnh).");
            desired.Add((r with { KeyID = "LYS" + item.Zone, SubID = item.Name, KeyValue = value }, false));
        }
        foreach (var item in items.Where(i => i.Props is not null))
        {
            foreach (var (code, raw) in item.Props!)
            {
                var value = raw.Trim();
                if (value.Length == 0 || seen.Contains(item.Name + "|" + code)) continue;
                if (!CanEdit(item, code)) { errors.Add($"Ô {item.Name}: thuộc tính {code} chỉ sửa được trên ERP (có SQL / lệnh)."); continue; }
                seen.Add(item.Name + "|" + code);
                desired.Add((new CfgRow(newId(), "LYS" + item.Zone, item.Name, value, code.ToUpperInvariant()), true));
            }
        }
        // Tiêu đề tab master.
        foreach (var (key, value) in captions ?? new Dictionary<string, string>())
        {
            if (value.Length == 0) continue;
            var old = Take(r => r.KeyID.Equals(key, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(r.SubID));
            desired.Add(old is null ? (new CfgRow(newId(), key, "", value, ""), true) : (old with { KeyValue = value }, false));
        }
        // Trang chi tiết: tiêu đề (T{n} / DCP) và quyền xem (TPV{n}, 0 = mặc định ⇒ không có dòng).
        foreach (var (n, p) in pages ?? new Dictionary<int, PageSetting>())
        {
            if (p.Caption is { Length: > 0 } cap)
            {
                var old = Take(r => r.KeyID.Equals($"T{n}", StringComparison.OrdinalIgnoreCase) && string.Equals(r.SubID, "DCP", StringComparison.OrdinalIgnoreCase));
                desired.Add(old is null ? (new CfgRow(newId(), $"T{n}", "DCP", cap, null), true) : (old with { KeyValue = cap }, false));
            }
            if (p.Visible is { Length: > 0 } vis && vis != "0")
            {
                var old = Take(r => r.KeyID.Equals($"TPV{n}", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(r.SubID));
                desired.Add(old is null ? (new CfgRow(newId(), $"TPV{n}", "", vis, ""), true) : (old with { KeyValue = vis }, false));
            }
        }

        var byId = managed.ToDictionary(r => r.UserAutoID, StringComparer.Ordinal);
        var update = desired.Where(d => !d.New && byId.TryGetValue(d.Row.UserAutoID, out var o) && !Same(o, d.Row)).Select(d => d.Row).ToList();
        return new Plan(pool, update, desired.Where(d => d.New).Select(d => d.Row).ToList(), errors.Count > 0 ? errors.Distinct().Take(20).ToList() : null);
    }

    private static bool Same(CfgRow a, CfgRow b) =>
        string.Equals(a.KeyID, b.KeyID, StringComparison.Ordinal) && string.Equals(a.SubID ?? "", b.SubID ?? "", StringComparison.Ordinal)
        && string.Equals(a.KeyValue ?? "", b.KeyValue ?? "", StringComparison.Ordinal) && string.Equals(a.SubValue ?? "", b.SubValue ?? "", StringComparison.Ordinal);

    /// <summary>Phạm vi ảnh bản 1 (GĐ1): LYT*, LYS*, T?V — chưa có tiêu đề / quyền xem trang chi tiết.</summary>
    public static bool IsLayoutRowV1(CfgRow r) =>
        r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) || r.KeyID.StartsWith("LYS", StringComparison.OrdinalIgnoreCase)
        || CaptionKeys.Contains(r.KeyID, StringComparer.OrdinalIgnoreCase) && string.IsNullOrEmpty(r.SubID);

    /// <summary>Ảnh các dòng bố cục (JSON <c>{"v":2,"rows":[…]}</c>) để lưu phiên bản / khôi phục.</summary>
    public static string Snapshot(IEnumerable<CfgRow> rows) =>
        JsonSerializer.Serialize(new
        {
            v = 2,
            rows = rows.Where(IsLayoutRow).OrderBy(r => r.KeyID, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.SubValue, StringComparer.Ordinal)
                .Select(r => new { r.UserAutoID, r.KeyID, r.SubID, r.KeyValue, r.SubValue, r.VDate, r.PFID }),
        });

    /// <summary>Đọc ảnh và phạm vi của nó: mảng = bản 1 (<see cref="IsLayoutRowV1"/>), đối tượng có "v":2 = bản 2 (<see cref="IsLayoutRow"/>).</summary>
    public static (List<CfgRow> Rows, Func<CfgRow, bool> Scope) ReadSnapshot(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Array
            ? (JsonSerializer.Deserialize<List<CfgRow>>(json) ?? [], IsLayoutRowV1)
            : (doc.RootElement.GetProperty("rows").Deserialize<List<CfgRow>>() ?? [], IsLayoutRow);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Short(string? s) => s is null ? "" : s.Length > 40 ? s[..40] : s;
}
