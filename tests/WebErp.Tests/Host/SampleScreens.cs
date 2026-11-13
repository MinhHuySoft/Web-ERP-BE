using WebErp.Core.Print;
using WebErp.Core.Metadata;
using WebErp.Erp.Metadata;

namespace WebErp.Tests.Host;

/// <summary>Dữ liệu mẫu (GIẢ) cho màn metadata: dùng chung cho test Host và contract test (file được link sang ContractTests).</summary>
public static class SampleScreens
{
    public static readonly FormMeta Form = MetadataCompiler.Compile("AP_PurposeRequestListFrm", "AP_PurposeRequestListFrm",
        new FrmLstRow("AP_PurposeRequestListFrm", "EDIT", "Đề nghị mua hàng", "AP_PurchaseRequestTbl", "PurchaseRequestID", null, null, null, null, null),
        [
            new FrmCfgRow("T0", "TN", "AP_PurchaseRequestTbl", null), new FrmCfgRow("T0", "PK", "PurchaseRequestID", null),
            new FrmCfgRow("T1", "TN", "AP_PurchaseRequestDetailTbl", null), new FrmCfgRow("T1", "PK", "UserAutoID", null),
        ], []);

    private static readonly ViewColumn[] Columns =
    [
        new("PurchaseRequestID", "Mã đề xuất", "text", null, false, false),
        new("PurchaseDate", "Ngày đề xuất", "date", null, false, false),
        new("isLock", "Đã khóa", "bool", null, false, false),
        new("Price", "Giá", "money", 0, false, true),
        new("RateExchange", "Tỷ giá", "number", 2, true, false),
        new("StatusID", "Trạng thái", "text", null, false, false, Names: new Dictionary<string, string> { ["1"] = "Đã duyệt" }),
    ];

    public static ListView List() => new(ScreenStatus.Ok, Form, Columns,
        [
            ["PO/001", new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Unspecified), false, 1234567.5m, 1m, 1],
            ["PO/002", new DateTime(2026, 11, 7, 0, 0, 0, DateTimeKind.Unspecified), true, null, null, 9],
        ],
        ["PO/001", "PO/002"], 1500, new Dictionary<string, decimal> { ["Price"] = 1234567.5m },
        [
            new ViewFilter("FromDate", "Từ ngày", "date", "2026-10-13"),
            new ViewFilter("ToDate", "Đến ngày", "date", "2026-11-12"),
            new ViewFilter("BranchID", "Chi nhánh", "select", "HN", [("", "(Tất cả)"), ("HN", "HN — Hà Nội")]),
            new ViewFilter("q", "Tìm nhanh", "search", ""),
        ],
        "PurchaseRequestID", CanOpenRecord: true, Page: 1, PageSize: 1000);

    public static RecordView Record(string key) => new(ScreenStatus.Ok, Form, key, "PurchaseRequestID",
        [
            new ViewTab("1", "Thông tin chung",
            [
                new ViewField("PurchaseRequestID", "Mã đề xuất", "text", key, 4, 1, true, null),
                new ViewField("PurchaseDate", "Ngày đề xuất", "date", new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Unspecified), 4, 1, false, null),
                new ViewField("isLock", "Đã khóa", "checkbox", true, 4, 1, false, null),
                new ViewField("Price", "Giá", "money", 1234567.5m, 8, 1, true, 0),
                new ViewField("ObjectID", "Nhà cung cấp", "lookup", "NCC01", 12, 1, false, null, Text: "Công ty A", LookupParas: ["HN"]),
                new ViewField("Notes", "Ghi chú <dài>", "textarea", "Dòng 1\nDòng 2", 40, 2, true, null),
            ]),
        ],
        [new ViewDetailTab("T1", "Hàng hóa đề nghị")]);

    /// <summary>Hộp thoại thực hiện của WinForms (vd sao lưu): chỉ mô tả + nút khóa.</summary>
    public static ListView Dialog() => new(ScreenStatus.Ok, MetadataCompiler.Compile("BackupDataFrm", "BackupDataFrm",
        new FrmLstRow("BackupDataFrm", "DIALOG", "Sao lưu dữ liệu", null, null, null, null, null, null, null),
        [new FrmCfgRow("WNOTE", "", "Sao lưu cơ sở dữ liệu.|Màn này chỉ để tham khảo.", null), new FrmCfgRow("WACT", "", "Sao lưu tại máy chủ;Shrink log 1MB", null)], []));

    /// <summary>Danh sách có nút thực hiện khóa và tab độc lập (IND) dưới lưới.</summary>
    public static ListView ListWithTabs() => List() with
    {
        Form = Form with { Settings = new Dictionary<string, string> { ["WACT"] = "Thêm năm;Xóa năm" } },
        Tabs = [new ViewDetailTab("T1", "Quý trong năm"), new ViewDetailTab("T2", "Kỳ trong năm")],
        GroupBy = "StatusID",
    };

    /// <summary>Kết quả tra cứu F4 của ô ObjectID (cột ẩn dùng để điền trường).</summary>
    public static LookupView Lookup() => new(ScreenStatus.Ok,
        [new("ObjectID", "Mã", "text", null, false, false), new("ObjectName", "Tên", "text", null, false, false), new("TaxCode", "MST", "text", null, true, false)],
        [["NCC01", "Công ty A", "0101"], ["NCC02", "Công ty <B>", null]], ["NCC01", "NCC02"], ["Công ty A", "Công ty <B>"], 7);

    public static DetailView Detail() => new(ScreenStatus.Ok, "T1",
        [new("ItemID", "Mã hàng", "text", null, false, false), new("Quantity", "Số lượng", "quantity", 2, false, true)],
        [["VT01", 2m], ["VT02", 3.5m]], ["d1", "d2"], 2, new Dictionary<string, decimal> { ["Quantity"] = 5.5m }, "UserAutoID");

    /// <summary>Bản in mẫu: hai mẫu, người ký, tham số hệ thống, một result set (có NULL, ngày, số) và một bảng phụ.</summary>
    public static PrintView Print(string? template) => new(ScreenStatus.Ok, "AP_PurposeRequestListReport",
        [new PrintTemplateOption("AP_PurposeRequestListReport", "In đề xuất mua hàng"), new PrintTemplateOption("AP_GoodsDeliveryReceiptReport", "In biên bản giao nhận")],
        template ?? "AP_PurposeRequestListReport", "rpx", ["PO/001"],
        [new PrintParam("Template", "text", template ?? "AP_PurposeRequestListReport"), new PrintParam("DocumentID", "text", "PO/001"),
            new PrintParam("UInput1", "text", "Nguyễn Văn A", "Giám đốc"), new PrintParam("Sys.SysCom1", "text", "Công ty Mẫu"),
            new PrintParam("Sys.ServerDate", "datetime", new DateTime(2026, 11, 13, 9, 30, 0)), new PrintParam("Sys.ReportPrintDate", "bool", true)],
        [new PrintUserInput("UInput1", "Giám đốc", "Nguyễn Văn A")],
        [new PrintDataSet([new ViewColumn("DocumentID", "DocumentID", "text", null, false, false), new ViewColumn("DocumentDate", "DocumentDate", "datetime", null, false, false),
                new ViewColumn("Amount", "Amount", "number", null, false, false), new ViewColumn("Notes", "Notes", "text", null, false, false)],
            [["PO/001", new DateTime(2026, 11, 10), 1234567.5m, null], ["PO/001", new DateTime(2026, 11, 10), 10m, "Ghi chú <b>"]]),
         new PrintDataSet([new ViewColumn("ItemID", "ItemID", "text", null, false, false)], [["VT01"]])],
        []);
}
