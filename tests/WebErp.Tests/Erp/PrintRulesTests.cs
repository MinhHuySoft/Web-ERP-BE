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

    [Fact]
    public void Templates_are_validated_again_on_the_server()
    {
        Assert.Empty(ErpPrint.ValidateTemplate(SampleScreens.TemplateJson));
        Assert.Equal(["Mẫu không phải JSON hợp lệ."], ErpPrint.ValidateTemplate("{"));
        Assert.Equal(["Mẫu không đúng định dạng erp-print-template/1."], ErpPrint.ValidateTemplate("{\"$schema\":\"x\"}"));
        var dup = SampleScreens.TemplateJson.Replace("\"controls\":[{", "\"controls\":[{\"id\":\"txt1\",\"type\":\"text\"},{");
        Assert.Contains("Mã control bị trùng: txt1.", ErpPrint.ValidateTemplate(dup));
        var badId = SampleScreens.TemplateJson.Replace("\"id\":\"txt1\"", "\"id\":\"x;drop\"");
        Assert.Contains(ErpPrint.ValidateTemplate(badId), e => e.StartsWith("Mã control không hợp lệ", StringComparison.Ordinal));
        var badBand = SampleScreens.TemplateJson.Replace("\"type\":\"detail\"", "\"type\":\"script\"");
        Assert.Contains(ErpPrint.ValidateTemplate(badBand), e => e.StartsWith("Band có loại không hợp lệ", StringComparison.Ordinal));
        var longExpr = SampleScreens.TemplateJson.Replace("{\"field\":\"PurchaseRequestID\"}", "{\"expr\":\"" + new string('x', 2001) + "\"}");
        Assert.Contains(ErpPrint.ValidateTemplate(longExpr), e => e.Contains("2000", StringComparison.Ordinal));
        var href = SampleScreens.TemplateJson.Replace("\"assets\":{}", "\"assets\":{\"a\":{\"mime\":\"image/png\",\"href\":\"https://x\"}}");
        Assert.Contains(ErpPrint.ValidateTemplate(href), e => e.Contains("URL ngoài", StringComparison.Ordinal));
        var svg = SampleScreens.TemplateJson.Replace("\"assets\":{}", "\"assets\":{\"a\":{\"mime\":\"image/svg+xml\",\"data\":\"PHN2Zz4=\"}}");
        Assert.Contains(ErpPrint.ValidateTemplate(svg), e => e.Contains("PNG/JPEG", StringComparison.Ordinal));
        Assert.Single(ErpPrint.ValidateTemplate(new string(' ', ErpPrint.MaxTemplateChars + 1)));
    }
}
