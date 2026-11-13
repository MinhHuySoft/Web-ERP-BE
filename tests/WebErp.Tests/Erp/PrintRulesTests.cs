using WebErp.Core.Print;
using WebErp.Erp.Metadata;
using WebErp.Erp.Print;
using WebErp.Tests.Host;

namespace WebErp.Tests.Erp;

/// <summary>Quy tắc in như WinForms: tên báo cáo của form, danh sách mẫu "tệp;Tên;…" của SY_RpDTbl.</summary>
public sealed class PrintRulesTests
{
    [Fact]
    public void Report_name_is_form_without_Frm_plus_Report_unless_BPRN_overrides()
    {
        Assert.Equal("AP_PurposeRequestListReport", ErpPrint.ReportName(SampleScreens.Form));
        var withBprn = MetadataCompiler.Compile("AR_InvoiceFrm", "AR_InvoiceFrm",
            new FrmLstRow("AR_InvoiceFrm", "EDIT", "Hóa đơn", "AR_InvoiceTbl", "DocumentID", null, null, null, null, null),
            [new FrmCfgRow("T0", "TN", "AR_InvoiceTbl", null), new FrmCfgRow("T0", "PK", "DocumentID", null), new FrmCfgRow("BPRN", "", "AR_Invoice2Report", null)], []);
        Assert.Equal("AR_Invoice2Report", ErpPrint.ReportName(withBprn));
        var bad = MetadataCompiler.Compile("X_Frm", "X_Frm", new FrmLstRow("X_Frm", "EDIT", "X", "X", "ID", null, null, null, null, null),
            [new FrmCfgRow("T0", "TN", "X", null), new FrmCfgRow("T0", "PK", "ID", null), new FrmCfgRow("BPRN", "", "x'; drop table y--", null)], []);
        Assert.Null(ErpPrint.ReportName(bad));
    }

    [Fact]
    public void Template_list_is_parsed_in_pairs_and_unsafe_names_are_dropped()
    {
        var t = ErpPrint.ParseTemplates("CS_ReceiptReport;In phiếu thu;CS_Receipt2Report.rpx;In phiếu thu 2 liên;..\\x;Lạ;CS_ReceiptReport;Trùng;ChungTuGhiSoReport", "CS_ReceiptReport");
        Assert.Equal(
            [new PrintTemplateOption("CS_ReceiptReport", "In phiếu thu"), new PrintTemplateOption("CS_Receipt2Report", "In phiếu thu 2 liên"),
                new PrintTemplateOption("ChungTuGhiSoReport", "ChungTuGhiSoReport")],
            t);
        Assert.Equal([new PrintTemplateOption("AP_OrderReport", "AP_OrderReport")], ErpPrint.ParseTemplates(null, "AP_OrderReport"));
        Assert.Equal([new PrintTemplateOption("AP_OrderReport", "AP_OrderReport")], ErpPrint.ParseTemplates(" ; ", "AP_OrderReport"));
    }
}
