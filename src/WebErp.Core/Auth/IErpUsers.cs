using WebErp.Core.Tenancy;

namespace WebErp.Core.Auth;

/// <summary>Đọc người dùng từ DB ERP của một công ty. Chỉ đọc.</summary>
public interface IErpUsers
{
    /// <summary>Người dùng đang hoạt động (<c>Disable = 0</c>) kèm hash mật khẩu; null nếu không có.</summary>
    Task<ErpLoginRecord?> FindForLoginAsync(Tenant tenant, string userName, CancellationToken ct);

    /// <summary>Người dùng còn hoạt động không (dùng khi kiểm cookie, có cache ở Host).</summary>
    Task<bool> IsEnabledAsync(Tenant tenant, string userName, CancellationToken ct);
}
