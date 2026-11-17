using Microsoft.Data.SqlClient;
using WebErp.Core.Errors;
using WebErp.Core.Tenancy;

namespace WebErp.Erp.Connection;

/// <summary>Mở kết nối tới DB ERP của đúng công ty. Không có chuỗi kết nối thì báo lỗi, không bao giờ rơi sang công ty khác.</summary>
public sealed class ErpConnectionFactory(ITenantStore tenants, ErpOptions options)
{
    public ErpOptions Options { get; } = options;

    public async Task<SqlConnection> OpenAsync(Tenant tenant, CancellationToken ct)
    {
        var raw = await tenants.GetConnectionStringAsync(tenant.Key, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
            throw new ErpUnavailableException(tenant.Key, new InvalidOperationException("Công ty chưa có chuỗi kết nối."));

        // Chuẩn hóa mỗi lần mở (rẻ), kể cả chuỗi nhập từ trước khi có bước chuẩn hóa ở công cụ quản trị.
        var conn = new SqlConnection(ErpConnectionStrings.Normalize(raw, Options).ConnectionString);
        try
        {
            await conn.OpenAsync(ct).ConfigureAwait(false);
            return conn;
        }
        catch (SqlException ex)
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw new ErpUnavailableException(tenant.Key, ex);
        }
    }

    /// <summary>
    /// Lỗi do hạ tầng (mất kết nối, server, hết thời gian chờ), không phải lỗi câu lệnh (tên cột sai, quyền…). Dùng làm bộ lọc
    /// <c>catch … when</c>, rồi đổi sang ngoại lệ đúng loại bằng <see cref="Translate"/>.
    /// </summary>
    public static bool IsConnectivity(SqlException ex) =>
        ex.Class >= 20 || ex.Number is -2 or 53 or 121 or 233 or 10053 or 10054 or 10060 or 10061 or 11001 or 4060 or 18456;

    /// <summary>SQL -2 khi đang chạy lệnh: "Execution Timeout Expired".</summary>
    public static bool IsCommandTimeout(SqlException ex) => ex.Number == -2;

    /// <summary>
    /// Lỗi hạ tầng xảy ra KHI CHẠY LỆNH (kết nối đã mở): hết thời gian chờ → <see cref="ErpTimeoutException"/> ("phản hồi chậm"),
    /// còn lại → <see cref="ErpUnavailableException"/>. Lỗi lúc mở kết nối luôn là "không kết nối được" (<see cref="OpenAsync"/>).
    /// </summary>
    public static ErpDatabaseException Translate(string tenantKey, SqlException ex, string operation, int timeoutSeconds) =>
        IsCommandTimeout(ex) ? new ErpTimeoutException(tenantKey, operation, timeoutSeconds, ex) : new ErpUnavailableException(tenantKey, ex);
}
