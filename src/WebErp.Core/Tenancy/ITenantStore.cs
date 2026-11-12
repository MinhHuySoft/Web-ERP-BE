namespace WebErp.Core.Tenancy;

/// <summary>Đọc cấu hình công ty. Bản cài đặt hiện tại: SQLite (QĐ-07). Sau này có thể là SQL Server (tài liệu 05, bậc 3).</summary>
public interface ITenantStore
{
    /// <summary>Các công ty gắn với host (cả công ty đang tắt). Rỗng nghĩa là host chưa được cấu hình.</summary>
    ValueTask<IReadOnlyList<Tenant>> FindByHostAsync(string host, CancellationToken ct);

    ValueTask<Tenant?> GetAsync(string key, CancellationToken ct);

    /// <summary>Chuỗi kết nối đã giải mã. Chỉ lớp mở kết nối DB ERP được gọi.</summary>
    ValueTask<string?> GetConnectionStringAsync(string key, CancellationToken ct);
}
