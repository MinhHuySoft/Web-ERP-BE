using Microsoft.AspNetCore.DataProtection;
using WebErp.Tests.Support;
using WebErp.Config;

namespace WebErp.Tests.Config;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weberp-test-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _time = new(new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.Zero));
    private readonly ConfigDatabase _db;
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly ConfigWriter _writer;

    public ConfigStoreTests()
    {
        _db = new ConfigDatabase(new WebErpPaths(_root));
        _writer = new ConfigWriter(_db, _protector, _time, "test");
        _writer.Initialize();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Host_maps_to_its_companies_and_connection_is_encrypted_at_rest()
    {
        _writer.AddTenant("MC", "Medcom", taxCode: "0300000000");
        _writer.SetConnection("MC", "Server=x;Database=MedData;User Id=u;Password=secret-value");
        _writer.AddHost("MC", "ERP.Medcom.vn.");
        var store = new SqliteTenantStore(_db, _protector, _time);

        var found = await store.FindByHostAsync("erp.medcom.vn:443", CancellationToken.None);
        Assert.Equal("MC", Assert.Single(found).Key);
        Assert.Equal("0300000000", found[0].TaxCode);
        Assert.Contains("secret-value", await store.GetConnectionStringAsync("MC", CancellationToken.None));
        Assert.DoesNotContain("secret-value", await File.ReadAllTextAsync(_db.Path));
        Assert.Empty(await store.FindByHostAsync("other.vn", CancellationToken.None));
    }

    [Fact]
    public async Task One_host_can_serve_many_companies_in_insertion_order()
    {
        _writer.AddTenant("A", "Công ty A");
        _writer.AddTenant("B", "Công ty B");
        _writer.AddHost("B", "group.vn");
        _writer.AddHost("A", "group.vn");
        var store = new SqliteTenantStore(_db, _protector, _time);
        Assert.Equal(["B", "A"], (await store.FindByHostAsync("group.vn", CancellationToken.None)).Select(t => t.Key));
    }

    [Fact]
    public async Task Changes_are_picked_up_after_the_check_interval()
    {
        _writer.AddTenant("A", "Công ty A");
        _writer.AddHost("A", "a.vn");
        var store = new SqliteTenantStore(_db, _protector, _time);
        Assert.Null((await store.GetAsync("A", CancellationToken.None))!.LogoutBefore);

        var at = _time.GetUtcNow();
        _writer.LogoutAll("A");
        Assert.Null((await store.GetAsync("A", CancellationToken.None))!.LogoutBefore); // còn trong bản chụp
        _time.Advance(SqliteTenantStore.CheckInterval + TimeSpan.FromSeconds(1));
        Assert.Equal(at, (await store.GetAsync("A", CancellationToken.None))!.LogoutBefore);
    }

    [Fact]
    public void Rejects_invalid_names()
    {
        Assert.Throws<ArgumentException>(() => _writer.AddTenant("a b", "x"));
        Assert.Throws<ArgumentException>(() => _writer.AddTenant("A", "x", moduleId: "SY;DROP"));
        _writer.AddTenant("A", "x");
        Assert.Throws<ArgumentException>(() => _writer.AddHost("A", "bad host"));
        Assert.Throws<InvalidOperationException>(() => _writer.SetEnabled("NOPE", false));
    }

    [Fact]
    public void Backup_creates_a_consistent_copy()
    {
        _writer.AddTenant("A", "x");
        var file = _writer.Backup(Path.Combine(_root, "backup"));
        Assert.True(File.Exists(file));
        var copy = new ConfigDatabase(new WebErpPaths(Path.Combine(_root, "restore")));
        Directory.CreateDirectory(Path.GetDirectoryName(copy.Path)!);
        File.Copy(file, copy.Path);
        Assert.Single(new ConfigWriter(copy, _protector, _time, "t").List());
    }
}
