namespace WebErp.HostSync;

/// <summary>Cấu hình agent (mục <c>WebErp:HostSync</c>; biến môi trường <c>WebErp__HostSync__PublicIp</c>…).</summary>
public sealed class HostSyncOptions
{
    /// <summary>Site IIS duy nhất agent được sửa.</summary>
    public string SiteName { get; set; } = "WebErp";

    /// <summary>IP công khai của VPS: bản ghi A của domain phải đúng bằng IP này.</summary>
    public string PublicIp { get; set; } = "103.232.122.205";

    public string DnsServer { get; set; } = "1.1.1.1";

    public string WacsPath { get; set; } = @"C:\win-acme\wacs.exe";

    /// <summary>Thư mục renewal của win-acme. Trống: <c>%ProgramData%\win-acme\acme-v02.api.letsencrypt.org</c>.</summary>
    public string? WacsConfigDir { get; set; }

    public string CertificateStore { get; set; } = "WebHosting";

    public int WacsTimeoutMinutes { get; set; } = 5;

    /// <summary>Chu kỳ kiểm "có gì thay đổi" (phiên bản cấu hình, nút Đồng bộ lại ngay).</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>Chạy đủ một vòng ít nhất mỗi chừng này giây dù không có thay đổi.</summary>
    public int FullSyncSeconds { get; set; } = 60;

    /// <summary>Gỡ nhiều hơn số domain này trong một vòng thì dừng lại, chờ quản trị viên xác nhận.</summary>
    public int MaxRemovalsWithoutApproval { get; set; } = 2;

    /// <summary>Trang kiểm sau khi gắn chứng chỉ (phải trả 200).</summary>
    public string ProbePath { get; set; } = "/erp/login";

    /// <summary>Domain đang hoạt động được kiểm lại chứng chỉ mỗi chừng này phút.</summary>
    public int CheckIntervalMinutes { get; set; } = 60;

    /// <summary>Chứng chỉ còn ít hơn chừng này ngày thì báo "sắp hết hạn".</summary>
    public int ExpiryWarningDays { get; set; } = 14;

    /// <summary>Domain agent bỏ qua hoàn toàn (vd domain do site khác quản lý).</summary>
    public string[] IgnoreHosts { get; set; } = [];

    public string ResolvedWacsConfigDir => string.IsNullOrWhiteSpace(WacsConfigDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "win-acme", "acme-v02.api.letsencrypt.org")
        : WacsConfigDir;
}
