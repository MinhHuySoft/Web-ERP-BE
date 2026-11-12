using Microsoft.AspNetCore.DataProtection;

namespace WebErp.Config;

/// <summary>
/// Mã hóa bí mật trong SQLite bằng Data Protection (QĐ-07 hệ quả 5): chuỗi kết nối, cài đặt chung, khóa TOTP. Mỗi loại một
/// "purpose" riêng, nên giá trị của loại này không giải mã được thành loại khác. Mất khóa (thư mục <c>keys</c> + chứng chỉ) là
/// mất mọi bí mật, nên phải sao lưu khóa cùng tệp cấu hình.
/// </summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _connection = provider.CreateProtector("WebErp.Config.ConnectionString.v1");
    private readonly IDataProtector _setting = provider.CreateProtector("WebErp.Config.Setting.v1");
    private readonly IDataProtector _totp = provider.CreateProtector("WebErp.Config.AdminTotp.v1");

    public string Protect(string plain) => _connection.Protect(plain);

    public string Unprotect(string protectedValue) => _connection.Unprotect(protectedValue);

    public string ProtectSetting(string plain) => _setting.Protect(plain);

    public string UnprotectSetting(string protectedValue) => _setting.Unprotect(protectedValue);

    public string ProtectTotp(byte[] secret) => _totp.Protect(Convert.ToBase64String(secret));

    public byte[] UnprotectTotp(string protectedValue) => Convert.FromBase64String(_totp.Unprotect(protectedValue));
}

/// <summary>Tên các cài đặt chung (bảng <c>setting</c>).</summary>
public static class SettingNames
{
    /// <summary>Hằng 6 ký tự của thuật toán mật khẩu ERP (tài liệu 12 §2.5). Thay cho biến môi trường của app pool.</summary>
    public const string LegacyPasswordKey = "legacy-password-key";
}
