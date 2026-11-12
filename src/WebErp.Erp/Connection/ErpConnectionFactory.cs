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

        var csb = new SqlConnectionStringBuilder(raw) { ApplicationName = "WebErp" };
        if (!raw.Contains("timeout", StringComparison.OrdinalIgnoreCase)) csb.ConnectTimeout = Options.ConnectTimeoutSeconds;

        var conn = new SqlConnection(csb.ConnectionString);
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

    /// <summary>Lỗi do mất kết nối / server, không phải lỗi câu lệnh (tên cột sai, quyền…).</summary>
    public static bool IsConnectivity(SqlException ex) =>
        ex.Class >= 20 || ex.Number is -2 or 53 or 121 or 233 or 10053 or 10054 or 10060 or 10061 or 11001 or 4060 or 18456;
}
