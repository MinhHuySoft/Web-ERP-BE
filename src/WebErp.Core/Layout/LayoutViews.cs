using WebErp.Core.Metadata;

namespace WebErp.Core.Layout;

/// <summary>Màn vẽ layout của một form (chuẩn ERP-HTML 15.3, tài liệu FE 07-ve-layout-form.md).</summary>
/// <param name="Json">Bố cục <c>erp-form-layout/1</c> cho FE (nội dung của <c>data-layout-src</c>).</param>
/// <param name="CanSave">Được lưu: quyền Admin trên form và DB công ty đã có bảng sao lưu (<c>tenant migrate</c>).</param>
public sealed record LayoutDesignView(ScreenStatus Status, string? Title = null, string? Json = null, bool CanSave = false, string? Note = null);

/// <param name="Conflict">Bố cục đã bị đổi (WinForms hoặc người khác) từ lúc mở trình vẽ: không ghi gì.</param>
/// <param name="Changed">Số dòng <c>SY_FrmCfg</c> đã thêm / sửa / xóa.</param>
public sealed record LayoutSaveResult(bool Ok, int VersionNo = 0, IReadOnlyList<string>? Errors = null, bool Conflict = false, int Changed = 0);

/// <summary>
/// Trình vẽ layout form như <c>LayoutSettingFrm</c> (Ctrl+F12) của WinForms: đọc / lưu dòng bố cục của form trong <c>SY_FrmCfg</c>
/// (WinForms và web cùng thấy), mỗi lần lưu giữ ảnh trước / sau trong schema <c>web</c> để khôi phục.
/// </summary>
public interface IErpLayout
{
    Task<LayoutDesignView> OpenAsync(ScreenRequest request, CancellationToken ct);

    Task<LayoutSaveResult> SaveAsync(ScreenRequest request, string json, string? comment, CancellationToken ct);

    /// <summary>Ghi lại ảnh "trước" của phiên bản <paramref name="versionNo"/> vào cấu hình form (cũng thành một phiên bản mới).</summary>
    Task<LayoutSaveResult> RestoreAsync(ScreenRequest request, int versionNo, string? comment, CancellationToken ct);

    /// <summary>Nguồn chép bố cục: danh sách form có bố cục (<paramref name="fid"/> rỗng) hoặc bố cục của một form (JSON). null = không có quyền.</summary>
    Task<string?> SourceAsync(ScreenRequest request, string? fid, CancellationToken ct);
}
