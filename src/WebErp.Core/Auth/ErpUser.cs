namespace WebErp.Core.Auth;

/// <summary>Người dùng ERP (bảng <c>SY_User</c>, tài liệu 12 §5.1). Không bao giờ chứa hash mật khẩu.</summary>
public sealed record ErpUser(string UserName, string FullName, string? UserGroupId, string? BranchId);

/// <summary>Kết quả đọc để đăng nhập: người dùng + hash lưu trong DB. Chỉ dùng trong bước kiểm mật khẩu.</summary>
public sealed record ErpLoginRecord(ErpUser User, string? PasswordHash);
