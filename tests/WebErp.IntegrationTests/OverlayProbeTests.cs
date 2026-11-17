using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Erp.Connection;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;

namespace WebErp.IntegrationTests;

/// <summary>Nút "Kiểm trên DB công ty" và danh sách "Form còn thiếu" của trang quản trị, chạy trên DB giả.</summary>
public sealed class OverlayProbeTests(FakeErpDatabase db) : IClassFixture<FakeErpDatabase>
{
    private OverlayProbe Probe()
    {
        var connections = new ErpConnectionFactory(new SingleTenantStore(db), new ErpOptions());
        return new OverlayProbe(connections, new ErpMetadata(connections, TimeProvider.System));
    }

    [Fact]
    public async Task Check_reports_columns_dropped_columns_and_missing_tables_without_reading_rows()
    {
        db.SkipIfUnavailable();
        const string json = """
            {
              "form": "XItemFrm", "formType": "EDIT", "caption": "Vật tư",
              "cfg": {
                "T0": { "TN": "CF_ItemTbl", "PK": "ItemID", "SE": "CF_ItemTbl.ItemID, CF_ItemTbl.ItemName, CF_ItemTbl.KhongCo", "FTX": "CF_ItemTbl.Unit <> {User}" },
                "T1": { "TN": "BangKhongCo", "PK": "ID", "MUL": "ItemID", "MDL": "ItemID" }
              }
            }
            """;
        var r = await Probe().CheckAsync(db.Tenant, json, null, CancellationToken.None);

        Assert.Null(r.Error);
        Assert.False(r.Ok);
        var t0 = r.Slots.Single(s => s.Slot == "T0");
        Assert.Null(t0.Error);
        Assert.Equal(["ItemID", "ItemName"], t0.Columns);
        Assert.Equal(["CF_ItemTbl.KhongCo"], t0.Dropped);
        Assert.Contains("thiếu bảng BangKhongCo", r.Slots.Single(s => s.Slot == "T1").Error);
    }

    [Fact]
    public async Task Check_rejects_writes_and_reports_sql_errors_and_never_writes()
    {
        db.SkipIfUnavailable();
        await using var c = new SqlConnection(db.ConnectionString);
        var before = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.WEB_Canary");

        var write = await Probe().CheckAsync(db.Tenant,
            """{ "form": "XFrm", "formType": "LIST", "caption": "X", "cfg": { "T0": { "TN": "CF_ItemTbl", "PK": "ItemID", "FTX": "1=1; INSERT dbo.WEB_Canary VALUES (99)" } } }""",
            null, CancellationToken.None);
        Assert.Contains("nhiều câu lệnh", write.Slots.Single().Error);

        var broken = await Probe().CheckAsync(db.Tenant,
            """{ "form": "XFrm", "formType": "LIST", "caption": "X", "cfg": { "T0": { "TN": "CF_ItemTbl", "PK": "ItemID", "EX": "CF_ItemTbl.ItemName + AS Hong" } } }""",
            null, CancellationToken.None);
        Assert.StartsWith("SQL lỗi", broken.Slots.Single().Error);

        Assert.Contains("JSON sai", (await Probe().CheckAsync(db.Tenant, "{ hỏng", null, CancellationToken.None)).Error);
        Assert.Equal(before, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.WEB_Canary"));
    }

    [Fact]
    public async Task Menu_gaps_list_forms_the_web_cannot_build()
    {
        db.SkipIfUnavailable();
        var r = await Probe().MenuGapsAsync(db.Tenant, CancellationToken.None);

        // Đăng xuất (lệnh) và Sổ quỹ (báo cáo) không phải form: chỉ đếm, không liệt kê.
        Assert.DoesNotContain(r.Gaps, g => g.Form is "LogOff" or "CS_CashBookReport");
        Assert.Equal(1, r.SkippedCommands);
        Assert.True(r.SkippedReports >= 1);
        Assert.DoesNotContain(r.Gaps, g => g.Form is "NguoiDungFrm" or "CS_ReceiptFrm" or "AP_PurposeRequestListFrm"); // có mô tả / có cấu hình
    }
}
