using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WebErp.Core.Auth;

/// <summary>
/// Mã xác thực 2 lớp TOTP (RFC 6238: HMAC-SHA1, 6 chữ số, bước 30 giây), dùng với Google Authenticator, Microsoft Authenticator…
/// Chấp nhận lệch ±1 bước (đồng hồ điện thoại lệch). Mỗi bước chỉ dùng được một lần (chống dùng lại mã vừa bị nhìn trộm).
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--) { counter[i] = (byte)(step & 0xFF); step >>= 8; }
        var hash = HMACSHA1.HashData(secret, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>Kiểm mã. Trả bước đã khớp (để lưu làm "bước cuối đã dùng"), hoặc null.</summary>
    /// <param name="lastUsedStep">Bước của mã hợp lệ lần trước; mã ở bước này hoặc cũ hơn bị từ chối.</param>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long lastUsedStep)
    {
        if (code is null) return null;
        code = code.Trim().Replace(" ", "", StringComparison.Ordinal);
        if (code.Length != Digits || !code.All(char.IsAsciiDigit)) return null;
        var current = StepAt(now);
        for (var d = -1; d <= 1; d++)
        {
            var step = current + d;
            if (step <= lastUsedStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(code))) return step;
        }
        return null;
    }

    /// <summary>Địa chỉ <c>otpauth://</c> để ứng dụng xác thực quét (mã QR) hoặc nhập tay.</summary>
    public static string ProvisioningUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={Base32(secret)}" +
        $"&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    public static string Base32(byte[] data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5) { sb.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) sb.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
