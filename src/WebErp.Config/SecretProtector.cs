using Microsoft.AspNetCore.DataProtection;

namespace WebErp.Config;

/// <summary>
/// Mã hóa chuỗi kết nối trong SQLite bằng Data Protection (QĐ-07 hệ quả 5). Mất khóa (thư mục <c>keys</c> + chứng chỉ) là mất
/// mọi chuỗi kết nối, nên phải sao lưu khóa cùng tệp cấu hình.
/// </summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("WebErp.Config.ConnectionString.v1");

    public string Protect(string plain) => _protector.Protect(plain);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}
