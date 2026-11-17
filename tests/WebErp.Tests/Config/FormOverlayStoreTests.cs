using Microsoft.AspNetCore.DataProtection;
using WebErp.Config;
using WebErp.Tests.Support;

namespace WebErp.Tests.Config;

/// <summary>Mô tả form sửa trên trang quản trị: lưu / phiên bản / lịch sử / xóa, và Host đọc được qua bản chụp cấu hình.</summary>
public sealed class FormOverlayStoreTests : IDisposable
{
    private const string Json = """{ "form": "XFrm", "formType": "LIST", "caption": "X", "cfg": { "T0": { "TN": "XTbl", "PK": "ID" } } }""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "weberp-test-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _time = new(new DateTimeOffset(2026, 11, 17, 1, 0, 0, TimeSpan.Zero));
    private readonly ConfigDatabase _db;
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly ConfigWriter _writer;

    public FormOverlayStoreTests()
    {
        _db = new ConfigDatabase(new WebErpPaths(_root));
        _writer = new ConfigWriter(_db, _protector, _time, "test");
        _writer.Initialize();
        _writer.AddTenant("TP", "Tín Phong");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Save_bumps_revision_keeps_history_and_detects_conflicts()
    {
        Assert.Equal(1, _writer.SaveFormOverlay("XFrm", null, Json, true, "lần đầu", null));
        Assert.Throws<ConfigConflictException>(() => _writer.SaveFormOverlay("XFrm", null, Json, true, null, null));   // đã có: phải mở để sửa
        Assert.Equal(2, _writer.SaveFormOverlay("XFrm", null, Json, false, "tắt", 1));
        Assert.Throws<ConfigConflictException>(() => _writer.SaveFormOverlay("XFrm", null, Json, true, null, 1));     // người khác vừa sửa

        var row = _writer.GetFormOverlay("xfrm", null)!;
        Assert.Equal((2L, false, "tắt"), (row.Revision, row.Enabled, row.Note));
        Assert.Null(_writer.GetFormOverlay("XFrm", "TP"));

        _writer.DeleteFormOverlay("XFrm", null, 2);
        Assert.Null(_writer.GetFormOverlay("XFrm", null));
        var history = _writer.FormOverlayHistory("XFrm", null);
        Assert.Equal(["delete", "save", "save"], history.Select(h => h.Action));
        Assert.All(history, h => Assert.Equal(Json, h.Json));

        // Nhật ký thao tác không chứa JSON (có thể có tên bảng / SQL).
        Assert.DoesNotContain(_writer.ReadAudit(20), a => (a.Detail ?? "").Contains("XTbl", StringComparison.Ordinal));
        Assert.Contains(_writer.ReadAudit(20), a => a.Action == "form.save" && a.Target == "XFrm");
    }

    [Fact]
    public void Names_and_tenants_are_checked()
    {
        Assert.Throws<ArgumentException>(() => _writer.SaveFormOverlay("X Frm; DROP", null, Json, true, null, null));
        Assert.Throws<InvalidOperationException>(() => _writer.SaveFormOverlay("XFrm", "KHONGCO", Json, true, null, null));
    }

    [Fact]
    public async Task Host_snapshot_lists_enabled_overlays_and_version_changes()
    {
        var store = new SqliteTenantStore(_db, _protector, _time);
        var before = await store.GetFormOverlaysAsync(CancellationToken.None);
        Assert.Empty(before.Items);

        _writer.SaveFormOverlay("XFrm", null, Json, true, null, null);
        _writer.SaveFormOverlay("XFrm", "TP", Json, true, null, null);
        _writer.SaveFormOverlay("YFrm", null, Json.Replace("XFrm", "YFrm", StringComparison.Ordinal), false, null, null);
        _time.Advance(SqliteTenantStore.CheckInterval + TimeSpan.FromSeconds(1));

        var after = await store.GetFormOverlaysAsync(CancellationToken.None);
        Assert.NotEqual(before.Version, after.Version);
        Assert.Equal([("XFrm", (string?)null), ("XFrm", "TP")], after.Items.Select(i => (i.Form, i.TenantKey)));   // YFrm đang tắt
    }

    [Fact]
    public void Deleting_a_company_removes_its_own_overlays_only()
    {
        _writer.SaveFormOverlay("XFrm", null, Json, true, null, null);
        _writer.SaveFormOverlay("XFrm", "TP", Json, true, null, null);
        _writer.DeleteTenant("TP");
        Assert.Equal([(string?)null], _writer.ListFormOverlays().Select(r => r.TenantKey));
    }
}
