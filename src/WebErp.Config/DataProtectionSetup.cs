using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;

namespace WebErp.Config;

/// <summary>
/// Cấu hình Data Protection dùng chung cho Host và công cụ quản trị. Hai bên PHẢI cùng tên ứng dụng và cùng thư mục khóa, thì
/// chuỗi kết nối do công cụ quản trị mã hóa mới giải mã được ở Host.
/// </summary>
public static class DataProtectionSetup
{
    public const string ApplicationName = "WebErp";

    /// <param name="certificateThumbprint">
    /// Chứng chỉ bọc khóa (LocalMachine\My). Nên dùng: khôi phục được lên VPS khác (QĐ-07, bổ sung 2026-11-11, hệ quả 7).
    /// Không có thì dùng DPAPI theo máy: chạy được, nhưng bản sao lưu khóa chỉ giải mã được trên đúng máy này.
    /// </param>
    public static IDataProtectionBuilder AddWebErpDataProtection(this IServiceCollection services, WebErpPaths paths, string? certificateThumbprint)
    {
        Directory.CreateDirectory(paths.KeysDir);
        var builder = services.AddDataProtection()
            .SetApplicationName(ApplicationName)
            .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDir));

        if (!string.IsNullOrWhiteSpace(certificateThumbprint))
            builder.ProtectKeysWithCertificate(FindCertificate(certificateThumbprint));
        else if (OperatingSystem.IsWindows())
            builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
        return builder;
    }

    private static X509Certificate2 FindCertificate(string thumbprint)
    {
        var t = thumbprint.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
        {
            using var store = new X509Store(StoreName.My, location);
            store.Open(OpenFlags.ReadOnly);
            var found = store.Certificates.Find(X509FindType.FindByThumbprint, t, validOnly: false);
            if (found.Count > 0) return found[0];
        }
        throw new InvalidOperationException($"Không tìm thấy chứng chỉ bọc khóa có thumbprint {t} trong kho My.");
    }
}
