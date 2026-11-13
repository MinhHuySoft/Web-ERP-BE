using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;

namespace WebErp.Erp.WebSchema;

/// <summary>Một migration của schema <c>web</c> (tệp <c>WebSchema/Migrations/NNNN_tên.sql</c>, nhúng vào dll).</summary>
public sealed record WebMigration(int Id, string Name, string Sql, string Checksum);

/// <summary>Migration đã chạy trên DB của công ty.</summary>
public sealed record AppliedMigration(int Id, string Name, string Checksum, DateTime AppliedAt, string? AppliedBy);

/// <summary>Tình trạng schema <c>web</c> của một công ty.</summary>
public sealed record WebSchemaStatus(IReadOnlyList<WebMigration> All, IReadOnlyList<AppliedMigration> Applied)
{
    public IReadOnlyList<WebMigration> Pending => All.Where(m => Applied.All(a => a.Id != m.Id)).ToList();

    /// <summary>Migration đã chạy nhưng tệp trong bản hiện tại khác nội dung (ai đó sửa tệp đã phát hành).</summary>
    public IReadOnlyList<WebMigration> Changed => All.Where(m => Applied.Any(a => a.Id == m.Id && a.Checksum != m.Checksum)).ToList();

    public bool UpToDate => Pending.Count == 0 && Changed.Count == 0;
}

/// <summary>
/// Tạo / nâng cấp schema <c>web</c> trong DB khách (QĐ-02): migration đánh số, chạy lại được, mỗi migration một giao dịch,
/// khóa <c>sp_getapplock</c> để hai tiến trình không chạy cùng lúc. KHÔNG tự chạy khi BE khởi động: quản trị chạy bằng
/// <c>tenant migrate</c> (CLI) hoặc nút trên trang quản trị. Chỉ tạo đối tượng trong schema <c>web</c>, không đụng bảng ERP.
/// </summary>
public sealed partial class WebSchemaMigrator(ErpConnectionFactory connections)
{
    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    [GeneratedRegex(@"^(\d{4})_([A-Za-z0-9_]+)\.sql$")]
    private static partial Regex FileName();

    private static readonly Lazy<IReadOnlyList<WebMigration>> Embedded = new(Load);

    public static IReadOnlyList<WebMigration> All => Embedded.Value;

    public async Task<WebSchemaStatus> StatusAsync(Tenant tenant, CancellationToken ct)
    {
        await using var c = await connections.OpenAsync(tenant, ct);
        return new WebSchemaStatus(All, await AppliedAsync(c, null, ct));
    }

    /// <summary>Chạy các migration còn thiếu theo thứ tự. Migration đã chạy mà tệp đổi nội dung ⇒ dừng, báo lỗi.</summary>
    public async Task<IReadOnlyList<WebMigration>> ApplyAsync(Tenant tenant, string appliedBy, CancellationToken ct)
    {
        await using var c = await connections.OpenAsync(tenant, ct);
        var done = new List<WebMigration>();
        foreach (var m in All)
        {
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            await c.ExecuteAsync(new CommandDefinition(
                "DECLARE @r int; EXEC @r = sp_getapplock @Resource = N'web:migrate', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; " +
                "IF @r < 0 THROW 50001, N'Đang có tiến trình khác cập nhật schema web.', 1;", transaction: tx, cancellationToken: ct));
            var applied = await AppliedAsync(c, tx, ct);
            var hit = applied.FirstOrDefault(a => a.Id == m.Id);
            if (hit is not null)
            {
                await tx.RollbackAsync(ct);
                if (hit.Checksum != m.Checksum)
                    throw new InvalidOperationException($"Migration {m.Id:0000} '{m.Name}' đã chạy nhưng nội dung tệp đã đổi. Không sửa migration đã phát hành; thêm migration mới.");
                continue;
            }
            foreach (var batch in GoLine().Split(m.Sql).Select(b => b.Trim()).Where(b => b.Length > 0))
                await c.ExecuteAsync(new CommandDefinition(batch, transaction: tx, commandTimeout: 300, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition(
                "INSERT web.MigrationHistory (Id, Name, Checksum, AppliedBy) VALUES (@Id, @Name, @Checksum, @By)",
                new { m.Id, m.Name, m.Checksum, By = appliedBy }, tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
            done.Add(m);
        }
        return done;
    }

    private static async Task<IReadOnlyList<AppliedMigration>> AppliedAsync(SqlConnection c, SqlTransaction? tx, CancellationToken ct)
    {
        var exists = await c.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN OBJECT_ID(N'web.MigrationHistory', N'U') IS NULL THEN 0 ELSE 1 END", transaction: tx, cancellationToken: ct));
        if (exists == 0) return [];
        return (await c.QueryAsync<AppliedMigration>(new CommandDefinition(
            "SELECT Id, Name, Checksum, AppliedAt, AppliedBy FROM web.MigrationHistory ORDER BY Id", transaction: tx, cancellationToken: ct))).ToList();
    }

    private static List<WebMigration> Load()
    {
        var asm = typeof(WebSchemaMigrator).Assembly;
        var list = new List<WebMigration>();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("WebErp.Migrations.", StringComparison.Ordinal)))
        {
            var file = res["WebErp.Migrations.".Length..];
            var m = FileName().Match(file);
            if (!m.Success) throw new InvalidDataException($"Tên migration sai mẫu NNNN_tên.sql: {file}");
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s, Encoding.UTF8);
            var sql = r.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
            list.Add(new WebMigration(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value, sql,
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sql)))));
        }
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        for (var i = 0; i < list.Count; i++)
            if (list[i].Id != i + 1) throw new InvalidDataException($"Migration phải đánh số liên tục từ 0001; thiếu {i + 1:0000}.");
        return list;
    }
}
