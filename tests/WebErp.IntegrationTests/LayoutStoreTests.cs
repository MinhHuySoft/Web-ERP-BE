using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Metadata;
using WebErp.Erp.Connection;
using WebErp.Erp.Layout;
using WebErp.Erp.Metadata;
using WebErp.Erp.Security;
using WebErp.Erp.WebSchema;

namespace WebErp.IntegrationTests;

/// <summary>
/// Vẽ layout trên DB giả (SQL thật): lưu ghi SY_FrmCfg như LayoutSettingFrm (chỉ dòng bố cục), có phiên bản trước / sau, chặn khi bố cục
/// đã bị đổi, chỉ Admin, khôi phục đúng nguyên trạng, bộ đệm metadata được xóa.
/// </summary>
public sealed class LayoutStoreTests(FakeErpDatabase db) : IClassFixture<FakeErpDatabase>
{
    private (ErpLayout Layout, ErpMetadata Meta, ErpConnectionFactory Cx) Create()
    {
        var cx = new ErpConnectionFactory(new SingleTenantStore(db), new ErpOptions());
        var meta = new ErpMetadata(cx, TimeProvider.System);
        return (new ErpLayout(cx, meta, new ErpPermissions(cx), new ErpOptions(), TimeProvider.System), meta, cx);
    }

    private ScreenRequest Req(string user = "Admin") => new(db.Tenant, user, null, "AP_PurposeRequestListFrm", null, new Dictionary<string, string>());

    private static async Task<List<(string Id, string Key, string Sub, string? Value, string? Order)>> RowsAsync(SqlConnection c) =>
        (await c.QueryAsync<(string, string, string, string?, string?)>(
            "SELECT UserAutoID, KeyID, ISNULL(SubID,''), KeyValue, SubValue FROM dbo.SY_FrmCfg WHERE FID = 'AP_PurposeRequestListFrm' ORDER BY UserAutoID")).ToList();

    private static string Revision(ErpLayout layout, ScreenRequest req)
    {
        using var d = JsonDocument.Parse(layout.OpenAsync(req, CancellationToken.None).GetAwaiter().GetResult().Json!);
        return d.RootElement.GetProperty("revision").GetString()!;
    }

    [Fact]
    public async Task Save_writes_layout_rows_only_versions_and_restores()
    {
        db.SkipIfUnavailable();
        var (layout, meta, cx) = Create();
        var ct = CancellationToken.None;
        await using var c = new SqlConnection(db.ConnectionString);
        var original = await RowsAsync(c);

        var open = await layout.OpenAsync(Req(), ct);
        Assert.Equal(ScreenStatus.Ok, open.Status);
        Assert.False(open.CanSave); // chưa có bảng sao lưu
        using var doc = JsonDocument.Parse(open.Json!);
        var root = doc.RootElement;
        Assert.Equal("erp-form-layout/1", root.GetProperty("$schema").GetString());
        Assert.Equal(["PurchaseRequestID", "PurchaseDate", "CommandButtonCtl", "Notes", "Price", "RateExchange"],
            root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("name").GetString()));
        var fields = root.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("type").GetString());
        Assert.Equal(("date", "number"), (fields["PurchaseDate"], fields["Price"]));
        Assert.DoesNotContain(fields.Keys, k => k.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Hàng hóa đề nghị", root.GetProperty("tabs").EnumerateArray().Single(t => t.GetProperty("id").GetString() == "5").GetProperty("caption").GetString());
        var revision = root.GetProperty("revision").GetString();

        string Json(string rev, string items, string captions = "{}") =>
            $$"""{"$schema":"erp-form-layout/1","form":"AP_PurposeRequestListFrm","revision":"{{rev}}","captions":{{captions}},"items":[{{items}}]}""";
        const string newLayout =
            """
            {"tab":"1","group":"","name":"PurchaseDate","cfg":"4;4;1;1;;;;;;;;;;;;;;;;0;0"},
            {"tab":"1","group":"","name":"PurchaseRequestID","cfg":"1;6;1;;;;;;;;;;;;;;;;;0;0"},
            {"tab":"1","group":"","name":"GrpBox1","cfg":"23;20;3;1;;;;;;;;;;;;;;;;0;0"},
            {"tab":"1","group":"GrpBox1","name":"Price","cfg":"5;8;1;1;;;;;;;;;;;;;;;;0;0"},
            {"tab":"2","group":"","name":"Notes","cfg":"1;99;2;1;;;;Ghi chú:dài;;;;;;;;;;;;0;0"}
            """;

        // Chưa có bảng sao lưu: không ghi gì.
        var noStore = await layout.SaveAsync(Req(), Json(revision!, newLayout), null, ct);
        Assert.False(noStore.Ok);
        Assert.Contains("tenant migrate", noStore.Errors![0]);
        Assert.Equal(original, await RowsAsync(c));

        await new WebSchemaMigrator(cx).ApplyAsync(db.Tenant, "test", ct);
        Assert.True((await layout.OpenAsync(Req(), ct)).CanSave);
        Assert.False((await layout.OpenAsync(Req("ketoan"), ct)).CanSave);
        var denied = await layout.SaveAsync(Req("ketoan"), Json(revision!, newLayout), null, ct);
        Assert.Contains("Admin", denied.Errors![0]);
        Assert.False((await layout.SaveAsync(Req(), Json("cu", newLayout), null, ct)).Ok is true);
        Assert.True((await layout.SaveAsync(Req(), Json("cu", newLayout), null, ct)).Conflict);

        var form0 = await meta.GetFormAsync(db.Tenant, "AP_PurposeRequestListFrm", "AP_PurposeRequestListFrm", ct); // đưa vào bộ đệm
        Assert.Equal("1", form0!.Layout.Single(l => l.Field == "Notes").Tab);
        var saved = await layout.SaveAsync(Req(), Json(revision!, newLayout, """{"T2V":"Khác"}"""), "Thử", ct);
        Assert.True(saved.Ok, string.Join(" | ", saved.Errors ?? []));
        Assert.Equal(1, saved.VersionNo);

        var rows = await RowsAsync(c);
        string? Order(string key, string sub) => rows.Single(r => r.Key == key && r.Sub == sub).Order;
        Assert.Equal(("001", "002", "003"), (Order("LYT1", "PurchaseDate"), Order("LYT1", "PurchaseRequestID"), Order("LYT1", "GrpBox1")));
        Assert.Equal(("001", "001"), (Order("LYT1GrpBox1", "Price"), Order("LYT2", "Notes")));
        Assert.Equal("c43", rows.Single(r => r.Sub == "Notes" && r.Key == "LYT2").Id); // giữ UserAutoID như WinForms
        Assert.Equal("Khác", rows.Single(r => r.Key == "T2V").Value);
        Assert.DoesNotContain(rows, r => r.Sub is "CommandButtonCtl" or "RateExchange");
        // Dòng không phải bố cục giữ nguyên.
        Assert.Equal(original.Where(r => !r.Key.StartsWith("LY", StringComparison.Ordinal)), rows.Where(r => !r.Key.StartsWith("LY", StringComparison.Ordinal) && r.Key != "T2V"));
        Assert.Equal(25, rows.Single(r => r.Key == "T2V").Id.Length);
        var v = await c.QuerySingleAsync<(string Kind, string Comment, string By, string Before)>(
            "SELECT Kind, Comment, CreatedBy, BeforeJson FROM web.FormLayoutVersion WHERE FID = 'AP_PurposeRequestListFrm' AND VersionNo = 1");
        Assert.Equal(("save", "Thử", "Admin"), (v.Kind, v.Comment, v.By));
        Assert.Contains("RateExchange", v.Before);
        // Bộ đệm metadata đã xóa: lần mở sau thấy bố cục mới.
        var form1 = await meta.GetFormAsync(db.Tenant, "AP_PurposeRequestListFrm", "AP_PurposeRequestListFrm", ct);
        Assert.Equal("2", form1!.Layout.Single(l => l.Field == "Notes").Tab);
        Assert.Contains(form1.Layout, l => l.Field == "Price"); // ô trong nhóm được trải phẳng như trước

        // Lưu lại đúng bố cục đang có: không đổi dòng nào nhưng vẫn ghi phiên bản.
        using var doc2 = JsonDocument.Parse((await layout.OpenAsync(Req(), ct)).Json!);
        var same = await layout.SaveAsync(Req(), Json(doc2.RootElement.GetProperty("revision").GetString()!, newLayout, """{"T2V":"Khác"}"""), null, ct);
        Assert.Equal((true, 2, 0), (same.Ok, same.VersionNo, same.Changed));

        // Khôi phục về trước lần 1: dòng bố cục trở lại nguyên trạng (UserAutoID, khóa, giá trị, thứ tự).
        var back = await layout.RestoreAsync(Req(), 1, null, ct);
        Assert.True(back.Ok, string.Join(" | ", back.Errors ?? []));
        Assert.Equal(3, back.VersionNo);
        Assert.Equal(original, await RowsAsync(c));
        Assert.Equal((("restore", 1)), await c.QuerySingleAsync<(string, int)>(
            "SELECT Kind, RestoredFrom FROM web.FormLayoutVersion WHERE FID = 'AP_PurposeRequestListFrm' AND VersionNo = 3"));
        Assert.False((await layout.RestoreAsync(Req(), 99, null, ct)).Ok);

        // GĐ2: thuộc tính LYS (mã cho phép), đổi tên control, tiêu đề / quyền xem trang chi tiết; mã có SQL bị chặn.
        using (var d = JsonDocument.Parse((await layout.OpenAsync(Req(), ct)).Json!))
        {
            var t5 = d.RootElement.GetProperty("tabs").EnumerateArray().Single(t => t.GetProperty("id").GetString() == "5");
            Assert.Equal(("Hàng hóa đề nghị", true, "0"), (t5.GetProperty("dcp").GetString(), t5.GetProperty("dcpEditable").GetBoolean(), t5.GetProperty("visible").GetString()));
            Assert.Contains("BT", d.RootElement.GetProperty("editableProps").GetProperty("22").EnumerateArray().Select(x => x.GetString()));
        }
        const string gd2 =
            """
            {"tab":"1","group":"","name":"PurchaseRequestID","cfg":"1;4;1;1;;;;;;;;;;;;;;;;0;0","props":{}},
            {"tab":"1","group":"","name":"PurchaseDate","cfg":"4;4;1;;;;;;;;;;;;;;;;;0;0","props":{"MXR":"1"}},
            {"tab":"1","group":"","name":"CommandButtonCtl_1","from":"CommandButtonCtl","cfg":"22;4;1;;;;;;;;;;;;;;;;;0;0","props":{"BT":"Gửi đề nghị"}},
            {"tab":"1","group":"","name":"Notes","cfg":"1;99;2;1;;;;;;;;;;;;;;;;0;0","props":{}},
            {"tab":"1","group":"","name":"Price","cfg":"5;8;1;1;;;;;;;;;;;;;;;;0;0","props":{}},
            {"tab":"1","group":"","name":"RateExchange","cfg":"5;8;1;;;;;;;;;;;;;;;;1;0;0","props":{}}
            """;
        string Gd2(string props) => "{\"$schema\":\"erp-form-layout/1\",\"form\":\"AP_PurposeRequestListFrm\",\"revision\":\"" + Revision(layout, Req())
            + "\",\"pages\":{\"1\":{\"caption\":\"Hàng đề nghị\",\"visible\":\"4\"}},\"items\":["
            + gd2.Replace("{\"BT\":\"Gửi đề nghị\"}", props, StringComparison.Ordinal) + "]}";
        var sqlProp = await layout.SaveAsync(Req(), Gd2("{\"BT\":\"x\",\"LBS\":\"SELECT 1\"}"), null, ct);
        Assert.False(sqlProp.Ok);
        Assert.Contains("LBS", sqlProp.Errors![0]);
        var g = await layout.SaveAsync(Req(), Gd2("{\"BT\":\"Gửi đề nghị\"}"), "GĐ2", ct);
        Assert.True(g.Ok, string.Join(" | ", g.Errors ?? []));
        var r2 = await RowsAsync(c);
        Assert.Equal("c42", r2.Single(r => r.Sub == "CommandButtonCtl_1" && r.Key == "LYT1").Id); // đổi tên giữ UserAutoID
        Assert.Equal("Gửi đề nghị", r2.Single(r => r.Key == "LYS1" && r.Sub == "CommandButtonCtl_1" && r.Order == "BT").Value);
        Assert.Equal("1", r2.Single(r => r.Key == "LYS1" && r.Sub == "PurchaseDate" && r.Order == "MXR").Value);
        Assert.Equal(("c28", "Hàng đề nghị"), r2.Where(r => r.Key == "T1" && r.Sub == "DCP").Select(r => (r.Id, r.Value)).Single());
        Assert.Equal("4", r2.Single(r => r.Key == "TPV1").Value);
        var form2 = await meta.GetFormAsync(db.Tenant, "AP_PurposeRequestListFrm", "AP_PurposeRequestListFrm", ct);
        Assert.Equal("Gửi đề nghị", form2!.Layout.Single(l => l.Field == "CommandButtonCtl_1").ButtonText);
        Assert.Equal(4, form2.SettingInt("TPV1", 0));

        // Nguồn chép bố cục: danh sách form và bố cục một form; người không phải Admin không xem được.
        Assert.Contains("\"fid\":\"AP_PurposeRequestListFrm\"", await layout.SourceAsync(Req(), null, ct));
        Assert.Contains("\"name\":\"CommandButtonCtl_1\"", await layout.SourceAsync(Req(), "AP_PurposeRequestListFrm", ct));
        Assert.Null(await layout.SourceAsync(Req("ketoan"), null, ct));

        // Khôi phục về trước lần GĐ2: tên, thuộc tính, tiêu đề, quyền xem trang trở lại như cũ.
        Assert.True((await layout.RestoreAsync(Req(), g.VersionNo, null, ct)).Ok);
        Assert.Equal(original, await RowsAsync(c));
    }
}
