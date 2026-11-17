using WebErp.Core.Metadata;
using WebErp.Core.Tenancy;
using WebErp.Erp.Metadata;

namespace WebErp.Tests.Erp;

/// <summary>Gộp tệp mô tả nhúng với mô tả sửa trên trang quản trị, và kiểm tệp mô tả trước khi lưu.</summary>
public sealed class OverlayCatalogTests
{
    private static readonly Tenant Tp = new("TP", "Tín Phong", "SY", true, null, null, null, null);
    private static readonly Tenant Mc = new("MC", "Medcom", "SY", true, null, null, null, null);

    private static string Json(string form, string caption, string table = "XTbl") =>
        $$"""{ "form": "{{form}}", "formType": "LIST", "caption": "{{caption}}", "cfg": { "T0": { "TN": "{{table}}", "PK": "ID" } } }""";

    private sealed class Store : IFormOverlayStore
    {
        public FormOverlaySnapshot Snapshot { get; set; } = FormOverlaySnapshot.Empty;

        public ValueTask<FormOverlaySnapshot> GetFormOverlaysAsync(CancellationToken ct) => ValueTask.FromResult(Snapshot);
    }

    [Fact]
    public async Task Without_store_only_built_in_files_apply()
    {
        var set = await new OverlayCatalog().ForTenantAsync(Tp, CancellationToken.None);
        Assert.Equal(OverlaySource.BuiltIn, set.SourceOf("UserLogFrm"));
        Assert.Equal("0", set.Stamp);
        Assert.False(set.IsHandCoded("XFrm"));
    }

    [Fact]
    public async Task Company_overlay_beats_global_which_beats_built_in()
    {
        var store = new Store
        {
            Snapshot = new FormOverlaySnapshot(7,
            [
                new FormOverlayRecord("UserLogFrm", null, Json("UserLogFrm", "chung"), 1),
                new FormOverlayRecord("UserLogFrm", "TP", Json("UserLogFrm", "riêng TP"), 3),
                new FormOverlayRecord("XFrm", null, Json("XFrm", "form mới"), 1),
            ]),
        };
        var catalog = new OverlayCatalog(store);
        var tp = await catalog.ForTenantAsync(Tp, CancellationToken.None);
        var mc = await catalog.ForTenantAsync(Mc, CancellationToken.None);

        Assert.Equal("riêng TP", tp.Get("UserLogFrm", null)!.Value.Lst.CaptionVN);
        Assert.Equal(OverlaySource.Tenant, tp.SourceOf("UserLogFrm"));
        Assert.Equal("chung", mc.Get("UserLogFrm", null)!.Value.Lst.CaptionVN);
        Assert.Equal(OverlaySource.Global, mc.SourceOf("UserLogFrm"));
        // Form chưa biết là lớp viết tay nhưng có mô tả trên web: coi là form viết tay (mô tả che cấu hình DB cùng tên).
        Assert.True(mc.IsHandCoded("XFrm"));
        Assert.Equal(OverlaySource.BuiltIn, mc.SourceOf("CS_ReceiptFrm"));
        Assert.NotEqual(tp.Stamp, mc.Stamp);
    }

    [Fact]
    public async Task Bad_json_is_skipped_and_stamp_follows_the_version()
    {
        var store = new Store { Snapshot = new FormOverlaySnapshot(1, [new FormOverlayRecord("UserLogFrm", null, "{ hỏng", 1)]) };
        var catalog = new OverlayCatalog(store);
        var first = await catalog.ForTenantAsync(Tp, CancellationToken.None);
        Assert.Equal(OverlaySource.BuiltIn, first.SourceOf("UserLogFrm"));
        Assert.Same(first, await catalog.ForTenantAsync(Tp, CancellationToken.None));

        // Khóa "form" không khớp tên bản ghi cũng bị bỏ.
        store.Snapshot = new FormOverlaySnapshot(2, [new FormOverlayRecord("UserLogFrm", null, Json("KhacFrm", "x"), 1)]);
        Assert.Equal(OverlaySource.BuiltIn, (await catalog.ForTenantAsync(Tp, CancellationToken.None)).SourceOf("UserLogFrm"));

        store.Snapshot = new FormOverlaySnapshot(3, [new FormOverlayRecord("UserLogFrm", null, Json("UserLogFrm", "mới"), 2)]);
        var third = await catalog.ForTenantAsync(Tp, CancellationToken.None);
        Assert.Equal(OverlaySource.Global, third.SourceOf("UserLogFrm"));
        Assert.NotEqual(first.Stamp, third.Stamp);
    }

    [Theory]
    [InlineData("""{ "form": "XFrm", "formType": "LIST", "caption": "X", "cfg": { "T0": { "TN": "XTbl", "PK": "ID", "FTX": "1=1; DELETE FROM XTbl" } } }""", "nhiều câu lệnh")]
    [InlineData("""{ "form": "XFrm", "formType": "LIST", "caption": "X", "cfg": { "T0": { "TN": "XTbl", "PK": "ID", "EX": "dbo.Fn() AS A INTO Y" } } }""", "into")]
    [InlineData("""{ "form": "XFrm", "formType": "LIST", "caption": "X", "cfg": { "T0": { "TN": "YTbl", "PK": "ID", "FRM": "(EXEC sp_who) YTbl" } } }""", "exec")]
    [InlineData("""{ "form": "XFrm", "formType": "LIST", "caption": "X", "para": { "Bank": { "T0": { "FTW": "isBank = 1 UNION SELECT 1; TRUNCATE TABLE X" } } }, "cfg": { "T0": { "TN": "XTbl", "PK": "ID" } } }""", "Bank.")]
    [InlineData("""{ "form": "XFrm", "caption": "X" }""", "formType")]
    [InlineData("""{ "form": "XFrm", """, "JSON sai")]
    [InlineData("""[1, 2]""", "đối tượng JSON")]
    public void Validation_rejects_bad_files(string json, string expected)
    {
        var r = FormOverlays.Validate(json);
        Assert.NotNull(r.Error);
        Assert.Contains(expected, r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validation_accepts_every_built_in_file_and_reports_aliases()
    {
        foreach (var name in FormOverlays.Names)
        {
            var json = FormOverlays.BuiltInJson(name);
            Assert.NotNull(json);
            Assert.Null(FormOverlays.Validate(json).Error);
        }
        var r = FormOverlays.Validate(FormOverlays.BuiltInJson("AR_Invoice2Frm")!);
        Assert.Equal("AR_InvoiceFrm", r.Form);
        Assert.Contains("AR_Invoice2Frm", r.Aliases);
        Assert.Null(FormOverlays.Validate("// chú thích\n" + Json("XFrm", "X")).Error);
    }
}
