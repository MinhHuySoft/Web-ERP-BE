using WebErp.Erp.Layout;

namespace WebErp.Tests.Erp;

/// <summary>Mô hình bố cục của trình vẽ layout (không DB): đọc vùng / nhóm, kiểm đầu vào, kế hoạch ghi như LayoutSettingFrm.</summary>
public sealed class LayoutModelTests
{
    private static readonly CfgRow[] Rows =
    [
        new("c01", "SP", "", "13", null),
        new("a1", "LYT1", "DocumentID", "1;4;1;1;;;;;;;;;;;;;;;;0;0", "001"),
        new("a2", "LYT1", "DocumentDate", "4;4;1;;;;;;;;;;;;;;;;;0;0", "002"),
        new("a3", "LYT1", "GrpBox1", "23;20;3;1;;;;;;;;;;;;;;;;0;0", "003"),
        new("a4", "LYT1GrpBox1", "Notes", "1;20;2;1;;;;Ghi chú:dài;;;;;;;;;;;;0;0", "001"),
        new("a5", "LYT2", "Price", "5;8;1;1;;;;;;;;;;;;;;;;0;0", "001"),
        new("a6", "LYT1", "Bad", "1;4;1", "x1"),               // SubValue không phải số: WinForms bỏ dòng
        new("a7", "LYTZZ", "Orphan", "1;4;1", "001"),          // vùng không thuộc tab nào: giữ nguyên
        new("s1", "LYS1GrpBox1", "Notes", "-16777024", "PV4"), // thuộc tính của ô trong nhóm
        new("s2", "LYS1", "Gone", "1", "PV3"),                  // thuộc tính mồ côi (không có ô): giữ nguyên
        new("t1", "T1V", "", "Chung", ""),
    ];

    [Fact]
    public void Parse_walks_tabs_and_groups_in_order()
    {
        var (items, zones) = LayoutModel.Parse(Rows);
        Assert.Equal(["DocumentID", "DocumentDate", "GrpBox1", "Notes", "Price"], items.Select(i => i.Name));
        Assert.Equal(("1", "GrpBox1", "1GrpBox1"), (items[3].Tab, items[3].Group, items[3].Zone));
        Assert.Equal(["1", "1GrpBox1", "2"], zones.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Revision_changes_only_with_layout_rows()
    {
        var r1 = LayoutModel.Revision(Rows);
        Assert.Equal(r1, LayoutModel.Revision([.. Rows, new CfgRow("x", "T0", "TN", "AnotherTbl", null)]));
        Assert.NotEqual(r1, LayoutModel.Revision([.. Rows.Where(r => r.UserAutoID != "a5")]));
    }

    [Fact]
    public void Diff_renumbers_moves_properties_with_items_and_keeps_orphans()
    {
        var seq = 0;
        // Đổi chỗ DocumentDate lên đầu, Notes ra khỏi nhóm sang tab 2, bỏ Price, thêm Memo vào nhóm, đổi tiêu đề tab 1, thêm tab 2.
        LayoutItem[] items =
        [
            new("1", "", "DocumentDate", "4;4;1;;;;;;;;;;;;;;;;;0;0"),
            new("1", "", "DocumentID", "1;6;1;1;;;;;;;;;;;;;;;;0;0"),
            new("1", "", "GrpBox1", "23;20;3;1;;;;;;;;;;;;;;;;0;0"),
            new("1", "GrpBox1", "Memo", "1;4;1"),
            new("2", "", "Notes", "1;20;2;1;;;;Ghi chú:dài;;;;;;;;;;;;0;0"),
        ];
        var p = LayoutModel.Diff(Rows, items, new Dictionary<string, string> { ["T1V"] = "", ["T2V"] = "Khác" }, () => $"n{++seq}");

        Assert.Equal(["a5", "t1"], p.Delete.Select(r => r.UserAutoID).Order(StringComparer.Ordinal)); // Price, T1V rỗng; a6/a7/s2 không đụng
        var up = p.Update.ToDictionary(r => r.UserAutoID);
        Assert.Equal(("LYT1", "001"), (up["a2"].KeyID, up["a2"].SubValue));
        Assert.Equal(("002", "1;6;1;1;;;;;;;;;;;;;;;;0;0"), (up["a1"].SubValue, up["a1"].KeyValue));
        Assert.Equal(("LYT2", "001"), (up["a4"].KeyID, up["a4"].SubValue));
        Assert.Equal("LYS2", up["s1"].KeyID); // thuộc tính đi theo ô sang vùng mới
        Assert.False(up.ContainsKey("a3"));    // GrpBox1: 003 như cũ
        Assert.Equal([("n1", "LYT1GrpBox1", "Memo", "001"), ("n2", "T2V", "", "")],
            p.Insert.Select(r => (r.UserAutoID, r.KeyID, r.SubID ?? "", r.SubValue ?? "")));
        Assert.Equal("Khác", p.Insert[1].KeyValue);
    }

    [Fact]
    public void Diff_without_changes_is_empty()
    {
        var (items, _) = LayoutModel.Parse(Rows);
        var p = LayoutModel.Diff(Rows, items, null, () => throw new InvalidOperationException());
        Assert.Equal(0, p.Count);
    }

    [Fact]
    public void ReadInput_rejects_bad_names_tabs_groups_sizes_and_duplicates()
    {
        const string ok = """{"$schema":"erp-form-layout/1","form":"F","revision":"r","captions":{"T1V":"A"},"items":[{"tab":"1","group":"","name":"A","cfg":"1;4;1"}]}""";
        var good = LayoutModel.ReadInput(ok);
        Assert.Empty(good.Errors);
        Assert.Equal(("F", "r", "A"), (good.Form, good.Revision, good.Captions!["T1V"]));

        Assert.NotEmpty(LayoutModel.ReadInput("{\"$schema\":\"khac\"}").Errors);
        Assert.NotEmpty(LayoutModel.ReadInput("không phải json").Errors);
        string Bad(string item) => LayoutModel.ReadInput("{\"$schema\":\"erp-form-layout/1\",\"items\":[" + item + "]}").Errors.Single();
        Assert.Contains("tab", Bad("""{"tab":"4","group":"","name":"A","cfg":"1;4;1"}"""));
        Assert.Contains("Tên ô", Bad("""{"tab":"1","group":"","name":"A;DROP","cfg":"1;4;1"}"""));
        Assert.Contains("loại control", Bad("""{"tab":"1","group":"","name":"A","cfg":"99;4;1"}"""));
        Assert.Contains("độ rộng", Bad("""{"tab":"1","group":"","name":"A","cfg":"1;41;1"}"""));
        Assert.Contains("nhóm GrpX", Bad("""{"tab":"1","group":"GrpX","name":"A","cfg":"1;4;1"}"""));
        Assert.Contains("hai lần", LayoutModel.ReadInput("""{"$schema":"erp-form-layout/1","items":[{"tab":"1","name":"A","cfg":"1"},{"tab":"2","name":"a","cfg":"1"}]}""").Errors.Single());
        Assert.Contains("Tiêu đề tab", LayoutModel.ReadInput("""{"$schema":"erp-form-layout/1","captions":{"T0":"x"},"items":[]}""").Errors.Single());
    }

    [Fact]
    public void Snapshot_round_trips_layout_rows_only()
    {
        var back = LayoutModel.ReadSnapshot(LayoutModel.Snapshot(Rows));
        Assert.Equal(Rows.Where(LayoutModel.IsLayoutRow).Select(r => r.UserAutoID).Order(StringComparer.Ordinal), back.Select(r => r.UserAutoID).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(back, r => r.KeyID == "SP");
    }
}
