using WebErp.Admin.Web;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;

namespace WebErp.Tests.Erp;

/// <summary>"Tạo nhanh": sinh JSON mô tả từ lựa chọn, đoán bảng theo tên form, đọc gợi ý của AI.</summary>
public sealed class QuickSpecTests
{
    [Fact]
    public void Generated_json_is_a_valid_overlay_with_master_detail_link()
    {
        var spec = new QuickSpec("AR_OrderFrm", "Quản lý đặt hàng", "EDIT", 500,
            new QuickSlot("AR_OrderTbl", "DocumentID", ["DocumentID", "DocumentDate", "ObjectName"], "DocumentDate", true),
            [new QuickSlot("AR_OrderDetailTbl", "UserAutoID", ["ItemID", "Quantity"], null, true, "Hàng hóa", "DocumentID", "DocumentID")]);
        var json = spec.ToJson();

        var v = FormOverlays.Validate(json);
        Assert.Null(v.Error);
        Assert.Equal("AR_OrderFrm", v.Form);
        Assert.Contains("\"SO\": \"AR_OrderTbl.DocumentDate DESC\"", json);
        Assert.Contains("\"MUL\": \"DocumentID\"", json);
        Assert.Contains("\"GD2\": \"ItemID;Quantity\"", json);
        Assert.Contains("\"SE\": \"AR_OrderDetailTbl.UserAutoID, AR_OrderDetailTbl.DocumentID, AR_OrderDetailTbl.ItemID, AR_OrderDetailTbl.Quantity\"", json);
        Assert.Contains("Quản lý đặt hàng", json);   // không mã hóa \uXXXX
    }

    [Fact]
    public void Odd_names_are_bracketed() =>
        Assert.Equal("[My Table].[Tên cột]", QuickSpec.Qualified("My Table", "Tên cột"));

    [Fact]
    public void Guess_follows_erp_naming()
    {
        string[] objects = ["AR_InvoiceTbl", "AR_OrderDetailTbl", "AR_OrderTbl", "AR_OrderView", "CF_ItemTbl", "SY_User"];
        var g = QuickGuesser.Guess("AR_OrderFrm", objects);
        Assert.Equal("AR_OrderTbl", g.MasterTable);
        Assert.Equal(["AR_OrderDetailTbl"], g.DetailTables);
        Assert.Equal("AR_OrderTbl", g.Candidates[0]);
        Assert.DoesNotContain("SY_User", g.Candidates);

        Assert.Equal("CS_Input", QuickGuesser.Base("CS_InputFrm"));
        Assert.Equal("GJ_DocBatchSetup", QuickGuesser.Base("GJ_DocBatchSetupfrm"));
        Assert.Null(QuickGuesser.Guess("XYZ_KhongCoFrm", objects).MasterTable);
    }

    [Fact]
    public void Ai_answer_keeps_only_real_tables_and_columns()
    {
        IReadOnlyList<AiTable> tables =
        [
            new("AR_OrderTbl", [new("DocumentID", "varchar", true, null), new("DocumentDate", "datetime", false, "Ngày"), new("Notes", "nvarchar", false, null)]),
            new("AR_OrderDetailTbl", [new("UserAutoID", "varchar", true, null), new("DocumentID", "varchar", false, null), new("ItemID", "varchar", false, null)]),
        ];
        const string answer = """
            Đây là gợi ý:
            {"caption": "Đặt hàng", "master": {"table": "ar_ordertbl", "primaryKey": "DocumentID", "columns": ["DocumentDate", "KhongCo", "Notes"], "sortColumn": "DocumentDate", "sortDesc": true},
             "details": [{"table": "AR_OrderDetailTbl", "primaryKey": "UserAutoID", "masterKey": "DocumentID", "detailKey": "DocumentID", "caption": "Chi tiết", "columns": ["ItemID"]},
                         {"table": "BangBia", "primaryKey": "ID", "masterKey": "DocumentID", "detailKey": "X", "columns": []}],
             "note": "Theo tên."}
            """;
        var s = AiAssistant.Parse(answer, tables)!;
        Assert.Equal("AR_OrderTbl", s.Master!.Table);                 // tên chuẩn theo DB
        Assert.Equal(["DocumentDate", "Notes"], s.Master.Columns);     // cột bịa bị bỏ
        Assert.Equal("AR_OrderDetailTbl", Assert.Single(s.Details).Table);
        Assert.Null(AiAssistant.Parse("không phải JSON", tables));
        Assert.Null(AiAssistant.Parse("""{"master": {"table": "BangBia"}}""", tables));
    }

    [Fact]
    public void Ai_prompt_has_schema_only()
    {
        var prompt = AiAssistant.Prompt("AR_OrderFrm", "Quản lý đặt hàng", [new("AR_OrderTbl", [new("DocumentID", "varchar", true, "Số chứng từ")])]);
        Assert.Contains("AR_OrderFrm", prompt);
        Assert.Contains("DocumentID varchar PK \"Số chứng từ\"", prompt);
    }
}
