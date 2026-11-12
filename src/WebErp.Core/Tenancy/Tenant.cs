namespace WebErp.Core.Tenancy;

/// <summary>
/// Một công ty (tenant) theo cấu hình (QĐ-07). Chuỗi kết nối KHÔNG nằm ở đây: chỉ lớp mở kết nối mới lấy,
/// qua <see cref="ITenantStore.GetConnectionStringAsync"/>.
/// </summary>
/// <param name="Key">Mã công ty, ổn định, dùng trong cookie và nhật ký.</param>
/// <param name="Name">Tên hiển thị (ô chọn công ty, thông tin phiên).</param>
/// <param name="ModuleId">Tiền tố bảng hệ thống của ERP (<c>SY</c>, <c>WA</c>…), tài liệu 12 §1.6.</param>
/// <param name="MaintenanceUntil">Đang bảo trì tới lúc này (BE trả 503 + Retry-After).</param>
/// <param name="LogoutBefore">Cookie cấp trước mốc này bị từ chối ("đăng xuất cả công ty").</param>
public sealed record Tenant(
    string Key,
    string Name,
    string ModuleId,
    bool Enabled,
    DateTimeOffset? MaintenanceUntil,
    DateTimeOffset? LogoutBefore,
    string? TaxCode,
    string? DatabaseLabel)
{
    public bool InMaintenance(DateTimeOffset now) => MaintenanceUntil is { } until && until > now;
}
