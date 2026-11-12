using WebErp.Core.Tenancy;

namespace WebErp.Core.Menu;

/// <summary>Đọc menu mà người dùng được phép thấy. Chỉ đọc.</summary>
public interface IErpMenu
{
    /// <param name="language"><c>vi</c>, <c>en</c> hoặc <c>zh</c>.</param>
    Task<IReadOnlyList<MenuEntry>> GetUserMenuAsync(Tenant tenant, string userName, string language, CancellationToken ct);
}
