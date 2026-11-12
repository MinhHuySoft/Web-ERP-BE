using System.Text.RegularExpressions;

namespace WebErp.Core.Tenancy;

/// <summary>Kiểm tra định dạng các tên do cấu hình cung cấp, trước khi chúng đi vào URL, cookie hay tên đối tượng SQL.</summary>
public static partial class TenantNames
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex KeyPattern();

    // Tiền tố module ghép vào tên đối tượng SQL (vd SY_GetUserMenuFnc): chỉ cho chữ cái, để không thể chèn SQL.
    [GeneratedRegex("^[A-Za-z]{2,5}$")]
    private static partial Regex ModulePattern();

    [GeneratedRegex("^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)(\\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostPattern();

    public static bool IsValidKey(string? key) => key is not null && KeyPattern().IsMatch(key);

    public static bool IsValidModule(string? module) => module is not null && ModulePattern().IsMatch(module);

    /// <summary>Chuẩn hóa host: chữ thường, bỏ dấu chấm cuối, bỏ cổng.</summary>
    public static string NormalizeHost(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        var colon = h.LastIndexOf(':');
        if (colon > 0 && !h.Contains(']', StringComparison.Ordinal)) h = h[..colon];
        return h.TrimEnd('.');
    }

    public static bool IsValidHost(string? host) => host is not null && HostPattern().IsMatch(NormalizeHost(host));
}
