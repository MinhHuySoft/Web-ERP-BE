using WebErp.Erp.Connection;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;

namespace WebErp.IntegrationTests;

/// <summary>"Tạo nhanh" trên DB giả: đọc cấu trúc, cột nối, tên menu, xem thử dữ liệu thật (chỉ đọc).</summary>
public sealed class SchemaExplorerTests(FakeErpDatabase db) : IClassFixture<FakeErpDatabase>
{
    private SchemaExplorer Explorer()
    {
        var connections = new ErpConnectionFactory(new SingleTenantStore(db), new ErpOptions());
        return new SchemaExplorer(connections, new ErpMetadata(connections, TimeProvider.System));
    }

    [Fact]
    public async Task Reads_tables_columns_keys_link_and_menu_caption()
    {
        db.SkipIfUnavailable();
        var x = Explorer();
        var objects = await x.ObjectsAsync(db.Tenant, CancellationToken.None);
        Assert.Contains("AP_PurchaseRequestTbl", objects);

        var cols = await x.ColumnsAsync(db.Tenant, "AP_PurchaseRequestTbl", "AP_PurchaseRequestFrm", CancellationToken.None);
        Assert.True(cols.Single(c => c.Name == "PurchaseRequestID").IsPrimaryKey);
        Assert.DoesNotContain(await x.ColumnsAsync(db.Tenant, "SY_User", "NguoiDungFrm", CancellationToken.None), c => c.Name == "Password"); // cột bí mật

        var link = await x.LinkAsync(db.Tenant, "AP_PurchaseRequestTbl", "AP_PurchaseRequestDetailTbl", "PurchaseRequestID", CancellationToken.None);
        Assert.Equal(("PurchaseRequestID", "PurchaseRequestID"), link);
        Assert.Equal("Phiếu thu", await x.MenuCaptionAsync(db.Tenant, "CS_ReceiptFrm", CancellationToken.None));
    }

    [Fact]
    public async Task Preview_reads_a_few_real_rows_and_the_details_of_the_first()
    {
        db.SkipIfUnavailable();
        var spec = new QuickSpec("XRequestFrm", "Đề nghị", "EDIT", 500,
            new QuickSlot("AP_PurchaseRequestTbl", "PurchaseRequestID", ["PurchaseRequestID", "PurchaseDate", "Notes"], "PurchaseRequestID", false),
            [new QuickSlot("AP_PurchaseRequestDetailTbl", "UserAutoID", ["ItemID", "Quantity"], null, true, "Chi tiết", "PurchaseRequestID", "PurchaseRequestID")]);
        var p = await Explorer().PreviewAsync(db.Tenant, spec, CancellationToken.None);

        var master = p.Single(t => t.Slot == "T0");
        Assert.Null(master.Error);
        Assert.Equal("PO/001", master.Rows[0][0]);                       // sắp xếp tăng dần theo khóa
        Assert.True(master.Rows.Count <= SchemaExplorer.PreviewRows);
        Assert.Null(p.Single(t => t.Slot == "T1").Error);
    }
}
