using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Metadata;
using WebErp.Core.Print;
using WebErp.Erp.Connection;
using WebErp.Erp.Metadata;
using WebErp.Erp.Print;
using WebErp.Erp.Screens;
using WebErp.Erp.Security;
using WebErp.Erp.WebSchema;

namespace WebErp.IntegrationTests;

/// <summary>In theo mẫu trên DB giả (SQL thật): SP báo cáo chạy trong giao dịch luôn rollback, mẫu mới nhất, kho mẫu web có phiên bản.</summary>
public sealed class PrintStoreTests(FakeErpDatabase db) : IClassFixture<FakeErpDatabase>
{
    private const string Json =
        """{"$schema":"erp-print-template/1","id":"AP_PurposeRequestListReport","unit":"mm","page":{"width":210,"height":297,"margin":{"top":10,"right":10,"bottom":10,"left":10},"bodyWidth":190},"groups":[],"bands":[{"id":"Detail","type":"detail","height":6,"controls":[{"id":"txt1","type":"text","x":0,"y":0,"w":30,"h":5,"value":{"sys":"SysCom1"}}]}],"assets":{}}""";

    private (ErpPrint Print, ErpConnectionFactory Connections) Create()
    {
        var cx = new ErpConnectionFactory(new SingleTenantStore(db), new ErpOptions());
        var meta = new ErpMetadata(cx, TimeProvider.System);
        var perms = new ErpPermissions(cx);
        return (new ErpPrint(cx, meta, new ErpScreens(cx, meta, perms, TimeProvider.System), perms, new ErpOptions()), cx);
    }

    private ScreenRequest Req(string user = "Admin", string? branch = null) =>
        new(db.Tenant, user, branch, "AP_PurposeRequestListFrm", null, new Dictionary<string, string>());

    [Fact]
    public async Task Report_sp_runs_read_only_and_web_versions_take_over_after_save()
    {
        db.SkipIfUnavailable();
        var (print, cx) = Create();
        var ct = CancellationToken.None;
        await using var c = new SqlConnection(db.ConnectionString);

        Assert.Equal("AP_PurposeRequestListReport", await print.ReportOfAsync(Req(), ct));
        var v = await print.PrepareAsync(Req(), ["PO/001"], null, new Dictionary<string, string> { ["UInput1"] = "Lê Thu" }, ct);
        Assert.Equal(ScreenStatus.Ok, v.Status);
        Assert.Equal(["AP_PurposeRequestListReport", "AP_PRCopyReport"], v.Templates!.Select(t => t.File));
        Assert.Equal(("AP_PurposeRequestListReport", "rpx", "r20260101000000"), (v.Template, v.TemplateFormat, v.TemplateVersion));
        Assert.Equal(2, v.Sets![0].Rows.Count); // hai dòng chi tiết của PO/001
        var cols = v.Sets[0].Columns.Select(x => x.Field).ToList();
        Assert.Equal("AP_PurposeRequestListReport", v.Sets[0].Rows[0][cols.IndexOf("Template")]);
        Assert.Equal("Admin", v.Sets[0].Rows[0][cols.IndexOf("UserName")]);
        Assert.Equal(["X"], v.Sets[1].Columns.Select(x => x.Field)); // cột dạng mật khẩu bị bỏ
        Assert.Contains(v.Messages!, m => m.Text == "Đã in thử");
        Assert.Contains(v.Params!, p => p.Name == "Sys.SysCom1" && (string?)p.Value == "Công ty giả");
        Assert.Contains(v.Params!, p => p.Name == "UInput1" && (string?)p.Value == "Lê Thu" && p.Caption == "Người lập");
        Assert.DoesNotContain(v.Params!, p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        Assert.True(v.CanDesign);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.WEB_Canary")); // SP ghi nhưng đã rollback

        // Không in được chứng từ ngoài quyền xem (PO/004 của người khác) dù SP nhận được mã.
        Assert.Equal(ScreenStatus.NotFound, (await print.PrepareAsync(Req("ketoan", "HN"), ["PO/004"], null, new Dictionary<string, string>(), ct)).Status);

        var rpx = await print.TemplateAsync(Req(), "AP_PurposeRequestListReport", ct);
        Assert.Equal(("db", "rpx"), (rpx!.Source, rpx.Format));
        Assert.Contains("SysCom1", System.Text.Encoding.UTF8.GetString(rpx.Content)); // bản mới nhất
        Assert.Null(await print.TemplateAsync(Req(), "Khac", ct));

        // Chưa có kho mẫu web: lưu báo rõ, không tự tạo bảng.
        var noStore = await print.SaveTemplateAsync(Req(), "AP_PurposeRequestListReport", Json, null, ct);
        Assert.False(noStore.Ok);
        Assert.Contains("tenant migrate", noStore.Errors![0]);

        await new WebSchemaMigrator(cx).ApplyAsync(db.Tenant, "test", ct);
        var s1 = await print.SaveTemplateAsync(Req(), "AP_PurposeRequestListReport", Json, "Lần đầu", ct);
        var s2 = await print.SaveTemplateAsync(Req(), "AP_PurposeRequestListReport", Json.Replace("\"w\":30", "\"w\":40"), null, ct);
        Assert.Equal((true, 1, true, 2), (s1.Ok, s1.VersionNo, s2.Ok, s2.VersionNo));
        var versions = (await c.QueryAsync<(int No, string Kind, string? Comment, string By)>(
            "SELECT v.VersionNo, v.SourceKind, v.Comment, v.CreatedBy FROM web.PrintTemplateVersion v ORDER BY v.VersionNo")).ToList();
        Assert.Equal([(1, "rpx", "Lần đầu", "Admin"), (2, "web", null, "Admin")], versions);

        var after = await print.PrepareAsync(Req(), ["PO/001"], null, new Dictionary<string, string>(), ct);
        Assert.Equal(("json", "w2"), (after.TemplateFormat, after.TemplateVersion));
        Assert.Contains(after.Params!, p => p.Name == "Sys.SysCom1");
        Assert.Contains(after.Messages!, m => m.Text.Contains("mẫu sửa trên web"));
        var json = await print.TemplateAsync(Req(), "AP_PurposeRequestListReport", ct);
        Assert.Equal(("web", "json"), (json!.Source, json.Format));
        Assert.Contains("\"w\":40", System.Text.Encoding.UTF8.GetString(json.Content));

        // Kiểm lại ở BE + quyền EPER (mẫu bản sao: EPER = 2, chỉ Admin của form).
        Assert.False((await print.SaveTemplateAsync(Req(), "AP_PurposeRequestListReport", "{\"$schema\":\"khac\"}", null, ct)).Ok);
        Assert.False((await print.SaveTemplateAsync(Req(), "KhongThuocBaoCao", Json, null, ct)).Ok);
        var denied = await print.SaveTemplateAsync(Req("ketoan", "HN"), "AP_PRCopyReport", Json, null, ct);
        Assert.False(denied.Ok);
        Assert.Contains("quyền", denied.Errors![0]);
        Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.WEB_Canary"));
    }
}
