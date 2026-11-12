using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebErp.Core.Auth;

/// <summary>
/// Hash mật khẩu tương thích ERP.NET (QĐ-01.3, tài liệu 12 §2.5). Viết lại từ mô tả thuật toán, không dùng mã của
/// <c>Tools.dll</c>.
/// <list type="number">
/// <item><c>S = UPPER(userName) + key + password</c>. <c>key</c> là hằng 6 ký tự của hệ cũ. Hằng này lấy từ cấu hình
/// máy chủ, <b>không</b> có trong repo.</item>
/// <item>Muối: 4 chữ số, mỗi chữ số 0–8.</item>
/// <item><c>h = HEX_UPPER(MD5(UTF8(S + muối)))</c> (32 ký tự). Chèn muối vào <c>h</c> tại vị trí <c>p</c> ∈ [4, 31].</item>
/// <item>Chèn <c>p</c> dạng 2 chữ số vào vị trí 3. Tổng 38 ký tự.</item>
/// </list>
/// Không có "mật khẩu hệ thống theo ngày" hay "lỗi thì cho qua" như WinForms (QĐ-03, ngoại lệ bảo mật).
/// </summary>
public static class LegacyPassword
{
    public const int HashLength = 38;

    /// <summary>Kiểm mật khẩu. Hash sai định dạng → false (WinForms cũng đăng nhập thất bại).</summary>
    public static bool Verify(string userName, string password, string? storedHash, string key)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (storedHash is null || storedHash.Length != HashLength) return false;
        if (!int.TryParse(storedHash.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var position)) return false;

        var withoutPosition = storedHash.Remove(3, 2);
        if (position < 0 || position + 4 > withoutPosition.Length) return false;
        var salt = withoutPosition.Substring(position, 4);
        var expected = withoutPosition.Remove(position, 4);

        var computed = Digest(userName, password, key, salt);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(expected));
    }

    /// <summary>
    /// Tạo hash định dạng cũ, để WinForms vẫn đăng nhập được sau khi đổi mật khẩu trên web.
    /// <paramref name="salt"/> và <paramref name="position"/> chỉ dùng cho test.
    /// </summary>
    public static string Encode(string userName, string password, string key, string? salt = null, int? position = null)
    {
        ArgumentNullException.ThrowIfNull(userName);
        ArgumentNullException.ThrowIfNull(password);
        ArgumentException.ThrowIfNullOrEmpty(key);
        salt ??= string.Concat(Enumerable.Range(0, 4).Select(_ => (char)('0' + RandomNumberGenerator.GetInt32(0, 9))));
        if (salt.Length != 4 || !salt.All(char.IsAsciiDigit)) throw new ArgumentException("Muối phải gồm 4 chữ số.", nameof(salt));
        var p = position ?? RandomNumberGenerator.GetInt32(4, 32);
        if (p is < 4 or > 31) throw new ArgumentOutOfRangeException(nameof(position), "Vị trí muối phải trong [4, 31].");

        var withSalt = Digest(userName, password, key, salt).Insert(p, salt);
        return withSalt.Insert(3, p.ToString("00", CultureInfo.InvariantCulture));
    }

    private static string Digest(string userName, string password, string key, string salt)
    {
        // Hệ cũ dùng ToUpper() theo culture của máy trạm. Tên đăng nhập ERP là ASCII nên ToUpperInvariant cho cùng kết quả.
        var s = userName.ToUpperInvariant() + key + password + salt;
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s)));
    }
}
