namespace WebErp.Erp.Connection;

/// <summary>Cấu hình truy cập DB ERP (mục <c>WebErp:Erp</c>).</summary>
public sealed class ErpOptions
{
    /// <summary>Timeout mỗi câu lệnh. Một khách chậm không được giữ luồng mãi (QĐ-07, bổ sung 2026-11-11, hệ quả 5).</summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Timeout riêng cho hàm menu <c>&lt;Module&gt;_GetUserMenuFnc</c>: hàm này chậm trên một số DB (TinPhong, SQL Server 2016 RTM)
    /// mà mỗi người dùng chỉ gọi vài phút một lần (có cache).
    /// </summary>
    public int MenuCommandTimeoutSeconds { get; set; } = 60;

    /// <summary>Đọc menu lâu hơn chừng này mili giây thì ghi log cảnh báo (theo dõi DB chậm theo công ty).</summary>
    public int MenuSlowLogMs { get; set; } = 5000;

    /// <summary>Timeout mở kết nối, dùng khi chuỗi kết nối không tự đặt.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Thư mục gốc chứa mẫu in <c>.rpx</c> của các công ty (mỗi công ty một thư mục con theo mã công ty), dùng khi mẫu không có trong
    /// <c>SY_RpFTbl</c> (WinForms đọc thư mục <c>Reports</c> trên máy). Để trống = chỉ đọc mẫu trong DB.
    /// </summary>
    public string? ReportsRoot { get; set; }
}
