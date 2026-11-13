namespace WebErp.Erp.Connection;

/// <summary>Cấu hình truy cập DB ERP (mục <c>WebErp:Erp</c>).</summary>
public sealed class ErpOptions
{
    /// <summary>Timeout mỗi câu lệnh. Một khách chậm không được giữ luồng mãi (QĐ-07, bổ sung 2026-11-11, hệ quả 5).</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Timeout mở kết nối, dùng khi chuỗi kết nối không tự đặt.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Thư mục gốc chứa mẫu in <c>.rpx</c> của các công ty (mỗi công ty một thư mục con theo mã công ty), dùng khi mẫu không có trong
    /// <c>SY_RpFTbl</c> (WinForms đọc thư mục <c>Reports</c> trên máy). Để trống = chỉ đọc mẫu trong DB.
    /// </summary>
    public string? ReportsRoot { get; set; }
}
