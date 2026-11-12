using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Auth;
using WebErp.Core.Tenancy;

namespace WebErp.IntegrationTests;

/// <summary>
/// Tạo một DB GIẢ tạm trên LocalDB (<c>(localdb)\MSSQLLocalDB</c>) theo <c>fake-erp.sql</c>, xóa khi xong.
/// Không bao giờ trỏ vào DB khách hàng (AGENTS.md). Không có LocalDB thì các test bị bỏ qua (CI hiện chưa có).
/// Đổi máy chủ: biến môi trường <c>WEBERP_TEST_SQL_MASTER</c> (chuỗi kết nối tới master của một SQL Server THỬ).
/// </summary>
public sealed class FakeErpDatabase : IAsyncLifetime
{
    public const string PasswordKey = "TEST01";
    private readonly string _master = Environment.GetEnvironmentVariable("WEBERP_TEST_SQL_MASTER")
                                      ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true;Connect Timeout=30";

    public string Name { get; } = "WebErpFake_" + Guid.NewGuid().ToString("N")[..12];
    public string? SkipReason { get; private set; }
    public string ConnectionString => new SqlConnectionStringBuilder(_master) { InitialCatalog = Name }.ConnectionString;
    public Tenant Tenant { get; } = new("FAKE", "Công ty giả", "SY", true, null, null, null, null);

    public async ValueTask InitializeAsync()
    {
        try
        {
            await using var m = new SqlConnection(_master);
            await m.OpenAsync();
            await m.ExecuteAsync($"CREATE DATABASE [{Name}]");
        }
        catch (SqlException ex)
        {
            SkipReason = "Không có SQL Server thử (LocalDB): " + ex.Message;
            return;
        }

        await using var c = new SqlConnection(ConnectionString);
        await c.OpenAsync();
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "fake-erp.sql"));
        foreach (var batch in script.Split(["\nGO\r\n", "\nGO\n", "\r\nGO\r\n"], StringSplitOptions.RemoveEmptyEntries))
            await c.ExecuteAsync(batch);

        object[] users =
        [
            new { u = "Admin", n = "Quản trị", p = LegacyPassword.Encode("Admin", "123", PasswordKey), d = false, g = (string?)null, b = (string?)null },
            new { u = "ketoan", n = "Kế toán viên", p = LegacyPassword.Encode("ketoan", "abc", PasswordKey), d = false, g = (string?)"KT", b = (string?)"HN" },
            new { u = "nghiviec", n = "Đã nghỉ", p = LegacyPassword.Encode("nghiviec", "abc", PasswordKey), d = true, g = (string?)"KT", b = (string?)null },
        ];
        await c.ExecuteAsync("INSERT dbo.SY_User (UserName,HoTen,[Password],[Disable],UserGroupID,BranchID) VALUES (@u,@n,@p,@d,@g,@b)", users);
    }

    public void SkipIfUnavailable()
    {
        if (SkipReason is not null) Assert.Skip(SkipReason);
    }

    public async ValueTask DisposeAsync()
    {
        if (SkipReason is not null) return;
        SqlConnection.ClearAllPools();
        await using var m = new SqlConnection(_master);
        await m.OpenAsync();
        await m.ExecuteAsync($"ALTER DATABASE [{Name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{Name}];");
    }
}

/// <summary>Kho công ty một phần tử, trỏ vào DB giả.</summary>
internal sealed class SingleTenantStore(FakeErpDatabase db) : ITenantStore
{
    public ValueTask<IReadOnlyList<Tenant>> FindByHostAsync(string host, CancellationToken ct) => ValueTask.FromResult<IReadOnlyList<Tenant>>([db.Tenant]);
    public ValueTask<Tenant?> GetAsync(string key, CancellationToken ct) => ValueTask.FromResult<Tenant?>(db.Tenant);
    public ValueTask<string?> GetConnectionStringAsync(string key, CancellationToken ct) => ValueTask.FromResult<string?>(db.ConnectionString);
}
