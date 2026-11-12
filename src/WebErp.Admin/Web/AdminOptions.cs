namespace WebErp.Admin.Web;

/// <summary>Cấu hình trang quản trị (mục <c>WebErp</c> và <c>WebErp:Admin</c>).</summary>
public sealed class AdminOptions
{
    /// <summary>Thư mục FE (<c>web/</c>). Trống = <c>wwwroot</c> của site (bản đóng gói).</summary>
    public string? FrontendDir { get; set; }

    public string? DataProtectionCertificateThumbprint { get; set; }

    public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";

    /// <summary>
    /// Tên miền được mở trang quản trị từ xa (vd <c>admin.bms7.net</c>). Ngoài các tên này chỉ mở được từ chính VPS
    /// (127.0.0.1 / localhost). Từ xa bắt buộc HTTPS.
    /// </summary>
    public string[] Hosts { get; set; } = [];

    /// <summary>Hết hạn khi rảnh.</summary>
    public int IdleMinutes { get; set; } = 30;

    /// <summary>Hạn tối đa của một lần đăng nhập.</summary>
    public int MaxHours { get; set; } = 8;
}
