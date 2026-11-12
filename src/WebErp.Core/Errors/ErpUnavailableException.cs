namespace WebErp.Core.Errors;

/// <summary>
/// Không kết nối được DB của một công ty (mạng, SQL Server tắt, sai chuỗi kết nối). Host trả 503 + Retry-After cho riêng
/// công ty đó; các công ty khác vẫn chạy (QĐ-07, bổ sung 2026-11-11, hệ quả 6).
/// </summary>
public sealed class ErpUnavailableException(string tenantKey, Exception inner)
    : Exception($"Không kết nối được DB của công ty '{tenantKey}'.", inner)
{
    public string TenantKey { get; } = tenantKey;
}
