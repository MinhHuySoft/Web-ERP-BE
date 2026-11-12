using Microsoft.Extensions.Configuration;

namespace WebErp.Config;

/// <summary>
/// Bố cục thư mục dữ liệu (hướng dẫn cấu trúc BE §6). Dữ liệu KHÔNG nằm trong thư mục site, vì thư mục site bị thay mỗi lần
/// triển khai.
/// </summary>
public sealed record WebErpPaths(string Root)
{
    public string DataDir => Path.Combine(Root, "data");
    public string KeysDir => Path.Combine(Root, "keys");
    public string FilesDir => Path.Combine(Root, "files");
    public string BackupDir => Path.Combine(Root, "backup");
    public string ConfigDb => Path.Combine(DataDir, "config.db");

    /// <summary>Đọc <c>WebErp:Root</c>. Đường dẫn tương đối tính theo <paramref name="baseDir"/>.</summary>
    public static WebErpPaths From(IConfiguration configuration, string baseDir)
    {
        var root = configuration["WebErp:Root"];
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Thiếu cấu hình WebErp:Root (thư mục dữ liệu, vd C:\\WebErp; đặt bằng biến môi trường WebErp__Root).");
        return new WebErpPaths(Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(baseDir, root)));
    }
}
