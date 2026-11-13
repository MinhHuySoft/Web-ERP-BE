using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Erp.Connection;
using WebErp.Erp.WebSchema;

namespace WebErp.IntegrationTests;

/// <summary>Migration schema web trên DB giả: tạo đủ bảng, chạy lại không đổi gì, không đụng bảng dbo.</summary>
public sealed class WebSchemaTests(FakeErpDatabase db) : IClassFixture<FakeErpDatabase>
{
    [Fact]
    public async Task Migrations_create_the_web_schema_once_and_are_idempotent()
    {
        db.SkipIfUnavailable();
        var migrator = new WebSchemaMigrator(new ErpConnectionFactory(new SingleTenantStore(db), new ErpOptions()));
        await using var c = new SqlConnection(db.ConnectionString);
        await c.OpenAsync();
        var dboBefore = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.objects WHERE schema_id = SCHEMA_ID('dbo')");

        Assert.False((await migrator.StatusAsync(db.Tenant, CancellationToken.None)).UpToDate);
        var first = await migrator.ApplyAsync(db.Tenant, "test", CancellationToken.None);
        Assert.Equal(WebSchemaMigrator.All.Count, first.Count);
        Assert.Empty(await migrator.ApplyAsync(db.Tenant, "test", CancellationToken.None));
        var status = await migrator.StatusAsync(db.Tenant, CancellationToken.None);
        Assert.True(status.UpToDate);
        Assert.Equal("test", status.Applied[0].AppliedBy);

        foreach (var t in new[] { "web.MigrationHistory", "web.CommandJournal", "web.PostCommitTask", "web.PrintTemplate", "web.PrintTemplateVersion" })
            Assert.Equal(1, await c.ExecuteScalarAsync<int>("SELECT CASE WHEN OBJECT_ID(@t, 'U') IS NULL THEN 0 ELSE 1 END", new { t }));
        Assert.Equal(dboBefore, await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.objects WHERE schema_id = SCHEMA_ID('dbo')"));
    }
}
