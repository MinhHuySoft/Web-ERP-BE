namespace WebErp.Host;

/// <summary>Cấu hình mục <c>WebErp</c>. Bí mật (khóa mật khẩu kiểu cũ) đặt bằng biến môi trường của app pool, không trong tệp.</summary>
public sealed class WebErpOptions
{
    /// <summary>Thư mục dữ liệu (data, keys, files…). Xem <see cref="Config.WebErpPaths"/>.</summary>
    public string Root { get; set; } = "";

    /// <summary>Thư mục FE (<c>web/</c> của repo FE). Trống = <c>wwwroot</c> của site (bản đóng gói).</summary>
    public string? FrontendDir { get; set; }

    public string AppName { get; set; } = "ERP Web";

    /// <summary>Thumbprint chứng chỉ bọc khóa Data Protection (LocalMachine\My). Nên đặt trên VPS.</summary>
    public string? DataProtectionCertificateThumbprint { get; set; }

    /// <summary>Cookie chỉ gửi qua HTTPS. Chỉ tắt khi chạy thử trên máy dev.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Múi giờ tính "kỳ" mặc định của phiên (WinForms: tháng hiện tại khi đăng nhập).</summary>
    public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";

    public LegacyPasswordOptions LegacyPassword { get; set; } = new();
    public SessionOptions Session { get; set; } = new();
    public LoginOptions Login { get; set; } = new();
    public ForwardedOptions Forwarded { get; set; } = new();
}

public sealed class LegacyPasswordOptions
{
    /// <summary>Hằng 6 ký tự của hệ cũ (tài liệu 12 §2.5). Đặt bằng biến môi trường <c>WebErp__LegacyPassword__Key</c>.</summary>
    public string? Key { get; set; }
}

public sealed class SessionOptions
{
    /// <summary>Hết hạn khi rảnh (trượt).</summary>
    public int IdleMinutes { get; set; } = 60;

    /// <summary>Hạn tối đa tính từ lúc đăng nhập, dù vẫn đang làm.</summary>
    public int MaxHours { get; set; } = 12;

    /// <summary>Bao lâu kiểm lại user còn hoạt động trong <c>SY_User</c> (cache, để không hỏi DB khách mỗi request).</summary>
    public int UserCheckSeconds { get; set; } = 60;

    /// <summary>Thời gian giữ menu đã đọc (WinForms cache 5 phút, tài liệu 12 §5.3).</summary>
    public int MenuCacheMinutes { get; set; } = 5;
}

public sealed class LoginOptions
{
    /// <summary>Số lần sai liên tiếp của một tài khoản trước khi khóa tạm.</summary>
    public int MaxFailures { get; set; } = 5;

    public int LockMinutes { get; set; } = 5;
}

public sealed class ForwardedOptions
{
    /// <summary>Bật khi có reverse proxy / load balancer trước IIS (tài liệu 05, bậc 2). Mặc định tắt.</summary>
    public bool Enabled { get; set; }

    /// <summary>IP của proxy được tin. Chỉ header từ các IP này mới được dùng.</summary>
    public string[] KnownProxies { get; set; } = [];
}
