using WebErp.Core.Auth;
using WebErp.Core.Errors;
using WebErp.Core.Menu;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Diagnostics;
using WebErp.Erp.Menu;
using WebErp.Erp.Security;

namespace WebErp.IntegrationTests;

public sealed class ErpReadTests(FakeErpDatabase db) : IClassFixture<FakeErpDatabase>
{
    private ErpConnectionFactory Connections() => new(new SingleTenantStore(db), new ErpOptions());

    [Fact]
    public async Task Finds_active_user_with_hash_and_ignores_disabled_ones()
    {
        db.SkipIfUnavailable();
        var users = new ErpUsers(Connections());

        var rec = await users.FindForLoginAsync(db.Tenant, "KETOAN", CancellationToken.None);
        Assert.NotNull(rec);
        Assert.Equal("ketoan", rec.User.UserName); // tên đúng hoa thường lấy từ DB
        Assert.Equal("KT", rec.User.UserGroupId);
        Assert.True(LegacyPassword.Verify("KETOAN", "abc", rec.PasswordHash, FakeErpDatabase.PasswordKey));

        Assert.Null(await users.FindForLoginAsync(db.Tenant, "nghiviec", CancellationToken.None));
        Assert.False(await users.IsEnabledAsync(db.Tenant, "nghiviec", CancellationToken.None));
        Assert.True(await users.IsEnabledAsync(db.Tenant, "ketoan", CancellationToken.None));
        Assert.Null(await users.FindForLoginAsync(db.Tenant, "' OR 1=1 --", CancellationToken.None));
    }

    [Fact]
    public async Task Menu_contains_only_granted_items_their_parents_and_unchecked_items()
    {
        db.SkipIfUnavailable();
        var entries = await new ErpMenu(Connections()).GetUserMenuAsync(db.Tenant, "ketoan", "vi", CancellationToken.None);
        var tree = MenuTree.Build(entries);

        var ids = entries.Select(e => e.MenuId).ToHashSet();
        Assert.Superset(new HashSet<string> { "01", "0101", "0199", "09", "0901", "0902", "0930" }, ids);
        Assert.DoesNotContain("1601", ids); // IsRun = 0
        Assert.DoesNotContain("0990", ids); // menu đã tắt
        Assert.DoesNotContain("16", ids);
        Assert.Equal("Bank", entries.Single(e => e.MenuId == "0902").Para);
        Assert.True(entries.Single(e => e.MenuId == "0930").BeginGroup);
        Assert.Equal(["01", "05", "09"], tree.Select(n => n.Entry.MenuId));
    }

    [Fact]
    public async Task Menu_uses_language_columns()
    {
        db.SkipIfUnavailable();
        var en = await new ErpMenu(Connections()).GetUserMenuAsync(db.Tenant, "Admin", "en", CancellationToken.None);
        Assert.Equal("Bank receipt", en.Single(e => e.MenuId == "0902").Caption);
        var zh = await new ErpMenu(Connections()).GetUserMenuAsync(db.Tenant, "Admin", "zh", CancellationToken.None);
        Assert.Equal("Phiếu thu ngân hàng(*)", zh.Single(e => e.MenuId == "0902").Caption);
    }

    [Fact]
    public async Task Probe_reports_database_and_required_objects()
    {
        db.SkipIfUnavailable();
        var r = await new ErpProbe(Connections()).ProbeAsync(db.Tenant, CancellationToken.None);
        Assert.True(r.Ok, r.Error);
        Assert.Equal(db.Name, r.Database);
        Assert.True(r.HasUserTable);
        Assert.True(r.HasMenuFunction);
    }

    [Fact]
    public async Task Unreachable_server_becomes_ErpUnavailableException()
    {
        db.SkipIfUnavailable();
        var bad = new Tenant("BAD", "x", "SY", true, null, null, null, null);
        var store = new BadStore();
        var users = new ErpUsers(new ErpConnectionFactory(store, new ErpOptions { ConnectTimeoutSeconds = 2 }));
        var ex = await Assert.ThrowsAsync<ErpUnavailableException>(() => users.FindForLoginAsync(bad, "x", CancellationToken.None));
        Assert.Equal("BAD", ex.TenantKey);
    }

    private sealed class BadStore : ITenantStore
    {
        public ValueTask<IReadOnlyList<Tenant>> FindByHostAsync(string host, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<Tenant>>([]);
        public ValueTask<Tenant?> GetAsync(string key, CancellationToken ct) => ValueTask.FromResult<Tenant?>(null);
        public ValueTask<string?> GetConnectionStringAsync(string key, CancellationToken ct) =>
            ValueTask.FromResult<string?>("Server=tcp:127.0.0.1,1;Database=x;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=2");
    }
}
