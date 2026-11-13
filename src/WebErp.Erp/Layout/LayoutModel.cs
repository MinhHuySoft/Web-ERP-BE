using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebErp.Erp.Layout;

/// <summary>Một dòng <c>SY_FrmCfg</c> của form (đủ cột để ghi lại nguyên trạng khi khôi phục; kiểm trên MedData 2026-11-13).</summary>
public sealed record CfgRow(string UserAutoID, string KeyID, string? SubID, string? KeyValue, string? SubValue, DateTime? VDate = null, string? PFID = null);

/// <summary>Một ô / control của bố cục: một dòng <c>LYT{tab}{nhóm…}</c>. <see cref="Group"/> = chuỗi tên nhóm cha nối nhau ("" = ngoài nhóm).</summary>
public sealed record LayoutItem(string Tab, string Group, string Name, string Cfg)
{
    public string Zone => Tab + Group;
}

/// <summary>
/// Mô hình bố cục của <c>LayoutSettingFrm</c> (mã dịch ngược Tools.Layout: LayoutConfigCxtl, LayoutX.SaveConfig), không chạm DB:
/// <list type="bullet">
/// <item>Tab: <c>1,2,3,A,B,C</c> (master) và <c>5…13</c> (trang chi tiết 1…9). Ô trong nhóm (ControlType 23) nằm ở
/// <c>LYT{tab}{Nhóm}</c>, nhóm lồng nối tiếp tên (<c>LYT1GrpBox1GrpBox2</c>).</item>
/// <item>Thứ tự: <c>SubValue</c> = số thứ tự 1..n đệm 3 chữ số ("001"); thiếu = 999; không phải số thì WinForms bỏ dòng.</item>
/// <item>Thuộc tính <c>LYS{vùng}</c> (SubID = tên ô) đi theo ô khi ô đổi vùng; bỏ ô thì bỏ thuộc tính của ô.</item>
/// <item>Tiêu đề tab master: <c>T1V…TCV</c> (SubID rỗng).</item>
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

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,49}$")]
    private static partial Regex NamePattern();

    /// <summary>Dòng thuộc phạm vi trình vẽ (để chụp ảnh / khôi phục): LYT*, LYS*, T?V.</summary>
    public static bool IsLayoutRow(CfgRow r) =>
        r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) || r.KeyID.StartsWith("LYS", StringComparison.OrdinalIgnoreCase)
        || CaptionKeys.Contains(r.KeyID, StringComparer.OrdinalIgnoreCase) && string.IsNullOrEmpty(r.SubID);

    public static int ControlType(string? cfg) =>
        int.TryParse((cfg ?? "").Split(';')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;

    /// <summary>Đọc bố cục: duyệt từ các tab, đi vào nhóm. Trả ô theo thứ tự và các vùng (phần sau LYT) đã đọc.</summary>
    public static (List<LayoutItem> Items, HashSet<string> Zones) Parse(IEnumerable<CfgRow> rows)
    {
        var lyt = rows.Where(r => r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(r.SubID)
                                  && !string.IsNullOrWhiteSpace(r.KeyValue) && Order(r) >= 0)
            .GroupBy(r => r.KeyID[3..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.GroupBy(x => x.SubID!, StringComparer.OrdinalIgnoreCase).Select(x => x.First())
                .OrderBy(Order).ThenBy(x => x.UserAutoID, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);
        var items = new List<LayoutItem>();
        var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in Tabs) Walk(tab, tab, "", 0);
        return (items, zones);

        void Walk(string tab, string zone, string group, int depth)
        {
            if (!lyt.TryGetValue(zone, out var list) || !zones.Add(zone)) return;
            foreach (var r in list)
            {
                items.Add(new LayoutItem(tab, group, r.SubID!, r.KeyValue!.Trim()));
                if (ControlType(r.KeyValue) == 23 && depth < 8) Walk(tab, zone + r.SubID, group + r.SubID, depth + 1);
            }
        }
    }

    /// <summary>Thứ tự trong vùng: rỗng = 999 (cuối); không phải số = -1 (WinForms bỏ dòng khi đọc).</summary>
    private static int Order(CfgRow r) =>
        string.IsNullOrWhiteSpace(r.SubValue) ? 999 : int.TryParse(r.SubValue.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1;

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
    public sealed record Input(string? Form, string? Revision, List<LayoutItem> Items, Dictionary<string, string>? Captions, List<string> Errors);

    /// <summary>Đọc và kiểm JSON <c>erp-form-layout/1</c> (không tin FE): tên, tab, nhóm, chuỗi cấu hình, độ dài cột DB.</summary>
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
            if (!root.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return new Input(Str(root, "form"), Str(root, "revision"), items, captions, [.. errors, "Bố cục thiếu danh sách ô."]);
            if (arr.GetArrayLength() > MaxItems) return new Input(null, null, items, null, [$"Bố cục có quá nhiều ô (tối đa {MaxItems})."]);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in arr.EnumerateArray())
            {
                var tab = Tabs.FirstOrDefault(t => t.Equals(Str(x, "tab"), StringComparison.OrdinalIgnoreCase));
                var group = Str(x, "group") ?? "";
                var name = Str(x, "name") ?? "";
                var cfg = (Str(x, "cfg") ?? "").Trim();
                if (tab is null) { errors.Add($"Ô {Short(name)}: tab không hợp lệ."); continue; }
                if (!NamePattern().IsMatch(name)) { errors.Add($"Tên ô không hợp lệ: \"{Short(name)}\"."); continue; }
                if (!names.Add(name)) { errors.Add($"Ô {name} có hai lần trong bố cục."); continue; }
                if (group.Length > 0 && !Regex.IsMatch(group, "^[A-Za-z_][A-Za-z0-9_]*$")) { errors.Add($"Ô {name}: nhóm không hợp lệ."); continue; }
                if (3 + tab.Length + group.Length > 50) { errors.Add($"Ô {name}: tên nhóm lồng quá dài (KeyID tối đa 50 ký tự)."); continue; }
                var p = cfg.Split(';');
                if (cfg.Length is 0 or > 4000 || p.Length > 40) { errors.Add($"Ô {name}: chuỗi cấu hình không hợp lệ."); continue; }
                if (!int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ct) || !ControlTypes.Contains(ct))
                { errors.Add($"Ô {name}: loại control {Short(p[0])} không hợp lệ."); continue; }
                if (p.Length > 1 && p[1].Length > 0 && (!int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sx) || sx is < 0 or > 40 and not 99))
                { errors.Add($"Ô {name}: độ rộng {Short(p[1])} không hợp lệ (0–40 hoặc 99)."); continue; }
                items.Add(new LayoutItem(tab, group, name, cfg));
            }
            // Nhóm của mỗi ô phải là một ô nhóm (23) cùng tab, có vùng = nhóm cha + tên.
            var zones = new HashSet<string>(items.Where(i => ControlType(i.Cfg) == 23).Select(i => i.Tab + "|" + i.Group + i.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var i in items.Where(i => i.Group.Length > 0 && !zones.Contains(i.Tab + "|" + i.Group)))
                errors.Add($"Ô {i.Name} thuộc nhóm {i.Group} không có trong tab.");
            return new Input(Str(root, "form"), Str(root, "revision"), items, captions, errors);
        }
    }

    /// <summary>Kế hoạch ghi: xóa / sửa / thêm dòng (theo UserAutoID). Dòng sửa giữ UserAutoID cũ như WinForms.</summary>
    public sealed record Plan(List<CfgRow> Delete, List<CfgRow> Update, List<CfgRow> Insert)
    {
        public int Count => Delete.Count + Update.Count + Insert.Count;
    }

    /// <summary>
    /// Dòng cần ghi để bố cục thành <paramref name="items"/> (+ tiêu đề tab). Chỉ đụng: LYT của các vùng đọc được và của vùng mới,
    /// LYS của ô (đổi vùng theo ô / bỏ theo ô), T?V có trong <paramref name="captions"/>.
    /// </summary>
    public static Plan Diff(IReadOnlyList<CfgRow> current, IReadOnlyList<LayoutItem> items, IReadOnlyDictionary<string, string>? captions, Func<string> newId)
    {
        var (oldItems, oldZones) = Parse(current);
        var oldZoneOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in oldItems) oldZoneOf.TryAdd(i.Name, i.Zone);
        var newZoneOf = items.ToDictionary(i => i.Name, i => i.Zone, StringComparer.OrdinalIgnoreCase);

        // Dòng đang có thuộc phạm vi quản lý.
        var managed = current.Where(r =>
            // Dòng LYT có thứ tự không phải số: WinForms bỏ khi đọc, web để nguyên (không đoán).
            r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) && oldZones.Contains(r.KeyID[3..]) && Order(r) >= 0
            || r.KeyID.StartsWith("LYS", StringComparison.OrdinalIgnoreCase) && oldZones.Contains(r.KeyID[3..]) && r.SubID is { } s
               && oldZoneOf.TryGetValue(s, out var z) && z.Equals(r.KeyID[3..], StringComparison.OrdinalIgnoreCase)
            || captions is not null && string.IsNullOrEmpty(r.SubID) && captions.ContainsKey(r.KeyID)).ToList();
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
                var old = Take(r => r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) && i.Name.Equals(r.SubID, StringComparison.OrdinalIgnoreCase)
                                    && r.KeyID.Equals(key, StringComparison.OrdinalIgnoreCase))
                          ?? Take(r => r.KeyID.StartsWith("LYT", StringComparison.OrdinalIgnoreCase) && i.Name.Equals(r.SubID, StringComparison.OrdinalIgnoreCase));
                desired.Add(old is null
                    ? (new CfgRow(newId(), key, i.Name, i.Cfg, sub), true)
                    : (old with { KeyID = key, SubID = i.Name, KeyValue = i.Cfg, SubValue = sub }, false));
            }
        }
        // LYS: thuộc tính của ô còn trong bố cục đi theo ô sang vùng mới.
        foreach (var r in managed.Where(r => r.KeyID.StartsWith("LYS", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (!newZoneOf.TryGetValue(r.SubID!, out var nz)) continue;
            pool.Remove(r);
            desired.Add((r with { KeyID = "LYS" + nz }, false));
        }
        // Tiêu đề tab master.
        foreach (var (key, value) in captions ?? new Dictionary<string, string>())
        {
            if (value.Length == 0) continue;
            var old = Take(r => r.KeyID.Equals(key, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(r.SubID));
            desired.Add(old is null ? (new CfgRow(newId(), key, "", value, ""), true) : (old with { KeyValue = value }, false));
        }

        var byId = managed.ToDictionary(r => r.UserAutoID, StringComparer.Ordinal);
        var update = desired.Where(d => !d.New && byId.TryGetValue(d.Row.UserAutoID, out var o) && !Same(o, d.Row)).Select(d => d.Row).ToList();
        return new Plan(pool, update, desired.Where(d => d.New).Select(d => d.Row).ToList());
    }

    private static bool Same(CfgRow a, CfgRow b) =>
        string.Equals(a.KeyID, b.KeyID, StringComparison.Ordinal) && string.Equals(a.SubID ?? "", b.SubID ?? "", StringComparison.Ordinal)
        && string.Equals(a.KeyValue ?? "", b.KeyValue ?? "", StringComparison.Ordinal) && string.Equals(a.SubValue ?? "", b.SubValue ?? "", StringComparison.Ordinal);

    /// <summary>Ảnh các dòng bố cục (JSON) để lưu phiên bản / khôi phục.</summary>
    public static string Snapshot(IEnumerable<CfgRow> rows) =>
        JsonSerializer.Serialize(rows.Where(IsLayoutRow).OrderBy(r => r.KeyID, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.SubValue, StringComparer.Ordinal)
            .Select(r => new { r.UserAutoID, r.KeyID, r.SubID, r.KeyValue, r.SubValue, r.VDate, r.PFID }));

    public static List<CfgRow> ReadSnapshot(string json) =>
        JsonSerializer.Deserialize<List<CfgRow>>(json) ?? [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Short(string? s) => s is null ? "" : s.Length > 40 ? s[..40] : s;
}
