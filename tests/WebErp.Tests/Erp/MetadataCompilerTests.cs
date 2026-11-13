using WebErp.Erp.Metadata;

namespace WebErp.Tests.Erp;

/// <summary>Dòng cấu hình giống form "Đề nghị mua hàng" của MedData (AP_PurposeRequestListFrm), rút gọn.</summary>
public sealed class MetadataCompilerTests
{
    private static readonly FrmLstRow Lst = new("AP_PurposeRequestListFrm", "EDIT", "Đề nghị mua hàng", "AP_PurchaseRequestTbl", "PurchaseRequestID",
        null, null, null, null, null);

    private static readonly FrmCfgRow[] Cfg =
    [
        new("SP", "", "13", null), new("SPA", "", "30", null), new("DFP", "", "PurchaseDate", null),
        new("T0", "TN", "AP_PurchaseRequestTbl", null), new("T0", "PK", "PurchaseRequestID", null),
        new("T0", "GD2", "PurchaseRequestID;PurchaseDate;StatusID", null), new("T0", "HDE", "RateExchange", null),
        new("T0", "SO", "PurchaseRequestID Desc", null), new("T0", "ACP", "Price", null),
        new("T1", "TN", "AP_PurchaseRequestDetailTbl", null), new("T1", "PK", "UserAutoID", null),
        new("T1", "TN1", "CF_ItemTbl", null), new("T1", "FKA1", "ItemID", null), new("T1", "FKB1", "ItemID", null),
        new("T1", "SE1", "A.ItemName, A.Unit", null), new("T2", "TN", "ChỉCóTN", null),
        new("F0", "PurchaseDate", "Ngày đề nghị", "CV"),
        new("LYT1", "PurchaseRequestID", "1;4;1;1;;;;;;;;;;;;;;;;0;0", "001"),
        new("LYT1", "PrintButtonCtl_1", "22;4;1;;1;;;;;;;;;;;;;;1;0;0", "002"),
        new("LYT1", "Notes", "1;99;2;1;;;;Ghi chú:dài;;;;;;;;;;;;0;0", "004"),
        new("LYT1", "RateExchange", "1;4;1;;;;;;;;;;;;;;;;1;0;0", "003"),
        new("LYT2", "Price", "5;8;1;1;;;;;;7;;;;;;;;;;0;0", "001"),
    ];

    [Fact]
    public void Compiles_slots_settings_captions_and_layout()
    {
        var f = MetadataCompiler.Compile("AP_PurposeRequestListFrm", "AP_PurposeRequestListFrm", Lst, Cfg, []);

        Assert.True(f.IsEdit);
        Assert.Equal(13, f.SettingInt("SP", 0));
        Assert.Equal("PurchaseDate", f.Setting("DFP"));
        var t0 = f.Master!;
        Assert.Equal("AP_PurchaseRequestTbl", t0.Source);
        Assert.Equal(["PurchaseRequestID", "PurchaseDate", "StatusID"], t0.Order);
        Assert.Equal(["Price"], t0.AmountColumns);
        var t1 = Assert.Single(f.Details); // T2 thiếu PK nên không hợp lệ
        Assert.Equal("A", Assert.Single(t1.Joins).Alias);
        Assert.Equal("Ngày đề nghị", f.Captions["F0:PurchaseDate"]);

        // Ô ẩn (vị trí 18 = 1) bị bỏ, kể cả nút in ẩn; thứ tự theo SubValue; ":" trong caption là ";".
        Assert.Equal(["PurchaseRequestID", "Notes", "Price"], f.Layout.Select(l => l.Field));
        var notes = f.Layout.Single(l => l.Field == "Notes");
        Assert.Equal(("Ghi chú;dài", 40, 2, true), (notes.Caption, notes.Span, notes.Rows, notes.RowStart));
        Assert.Equal(7, f.Layout.Single(l => l.Field == "Price").VisiblePermiss);
    }

    [Fact]
    public void Compiles_detail_page_layout_and_button_captions()
    {
        // Giống hóa đơn bán (AR_InvoiceFrm) của MedData: LYT6 là ô của trang chi tiết 2; nút user control lấy chữ ở LYT[7], LYS BT, rồi mặc định.
        FrmCfgRow[] cfg =
        [
            new("T0", "TN", "AR_InvoiceTbl", null), new("T0", "PK", "DocumentID", null),
            new("LYT1", "DocumentID", "1;4;1;1;;;;;;;;;;;;;;;;0;0", "001"),
            new("LYT1", "StatusButtonCtl_1", "22;3;1;;;;;Chuyển trạng thái;;;;;;;;SELECT 1;;;;", "003"),
            new("LYT1", "CommandButtonCtl_2", "22;4;1;;;;;;;;;;;;;;;;;0;0", "006"),
            new("LYT1", "ViettelPostCtl", "22;4;1;;;;;;;;;;;;;;;;;0;0", "020"),
            new("LYT1", "GridDetailCtl", "22;99;99;;;;;;;;;;;;;;;;;0;0", "021"),
            new("LYT1", "CommandButtonCtl_3", "22;4;1;;;;;;;;;;;;;;;;;0;0", "022"),
            new("LYT1", "UserControl462", "26;4;1;;;;;Tính khuyến mãi;;;;;;;2;;;", "023"),
            new("LYS1", "CommandButtonCtl_2", "Xem thông tin hợp đồng", "BT"),
            new("LYS1", "CommandButtonCtl_2", "AR_Invoice_XemHopDongStp N'{0}'", "C4"),
            new("LYT6", "PaymentTermID", "2;4;1;1;;110;;;;;;;;;;;;;;0;0", "002"),
            new("LYT6", "PaymentTypeID", "2;4;1;1;;110;;;;;;;;;;;;;;0;0", "001"),
            new("LYT14", "Bogus", "1;4;1;1;;;;;;;;;;;;;;;;0;0", "001"),
        ];
        var f = MetadataCompiler.Compile("AR_InvoiceFrm", "AR_InvoiceFrm", null, cfg, []);

        // LYT14 không phải tab của engine; LYT6 → trang chi tiết 2.
        Assert.Equal(["DocumentID", "StatusButtonCtl_1", "CommandButtonCtl_2", "ViettelPostCtl", "GridDetailCtl", "CommandButtonCtl_3",
            "UserControl462", "PaymentTypeID", "PaymentTermID"], f.Layout.Select(l => l.Field));
        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 2, 2], f.Layout.Select(l => l.DetailPage));
        string? Btn(string name) => f.Layout.Single(l => l.Field == name).ButtonText;
        Assert.Equal("Chuyển trạng thái", Btn("StatusButtonCtl_1"));
        Assert.Equal("Xem thông tin hợp đồng", Btn("CommandButtonCtl_2"));
        Assert.Equal("VIETEL POST", Btn("ViettelPostCtl"));
        Assert.Null(Btn("GridDetailCtl"));        // lưới con: không phải nút
        Assert.Null(Btn("CommandButtonCtl_3"));   // không có chữ nào: web bỏ qua
        Assert.Equal("Tính khuyến mãi", Btn("UserControl462"));
        Assert.Null(Btn("DocumentID"));
    }

    [Fact]
    public void List_form_without_T0_falls_back_to_SY_FrmLstTbl()
    {
        var lst = new FrmLstRow("CF_BranchListFrm", "LIST", "Chi nhánh", "CF_BranchTbl", "BranchID", "isDisable = 0", "Notes", "Budget", null, null);
        var f = MetadataCompiler.Compile("CF_BranchListFrm", "CF_BranchListFrm", lst, [], []);
        var t0 = f.Master!;
        Assert.Equal(("CF_BranchTbl", "BranchID", "isDisable = 0"), (t0.Table, t0.PrimaryKey, t0.FilterEx));
        Assert.Equal(["Notes"], t0.Hidden);
        Assert.Equal(["Budget"], t0.SumColumns);
    }
}
