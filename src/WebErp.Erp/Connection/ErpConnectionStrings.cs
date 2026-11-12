using Microsoft.Data.SqlClient;

namespace WebErp.Erp.Connection;

/// <summary>Chuỗi kết nối sau khi chuẩn hóa, kèm các điểm đã đổi (để công cụ quản trị báo cho người nhập).</summary>
public sealed record NormalizedConnection(string ConnectionString, string? Database, IReadOnlyList<string> Changes);

/// <summary>
/// Chuẩn hóa chuỗi kết nối tới DB ERP trước khi dùng. Chuỗi hay được chép từ SSMS, kèm những thiết lập không hợp với web:
/// <list type="bullet">
/// <item><c>Pooling=False</c>: mỗi câu lệnh phải mở lại kết nối (TCP + TLS + đăng nhập), qua Internet tốn hàng trăm ms → bật lại.</item>
/// <item><c>Persist Security Info=True</c>: mật khẩu đọc lại được từ đối tượng kết nối → tắt.</item>
/// <item><c>Command Timeout=0</c> (chờ vô hạn) → dùng timeout của BE, để một DB chậm không giữ luồng mãi.</item>
/// <item><c>Application Name</c> → <c>WebErp</c>, để DBA phân biệt kết nối của web trong SQL Server.</item>
/// <item>Máy chủ ở xa mà <c>Encrypt=False</c> → bắt buộc mã hóa (QĐ-07, bổ sung 2026-11-11, mục 3): mật khẩu và dữ liệu không
/// được đi trần qua Internet. Giữ nguyên <c>TrustServerCertificate</c> người nhập chọn.</item>
/// </list>
/// </summary>
public static class ErpConnectionStrings
{
    public static NormalizedConnection Normalize(string raw, ErpOptions options)
    {
        var csb = new SqlConnectionStringBuilder(raw);
        var changes = new List<string>();

        if (!csb.Pooling)
        {
            csb.Pooling = true;
            changes.Add("Bật lại Pooling (Pooling=False làm mỗi câu lệnh phải đăng nhập lại SQL Server).");
        }
        if (csb.PersistSecurityInfo)
        {
            csb.PersistSecurityInfo = false;
            changes.Add("Tắt Persist Security Info (không để mật khẩu đọc lại được từ kết nối).");
        }
        if (csb.CommandTimeout != options.CommandTimeoutSeconds)
        {
            if (csb.ShouldSerialize("Command Timeout")) changes.Add($"Command Timeout dùng {options.CommandTimeoutSeconds} giây của BE (không chờ vô hạn).");
            csb.CommandTimeout = options.CommandTimeoutSeconds;
        }
        if (!csb.ShouldSerialize("Connect Timeout")) csb.ConnectTimeout = options.ConnectTimeoutSeconds;
        if (!string.Equals(csb.ApplicationName, "WebErp", StringComparison.Ordinal))
        {
            if (csb.ShouldSerialize("Application Name")) changes.Add($"Application Name đổi từ \"{csb.ApplicationName}\" sang \"WebErp\".");
            csb.ApplicationName = "WebErp";
        }
        if (!IsLocal(csb.DataSource) && csb.Encrypt.Equals(SqlConnectionEncryptOption.Optional))
        {
            csb.Encrypt = SqlConnectionEncryptOption.Mandatory;
            changes.Add("Máy chủ SQL ở xa: bật mã hóa (Encrypt=True) để mật khẩu và dữ liệu không đi trần qua Internet.");
        }

        return new NormalizedConnection(csb.ConnectionString, string.IsNullOrWhiteSpace(csb.InitialCatalog) ? null : csb.InitialCatalog, changes);
    }

    /// <summary>Máy chủ SQL nằm trên chính máy này (không đi qua mạng).</summary>
    public static bool IsLocal(string dataSource)
    {
        var s = dataSource.Trim();
        foreach (var prefix in new[] { "tcp:", "np:", "lpc:", "admin:" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) s = s[prefix.Length..];
        if (s.StartsWith("(localdb)", StringComparison.OrdinalIgnoreCase)) return true;
        var host = s.Split(',', '\\')[0].Trim();
        return host is "." or "(local)" or "localhost" or "127.0.0.1" or "::1"
               || string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }
}
