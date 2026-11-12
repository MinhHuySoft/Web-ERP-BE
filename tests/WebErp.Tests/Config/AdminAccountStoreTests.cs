using Microsoft.AspNetCore.DataProtection;
using WebErp.Config;
using WebErp.Core.Auth;
using WebErp.Tests.Support;

namespace WebErp.Tests.Config;

public sealed class AdminAccountStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weberp-admin-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock _time = new(new DateTimeOffset(2026, 11, 12, 1, 0, 0, TimeSpan.Zero));
    private readonly ConfigDatabase _db;
    private readonly SecretProtector _protector = new(new EphemeralDataProtectionProvider());
    private readonly AdminAccountStore _accounts;
    private readonly ConfigWriter _writer;
    private readonly byte[] _secret = Totp.NewSecret();

    public AdminAccountStoreTests()
    {
        _db = new ConfigDatabase(new WebErpPaths(_root));
        _writer = new ConfigWriter(_db, _protector, _time, "test");
        _writer.Initialize();
        _accounts = new AdminAccountStore(_db, _protector, _time);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string NextCode()
    {
        _time.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        return Totp.Code(_secret, Totp.StepAt(_time.GetUtcNow()));
    }

    private void CreateAdmin() => _accounts.Create("quan.tri", "Quan-Tri-2026", _secret, Totp.StepAt(_time.GetUtcNow()), "test");

    [Fact]
    public void Login_needs_password_and_fresh_code()
    {
        CreateAdmin();
        Assert.Equal(1, _accounts.Count());
        var code = NextCode();
        Assert.Equal(AdminLoginStatus.Invalid, _accounts.Login("quan.tri", "sai", code).Status);
        Assert.Equal(AdminLoginStatus.Ok, _accounts.Login("QUAN.TRI", "Quan-Tri-2026", code).Status);
        Assert.Equal(AdminLoginStatus.Invalid, _accounts.Login("quan.tri", "Quan-Tri-2026", code).Status); // mã đã dùng
        Assert.Equal(AdminLoginStatus.Invalid, _accounts.Login("khong.co", "Quan-Tri-2026", NextCode()).Status);
        Assert.True(_accounts.VerifyCode("quan.tri", NextCode()));
        Assert.False(_accounts.VerifyCode("quan.tri", "000000"));
    }

    [Fact]
    public void Five_failures_lock_the_account()
    {
        CreateAdmin();
        for (var i = 0; i < AdminAccountStore.MaxFailures - 1; i++) Assert.Equal(AdminLoginStatus.Invalid, _accounts.Login("quan.tri", "sai", "000000").Status);
        var locked = _accounts.Login("quan.tri", "sai", "000000");
        Assert.Equal(AdminLoginStatus.Locked, locked.Status);
        Assert.Equal(AdminLoginStatus.Locked, _accounts.Login("quan.tri", "Quan-Tri-2026", NextCode()).Status);
        _time.Advance(AdminAccountStore.LockFor);
        Assert.Equal(AdminLoginStatus.Ok, _accounts.Login("quan.tri", "Quan-Tri-2026", NextCode()).Status);
    }

    [Fact]
    public void Weak_password_and_bad_name_are_refused()
    {
        Assert.Throws<ArgumentException>(() => _accounts.Create("quan.tri", "ngan", _secret, 0, "test"));
        Assert.Throws<ArgumentException>(() => _accounts.Create("a b", "Quan-Tri-2026", _secret, 0, "test"));
    }

    [Fact]
    public async Task Settings_are_encrypted_and_visible_to_the_host_store()
    {
        var store = new SqliteTenantStore(_db, _protector, _time);
        Assert.Null(await store.GetSettingAsync(SettingNames.LegacyPasswordKey, CancellationToken.None));
        _writer.SetSetting(SettingNames.LegacyPasswordKey, "ABC123");
        Assert.True(_writer.HasSetting(SettingNames.LegacyPasswordKey));
        _time.Advance(SqliteTenantStore.CheckInterval + TimeSpan.FromSeconds(1));
        Assert.Equal("ABC123", await store.GetSettingAsync(SettingNames.LegacyPasswordKey, CancellationToken.None));
        Assert.DoesNotContain("ABC123", await File.ReadAllTextAsync(_db.Path));
        Assert.Contains(_writer.ReadAudit(10), a => a.Action == "setting.set" && a.Detail is null);
    }

    [Fact]
    public void Update_detects_concurrent_edits_and_replaces_hosts()
    {
        _writer.AddTenant("A", "Công ty A");
        var first = _writer.Get("A")!;
        _time.Advance(TimeSpan.FromSeconds(1));
        _writer.Update("A", "Công ty A mới", "SY", null, null, true, ["a.bms7.net", "A.bms7.net", "b.bms7.net"], first.Revision);
        var second = _writer.Get("A")!;
        Assert.Equal(["a.bms7.net", "b.bms7.net"], second.Hosts);
        Assert.Throws<ConfigConflictException>(() => _writer.Update("A", "x", "SY", null, null, true, [], first.Revision));
        Assert.Throws<ArgumentException>(() => _writer.Update("A", "x", "SY", null, null, true, ["bad host"], second.Revision));
    }
}
