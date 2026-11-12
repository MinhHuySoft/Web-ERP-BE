namespace WebErp.Core.Tenancy;

/// <summary>
/// Các công ty phục vụ ở host của request hiện tại (một domain có thể có nhiều công ty, QĐ-07).
/// Công ty đang dùng của người dùng lấy từ cookie và PHẢI thuộc danh sách này.
/// </summary>
public sealed class HostTenants(string host, IReadOnlyList<Tenant> tenants)
{
    public string Host { get; } = host;

    /// <summary>Chỉ các công ty đang bật.</summary>
    public IReadOnlyList<Tenant> Tenants { get; } = tenants.Where(t => t.Enabled).ToList();

    public Tenant? Find(string? key) =>
        key is null ? null : Tenants.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));
}
