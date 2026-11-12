using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Metadata;
using WebErp.Erp.Connection;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;
using WebErp.Erp.Security;

namespace WebErp.IntegrationTests;

/// <summary>Màn dựng từ metadata trên DB giả (cấu hình rút gọn từ MedData): SQL thật chạy trên SQL Server thật.</summary>
public sealed class ScreenReadTests(FakeErpDatabase db) : IClassFixture<FakeErpDatabase>
{
    private ErpScreens Screens()
    {
        var connections = new ErpConnectionFactory(new SingleTenantStore(db), new ErpOptions());
        return new ErpScreens(connections, new ErpMetadata(connections, TimeProvider.System), new ErpPermissions(connections), TimeProvider.System);
    }

    private ScreenRequest Req(string user, string? branch, string form, Dictionary<string, string>? query = null) =>
        new(db.Tenant, user, branch, form, null, query ?? []);

    [Fact]
    public async Task Plain_user_sees_own_documents_of_own_branch_in_default_date_range()
    {
        db.SkipIfUnavailable();
        var v = await Screens().OpenListAsync(Req("ketoan", "HN", "AP_PurposeRequestListFrm"), CancellationToken.None);

        Assert.Equal(ScreenStatus.Ok, v.Status);
        // PO/002 khác chi nhánh, PO/003 quá 30 ngày (SP=13, SPA=30), PO/004 của người khác.
        Assert.Equal(["PO/001"], v.Keys!);
        Assert.Equal(1, v.Total);
        Assert.Equal(["PurchaseRequestID", "PurchaseDate", "StatusID"], v.Columns!.Take(3).Select(c => c.Field));
        Assert.Equal("Ngày đề nghị", v.Columns!.Single(c => c.Field == "PurchaseDate").Title); // F0 đè SY_FmtFldTbl
        Assert.True(v.Columns!.Single(c => c.Field == "RateExchange").Hidden);
        // ketoan có isHideAmount: cột tiền (ACP) bị loại khỏi dữ liệu, không chỉ ẩn (QĐ-03 ngoại lệ 5).
        Assert.DoesNotContain(v.Columns!, c => c.Field == "Price");
        Assert.True(v.CanOpenRecord);
        var branch = v.Filters!.Single(f => f.Name == "BranchID");
        Assert.Equal("HN", branch.Value); // mặc định {BranchID} của người dùng
        Assert.Contains(("HCM", "HCM — Hồ Chí Minh"), branch.Options!);
    }

    [Fact]
    public async Task Admin_sees_everyone_and_filters_and_search_use_parameters()
    {
        db.SkipIfUnavailable();
        var all = await Screens().OpenListAsync(Req("Admin", null, "AP_PurposeRequestListFrm",
            new() { ["FromDate"] = "2000-01-01", ["BranchID"] = "" }), CancellationToken.None);
        Assert.Equal(["PO/004", "PO/003", "PO/002", "PO/001"], all.Keys!); // SO = PurchaseRequestID Desc
        var price = all.Columns!.Single(c => c.Field == "Price");
        Assert.Equal(("money", true), (price.Type, price.Sum));
        Assert.Equal(10000m, all.Sums!["Price"]);

        var hn = await Screens().OpenListAsync(Req("Admin", null, "AP_PurposeRequestListFrm",
            new() { ["FromDate"] = "2000-01-01", ["BranchID"] = "HN", ["q"] = "' OR 1=1 --" }), CancellationToken.None);
        Assert.Empty(hn.Keys!);
        var search = await Screens().OpenListAsync(Req("Admin", null, "AP_PurposeRequestListFrm",
            new() { ["FromDate"] = "2000-01-01", ["BranchID"] = "HN", ["q"] = "003" }), CancellationToken.None);
        Assert.Equal(["PO/003"], search.Keys!);
    }

    [Fact]
    public async Task Record_view_applies_the_same_row_filters_and_lazy_detail_joins_lookup_names()
    {
        db.SkipIfUnavailable();
        var s = Screens();
        Assert.Equal(ScreenStatus.NotFound, (await s.OpenRecordAsync(Req("ketoan", "HN", "AP_PurposeRequestListFrm"), "PO/004", CancellationToken.None)).Status);

        var rec = await s.OpenRecordAsync(Req("ketoan", "HN", "AP_PurposeRequestListFrm"), "PO/001", CancellationToken.None);
        Assert.Equal(ScreenStatus.Ok, rec.Status);
        var fields = Assert.Single(rec.Tabs!).Fields;
        // Nút, ô ẩn (LYT vị trí 18) và cột tiền khi người dùng bị ẩn tiền đều bị bỏ.
        Assert.Equal(["PurchaseRequestID", "PurchaseDate", "Notes"], fields.Select(f => f.Field));
        var admin = await s.OpenRecordAsync(Req("Admin", null, "AP_PurposeRequestListFrm"), "PO/001", CancellationToken.None);
        Assert.Equal("money", admin.Tabs![0].Fields.Single(f => f.Field == "Price").Control);
        var tab = Assert.Single(rec.Details!);
        Assert.Equal(("T1", "Hàng hóa đề nghị"), (tab.Id, tab.Title));

        var det = await s.OpenDetailAsync(Req("ketoan", "HN", "AP_PurposeRequestListFrm"), "PO/001", "T1", CancellationToken.None);
        Assert.Equal(ScreenStatus.Ok, det.Status);
        Assert.Equal(2, det.Rows!.Count);
        Assert.Equal(["ItemID", "ItemName", "Unit", "Quantity"], det.Columns!.Take(4).Select(c => c.Field));
        Assert.Equal("Vật tư một", det.Rows[0][1]);
        Assert.Equal(5.5m, det.Sums!["Quantity"]);
    }

    [Fact]
    public async Task List_form_without_T0_falls_back_to_SY_FrmLstTbl()
    {
        db.SkipIfUnavailable();
        var v = await Screens().OpenListAsync(Req("ketoan", "HN", "CF_BranchListFrm"), CancellationToken.None);
        Assert.Equal(ScreenStatus.Ok, v.Status);
        Assert.Equal(["HN"], v.Keys!); // bảng có BranchID: lọc chi nhánh của người dùng như WinForms
        Assert.False(v.CanOpenRecord); // LIST không có màn chứng từ
    }

    [Fact]
    public async Task Metadata_that_writes_is_refused_and_nothing_is_written()
    {
        db.SkipIfUnavailable();
        var v = await Screens().OpenListAsync(Req("ketoan", "HN", "BAD_ConfigFrm"), CancellationToken.None);
        Assert.Equal(ScreenStatus.ConfigError, v.Status);
        Assert.Contains("FTX", v.Note);

        // Hook ESL (DELETE) của form thật không bao giờ chạy; bảng canary còn nguyên.
        await Screens().OpenListAsync(Req("ketoan", "HN", "AP_PurposeRequestListFrm"), CancellationToken.None);
        await using var c = new SqlConnection(db.ConnectionString);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.WEB_Canary"));
    }

    [Fact]
    public async Task Hand_coded_and_unknown_forms_are_not_supported()
    {
        db.SkipIfUnavailable();
        Assert.Equal(ScreenStatus.NotSupported, (await Screens().OpenListAsync(Req("ketoan", "HN", "CS_ReceiptFrm"), CancellationToken.None)).Status);
        Assert.Equal(ScreenStatus.NotSupported, (await Screens().OpenListAsync(Req("ketoan", "HN", "Khong_Co_Frm"), CancellationToken.None)).Status);
    }
}
