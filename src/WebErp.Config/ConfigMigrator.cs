using System.Globalization;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace WebErp.Config;

/// <summary>Chạy migration <c>db/config/NNN_*.sql</c> (nhúng trong assembly). Chỉ công cụ quản trị gọi.</summary>
public static class ConfigMigrator
{
    private const string Prefix = "WebErp.Config.Migrations.";

    /// <summary>Phiên bản schema mà mã này cần. Host từ chối chạy với tệp cũ hơn.</summary>
    public static int RequiredVersion => Migrations().Max(m => m.Version);

    public static IReadOnlyList<(int Version, string Name)> Migrate(SqliteConnection c)
    {
        ConfigDatabase.Exec(c, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL PRIMARY KEY, name TEXT NOT NULL, applied_at TEXT NOT NULL);");
        var current = CurrentVersion(c);
        var applied = new List<(int, string)>();
        foreach (var m in Migrations().Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var tx = c.BeginTransaction();
            c.Execute(m.Sql, transaction: tx);
            c.Execute("INSERT INTO schema_version (version, name, applied_at) VALUES (@v, @n, @at)",
                new { v = m.Version, n = m.Name, at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) }, tx);
            tx.Commit();
            applied.Add((m.Version, m.Name));
        }
        return applied;
    }

    public static int CurrentVersion(SqliteConnection c)
    {
        var exists = c.ExecuteScalar<long>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='schema_version'");
        return exists == 0 ? 0 : c.ExecuteScalar<int?>("SELECT MAX(version) FROM schema_version") ?? 0;
    }

    private static IEnumerable<(int Version, string Name, string Sql)> Migrations()
    {
        var asm = typeof(ConfigMigrator).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal)))
        {
            var name = res[Prefix.Length..];
            var version = int.Parse(name.AsSpan(0, name.IndexOf('_')), CultureInfo.InvariantCulture);
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            yield return (version, name, r.ReadToEnd());
        }
    }
}
