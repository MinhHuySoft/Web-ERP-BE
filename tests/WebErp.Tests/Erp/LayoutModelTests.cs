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
    public void Snapshot_round_trips_layout_rows_only_and_old_snapshots_keep_their_scope()
    {
        CfgRow[] rows = [.. Rows, new("p1", "T1", "DCP", "Hàng", null), new("p2", "TPV2", "", "1", ""), new("p3", "T1", "TN", "Tbl", null)];
        var (back, scope) = LayoutModel.ReadSnapshot(LayoutModel.Snapshot(rows));
        Assert.Equal(rows.Where(LayoutModel.IsLayoutRow).Select(r => r.UserAutoID).Order(StringComparer.Ordinal), back.Select(r => r.UserAutoID).Order(StringComparer.Ordinal));
        Assert.Contains(back, r => r.UserAutoID == "p1");
        Assert.DoesNotContain(back, r => r.KeyID is "SP" || r.UserAutoID == "p3");
        Assert.True(scope(rows.Single(r => r.UserAutoID == "p2")));
        // Ảnh bản 1 (mảng, GĐ1) không có dòng trang chi tiết: khôi phục không được đụng tới chúng.
        var (_, oldScope) = LayoutModel.ReadSnapshot("[{\"UserAutoID\":\"a1\",\"KeyID\":\"LYT1\",\"SubID\":\"DocumentID\",\"KeyValue\":\"1\",\"SubValue\":\"001\"}]");
        Assert.False(oldScope(rows.Single(r => r.UserAutoID == "p1")));
        Assert.True(oldScope(rows.Single(r => r.UserAutoID == "a1")));
    }

    [Fact]
    public void Parse_reads_item_properties()
    {
        var (items, _) = LayoutModel.Parse(Rows);
        Assert.Equal("-16777024", items.Single(i => i.Name == "Notes").Props!["PV4"]);
        Assert.Empty(items.Single(i => i.Name == "Price").Props!);
    }

    [Fact]
    public void Diff_edits_allowed_properties_keeps_sql_ones_and_renames()
    {
        CfgRow[] rows =
        [
            new("a1", "LYT1", "CommandButtonCtl_2", "22;4;1;;;;;;;;;;;;;;;;;0;0", "001"),
            new("s1", "LYS1", "CommandButtonCtl_2", "Xem hợp đồng", "BT"),
            new("s2", "LYS1", "CommandButtonCtl_2", "AR_Invoice_XemHopDongStp N'{0}'", "C4"),
            new("a2", "LYT1", "UserControl1", "28;6;1;;;;;Nhãn", "002"),
            new("s3", "LYS1", "UserControl1", "10", "FS"),
            new("p1", "T1", "DCP", "Chi tiết", null),
        ];
        var seq = 0;
        LayoutItem[] items =
        [
            new("1", "", "CommandButtonCtl_2", "22;4;1;;;;;;;;;;;;;;;;;0;0",
                new Dictionary<string, string> { ["BT"] = "Xem thông tin hợp đồng", ["C4"] = "AR_Invoice_XemHopDongStp N'{0}'", ["PT"] = "-5" }),
            new("1", "", "LblGhiChu", "28;6;1;;;;;Nhãn", new Dictionary<string, string> { ["FS"] = "", ["FB"] = "1" }, From: "UserControl1"),
        ];
        var p = LayoutModel.Diff(rows, items, null, () => $"n{++seq}", new Dictionary<int, PageSetting> { [1] = new("Hàng hóa", "1"), [2] = new(null, "0") });
        Assert.Null(p.Errors);
        var up = p.Update.ToDictionary(r => r.UserAutoID);
        Assert.Equal("Xem thông tin hợp đồng", up["s1"].KeyValue);
        Assert.False(up.ContainsKey("s2")); // mã có lệnh SQL: giữ nguyên
        Assert.Equal(("LblGhiChu", "002"), (up["a2"].SubID, up["a2"].SubValue)); // đổi tên giữ UserAutoID
        Assert.Equal("Hàng hóa", up["p1"].KeyValue);
        Assert.Equal(["s3"], p.Delete.Select(r => r.UserAutoID)); // FS để trống ⇒ về mặc định
        Assert.Equal([("LYS1", "CommandButtonCtl_2", "PT", "-5"), ("LYS1", "LblGhiChu", "FB", "1"), ("TPV1", "", "", "1")],
            p.Insert.Select(r => (r.KeyID, r.SubID ?? "", r.SubValue ?? "", r.KeyValue ?? "")));

        // Sửa mã có SQL / lệnh ⇒ lỗi, kể cả mã mới.
        var bad = LayoutModel.Diff(rows, [items[0] with { Props = new Dictionary<string, string> { ["C4"] = "DROP TABLE x", ["BT"] = "A" } }], null, () => "x");
        Assert.Contains(bad.Errors!, e => e.Contains("C4", StringComparison.Ordinal));
        var bad2 = LayoutModel.Diff(rows, [items[1] with { Props = new Dictionary<string, string> { ["LBS"] = "SELECT 1" } }], null, () => "x");
        Assert.Contains(bad2.Errors!, e => e.Contains("LBS", StringComparison.Ordinal));
    }

    [Fact]
    public void Diff_blocks_tricks_around_command_properties()
    {
        CfgRow[] rows =
        [
            new("a1", "LYT1", "BarcodeInputCtl", "22;5;1", "001"),
            new("s1", "LYS1", "BarcodeInputCtl", "AG_XuatKho_BarcodeStp '{0}'", "F1"), // F1 của ô quét mã là câu lệnh
            new("a2", "LYT1", "CommandButtonCtl_2", "22;4;1", "002"),
            new("s2", "LYS1", "CommandButtonCtl_2", "AR_Invoice_XemHopDongStp N'{0}'", "C4"),
        ];
        // Đổi ô quét mã thành nhãn (28) để sửa F1 (kiểu viền chữ của nhãn): chặn, và cũng không cho đổi loại khi còn F1.
        var asLabel = LayoutModel.Diff(rows, [new LayoutItem("1", "", "BarcodeInputCtl", "28;5;1", new Dictionary<string, string> { ["F1"] = "1" })], null, () => "x");
        Assert.Contains(asLabel.Errors!, e => e.Contains("F1", StringComparison.Ordinal));
        // Đổi tên CommandButtonCtl_2 thành ExecSQLWithParaButtonCtl_9: C4 sẽ bị hiểu là câu lệnh cần chạy ⇒ chặn.
        var renamed = LayoutModel.Diff(rows, [new LayoutItem("1", "", "ExecSQLWithParaButtonCtl_9", "22;4;1", From: "CommandButtonCtl_2")], null, () => "x");
        Assert.Contains(renamed.Errors!, e => e.Contains("lớp khác", StringComparison.Ordinal));
        // Đổi tên trong cùng lớp thì được, thuộc tính đi theo.
        var same = LayoutModel.Diff(rows, [new LayoutItem("1", "", "CommandButtonCtl_3", "22;4;1", From: "CommandButtonCtl_2")], null, () => "x");
        Assert.Null(same.Errors);
        Assert.Contains(same.Update, r => r.UserAutoID == "s2" && r.SubID == "CommandButtonCtl_3" && r.KeyValue == "AR_Invoice_XemHopDongStp N'{0}'");
        Assert.Equal("ExecSQLWithParaButtonCtl", LayoutModel.UcClass("ExecSQLWithParaButtonCtl_9"));
        Assert.Equal("AG_XuatKhoFrm", LayoutModel.UcClass("AG_XuatKhoFrm"));
    }

    [Fact]
    public void ReadInput_reads_props_rename_and_pages()
    {
        var input = LayoutModel.ReadInput("""{"$schema":"erp-form-layout/1","pages":{"1":{"caption":"Hàng","visible":"4"},"7":{"visible":"1"}},"items":[{"tab":"1","name":"B","from":"A","cfg":"28;6;1","props":{"FS":"10","bad code":"x"}}]}""");
        Assert.Equal(2, input.Errors.Count); // mã thuộc tính sai, quyền xem trang 7 (chỉ trang 1–5)
        Assert.Equal(("A", "10"), (input.Items[0].From, input.Items[0].Props!["FS"]));
        Assert.Equal(new PageSetting("Hàng", "4"), input.Pages![1]);
    }
}
