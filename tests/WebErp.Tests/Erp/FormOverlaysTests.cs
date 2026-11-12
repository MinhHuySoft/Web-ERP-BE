using WebErp.Erp.Metadata;

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
            Assert.True(f.Master is not null, $"{name}: thiếu T0 hợp lệ (TN + PK).");
            Assert.Equal("EDIT", f.FormType);
            Assert.True(HandCodedForms.Names.Contains(name), $"{name} không phải form viết tay: không cần tệp mô tả.");
        }
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
