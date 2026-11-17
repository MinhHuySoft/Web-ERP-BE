namespace WebErp.Core.Metadata;

/// <summary>
/// Một tệp mô tả form viết tay sửa trên trang quản trị (bảng <c>form_overlay</c> của <c>config.db</c>). Cùng định dạng JSON với tệp nhúng
/// <c>Metadata/Overlays/*.json</c> của BE, nhưng có hiệu lực ngay khi lưu, không cần build / deploy.
/// </summary>
/// <param name="TenantKey">null = áp cho mọi công ty; có giá trị = chỉ công ty đó (đứng trước bản chung).</param>
public sealed record FormOverlayRecord(string Form, string? TenantKey, string Json, long Revision);

/// <summary>Các mô tả đang bật, kèm phiên bản cấu hình lúc đọc (đổi khi có người lưu / xóa mô tả).</summary>
public sealed record FormOverlaySnapshot(long Version, IReadOnlyList<FormOverlayRecord> Items)
{
    public static readonly FormOverlaySnapshot Empty = new(0, []);
}

/// <summary>Nguồn mô tả form sửa trên trang quản trị. Không có (test, công cụ dòng lệnh) thì chỉ dùng tệp nhúng.</summary>
public interface IFormOverlayStore
{
    ValueTask<FormOverlaySnapshot> GetFormOverlaysAsync(CancellationToken ct);
}
