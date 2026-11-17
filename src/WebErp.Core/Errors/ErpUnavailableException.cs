namespace WebErp.Core.Errors;

/// <summary>
/// DB của một công ty không phục vụ được yêu cầu (không kết nối được, hoặc phản hồi quá chậm). Host trả 503 + Retry-After cho riêng
/// công ty đó; các công ty khác vẫn chạy (QĐ-07, bổ sung 2026-11-11, hệ quả 6).
/// </summary>
public abstract class ErpDatabaseException(string tenantKey, string message, Exception inner) : Exception(message, inner)
{
    public string TenantKey { get; } = tenantKey;
}

/// <summary>Không kết nối được DB của công ty (mạng, SQL Server tắt, sai chuỗi kết nối, sai đăng nhập SQL).</summary>
public sealed class ErpUnavailableException(string tenantKey, Exception inner)
    : ErpDatabaseException(tenantKey, $"Không kết nối được DB của công ty '{tenantKey}'.", inner);

/// <summary>
/// Đã kết nối được nhưng câu lệnh chạy quá thời gian chờ (SQL -2 "Execution Timeout Expired"): DB công ty phản hồi chậm.
/// Khác với "không kết nối được": thường thử lại sau là qua.
/// </summary>
/// <param name="operation">Việc đang làm (vd "menu"), để ghi log.</param>
/// <param name="timeoutSeconds">Thời gian chờ đã dùng.</param>
public sealed class ErpTimeoutException(string tenantKey, string operation, int timeoutSeconds, Exception inner)
    : ErpDatabaseException(tenantKey, $"DB của công ty '{tenantKey}' phản hồi chậm: {operation} quá {timeoutSeconds} giây.", inner)
{
    public string Operation { get; } = operation;

    public int TimeoutSeconds { get; } = timeoutSeconds;
}
