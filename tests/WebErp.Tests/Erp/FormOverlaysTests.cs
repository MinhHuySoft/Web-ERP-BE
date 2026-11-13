using WebErp.Core.Metadata;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;

namespace WebErp.Tests.Erp;

/// <summary>Tệp mô tả form viết tay (Metadata/Overlays/*.json) nạp được và biên dịch thành slot như SY_FrmCfg.</summary>
public sealed class FormOverlaysTests
{
    [Fact]
    public void Every_overlay_parses_and_has_a_valid_master_slot()
    {
        Assert.NotEmpty(FormOverlays.Names);
        foreach (var name in FormOverlays.Names)
        {
            var o = FormOverlays.Get(name, null)!.Value;
            var f = MetadataCompiler.Compile(name, name, o.Lst, o.Rows, []);
            Assert.Contains(f.FormType, new[] { "EDIT", "LIST", "DIALOG" });
            Assert.True(f.Master is not null || f.IsDialog, $"{name}: thiếu T0 hợp lệ (TN + PK).");
            Assert.True(HandCodedForms.Names.Contains(name), $"{name} không phải form viết tay: không cần tệp mô tả.");
            // Tệp mô tả không bao giờ chọn cột bí mật (mật khẩu, token, nội dung tệp đính kèm).
            foreach (var r in o.Rows.Where(r => r.SubID is "SE" or "EX" or "SE1" or "SE2"))
                Assert.DoesNotMatch(@"(?i)\.(password\d*|base64content\d*|datavalue)\b", r.KeyValue ?? "");
            foreach (var d in f.Details.Where(d => !d.Independent))
                Assert.True(f.IsEdit, $"{name}.{d.Id}: tab chi tiết nối chứng từ chỉ có ở form EDIT.");
        }
    }

    [Fact]
    public void Web_keys_compile_captions_independent_tabs_filters_and_module_prefix()
    {
        var eq = FormOverlays.Get("EQ_AllocateFrm", null)!.Value;
        var f = MetadataCompiler.Compile("EQ_AllocateFrm", "EQ_AllocateFrm", eq.Lst, eq.Rows, []);
        Assert.Equal("Mã phân bổ", f.Captions["F2:LinkItem"]);               // F2 trong tệp mô tả = tiêu đề cột (SubValue CV)
        Assert.Equal("PeriodID", f.Slots["T1"].MasterField);

        var fiscal = FormOverlays.Get("FiscalFrm", null)!.Value;
        var ff = MetadataCompiler.Compile("FiscalFrm", "FiscalFrm", fiscal.Lst, fiscal.Rows, []);
        Assert.Equal("LIST", ff.FormType);
        Assert.True(ff.Slots["T1"].Independent && ff.Slots["T2"].Independent);

        var bs = FormOverlays.Get("GJ_BalanceSheetSetupFrm", null)!.Value;
        var fb = MetadataCompiler.Compile("GJ_BalanceSheetSetupFrm", "GJ_BalanceSheetSetupFrm", bs.Lst, bs.Rows, bs.Filters);
        Assert.Equal(("GroupID", 3, "QD15"), (fb.Filters[0].Column, fb.Filters[0].Type, fb.Filters[0].DefaultValue));

        var perm = FormOverlays.Get("NguoiDungPhanQuyenFrm", null)!.Value;
        Assert.Contains(perm.Rows, r => r.KeyValue == "{Mod}_UserGroupPermisstion"); // ErpMetadata thay {Mod} bằng mã module đã kiểm
        Assert.Equal("GroupName", MetadataCompiler.Compile("P", "P", perm.Lst, perm.Rows, []).Slots["T1"].GroupBy);

        var lockDoc = FormOverlays.Get("SY_LockDocumentFrm", null)!.Value;
        Assert.StartsWith("(SELECT SourceID", MetadataCompiler.Compile("L", "L", lockDoc.Lst, lockDoc.Rows, []).Slots["T1"].FromOverride);
    }

    [Fact]
    public void Branch_filter_follows_winforms_dynamic_forms_only()
    {
        static FormMeta F(string name, params FrmCfgRow[] rows) =>
            MetadataCompiler.Compile(name, name, new FrmLstRow(name, "EDIT", name, "T", "ID", null, null, null, null, null), rows, []);
        Assert.True(ErpScreens.BranchFiltered(F("AP_PurposeRequestListFrm")));                                  // form động: GetBranchFilter
        Assert.False(ErpScreens.BranchFiltered(F("AP_PurposeRequestListFrm", new FrmCfgRow("SPA3", "", "1", null))));
        Assert.False(ErpScreens.BranchFiltered(F("CS_ReceiptFrm")));                                            // form viết tay: không lọc
        Assert.False(ErpScreens.BranchFiltered(F("CS_ReceiptFrm", new FrmCfgRow("SPA3", "", "0", null))));     // SPA3 của DB không áp
        var stock = FormOverlays.Get("AR_StockOutFrm", null)!.Value;                                             // form tự lọc trong mã: WBR
        Assert.True(ErpScreens.BranchFiltered(MetadataCompiler.Compile("AR_StockOutFrm", "AR_StockOutFrm", stock.Lst, stock.Rows, [])));
    }

    [Fact]
    public void Para_rows_win_and_database_config_stays_first()
    {
        var bank = FormOverlays.Get("CS_ReceiptFrm", "Bank")!.Value;
        // Cấu hình thật của khách (DB) đứng trước tệp mô tả; FTW theo Para không bị DB đè.
        FrmCfgRow[] db = [new("T0", "TN", "CS_ReceiptTbl", null), new("T0", "PK", "DocumentID", null), new("T0", "SO", "DocumentDate desc", null)];
        var f = MetadataCompiler.Compile("CS_ReceiptFrm", "CS_ReceiptFrm_Bank", bank.Lst, [.. db, .. bank.Rows], []);

        Assert.Equal("CS_ReceiptTbl.isBank = 1", f.Master!.FilterWeb);
        Assert.Equal("DocumentDate desc", f.Master.Sort);
        Assert.Equal(["T1", "T2", "T3", "T4"], f.Details.Select(d => d.Id));
        Assert.True(f.Slots["T3"].HideOnHideAmount); // Định khoản
        Assert.True(f.Slots["T4"].Optional);         // bảng lịch sử trạng thái có thể không có
        Assert.Equal(("ReceiptID", "Thông tin hóa đơn"), (f.Slots["T2"].DetailField, f.Slots["T2"].Caption));
        Assert.Equal("CS_ReceiptTbl.isBank = 0", FormOverlays.Get("CS_ReceiptFrm", null)!.Value.Rows.First(r => r.SubID == "FTW").KeyValue);
    }
}
