using Microsoft.Data.Sqlite;

namespace WebErp.Config;

/// <summary>
/// Mở tệp SQLite cấu hình. Host chỉ mở <b>chỉ đọc</b>; chỉ công cụ quản trị được ghi.
/// Dùng journal kiểu DELETE (không dùng WAL): ở chế độ WAL, người chỉ đọc vẫn cần quyền ghi tệp <c>-shm</c>, như vậy không
/// tách được quyền NTFS "Host chỉ đọc". Cấu hình rất ít khi ghi nên không cần WAL.
/// </summary>
public sealed class ConfigDatabase(WebErpPaths paths)
{
    public string Path => paths.ConfigDb;

    public bool Exists => File.Exists(paths.ConfigDb);

    public SqliteConnection OpenReadOnly()
    {
        if (!Exists) throw new ConfigNotInitializedException(paths.ConfigDb);
        return Open(SqliteOpenMode.ReadOnly);
    }

    public SqliteConnection OpenReadWrite()
    {
        Directory.CreateDirectory(paths.DataDir);
        var c = Open(SqliteOpenMode.ReadWriteCreate);
        Exec(c, "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;");
        return c;
    }

    private SqliteConnection Open(SqliteOpenMode mode)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = paths.ConfigDb,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5,
            // Không giữ tệp mở trong pool: công cụ quản trị cần chép / sao lưu tệp này khi BE đang chạy.
            Pooling = false,
        }.ToString();
        var c = new SqliteConnection(cs);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=5000;");
        return c;
    }

    internal static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

public sealed class ConfigNotInitializedException(string path)
    : Exception($"Chưa có tệp cấu hình '{path}'. Chạy 'WebErp.Admin init' trước.");
