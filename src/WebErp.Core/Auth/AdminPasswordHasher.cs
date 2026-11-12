using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebErp.Core.Auth;

/// <summary>
/// Mật khẩu tài khoản quản trị (KHÔNG phải tài khoản ERP): PBKDF2-SHA256, 600.000 vòng, muối 16 byte (QĐ-07 hệ quả 7).
/// Định dạng lưu: <c>pbkdf2-sha256$&lt;vòng&gt;$&lt;muối base64&gt;$&lt;hash base64&gt;</c>.
/// </summary>
public static class AdminPasswordHasher
{
    public const int MinLength = 10;
    private const int Iterations = 600_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Lý do mật khẩu chưa đủ mạnh, hoặc null nếu đạt.</summary>
    public static string? Weakness(string password)
    {
        if (password.Length < MinLength) return $"Mật khẩu quản trị phải từ {MinLength} ký tự.";
        var kinds = new[] { password.Any(char.IsLower), password.Any(char.IsUpper), password.Any(char.IsDigit), password.Any(c => !char.IsLetterOrDigit(c)) };
        return kinds.Count(k => k) < 3 ? "Mật khẩu quản trị cần ít nhất 3 trong 4 loại: chữ thường, chữ hoa, chữ số, ký tự đặc biệt." : null;
    }
}
