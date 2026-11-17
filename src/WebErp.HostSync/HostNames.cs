using System.Net;
using System.Text.RegularExpressions;

namespace WebErp.HostSync;

/// <summary>Kiểm tên miền trước khi đưa vào IIS / win-acme / netsh. Chặt hơn kiểm của trang quản trị: phải là tên công khai.</summary>
public static partial class HostNames
{
    [GeneratedRegex("[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Email();

    /// <summary>Che địa chỉ email trong thông điệp lỗi trước khi lưu / hiển thị.</summary>
    public static string MaskEmails(string s) => Email().Replace(s, "***@***");

    // Chữ thường, a-z0-9-., mỗi nhãn 1-63 ký tự, không bắt đầu / kết thúc bằng '-', TLD chỉ chữ cái, tổng <= 253.
    [GeneratedRegex("^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicHost();

    /// <summary>
    /// true nếu <paramref name="host"/> đúng là tên miền công khai agent được phép đồng bộ: đã chuẩn hóa (chữ thường), không
    /// wildcard, không cổng, không phải IP, không phải localhost.
    /// </summary>
    public static bool IsSyncable(string? host) =>
        host is not null && PublicHost().IsMatch(host) && !IPAddress.TryParse(host, out _)
        && !host.EndsWith(".localhost", StringComparison.Ordinal) && !host.EndsWith(".local", StringComparison.Ordinal);
}
