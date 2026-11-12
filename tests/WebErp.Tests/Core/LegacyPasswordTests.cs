using System.Security.Cryptography;
using System.Text;
using WebErp.Core.Auth;

namespace WebErp.Tests.Core;

public sealed class LegacyPasswordTests
{
    // Khóa GIẢ chỉ dùng cho test. Khóa thật của hệ cũ không bao giờ nằm trong repo.
    private const string Key = "TEST01";

    [Fact]
    public void Encode_builds_the_documented_format()
    {
        var hash = LegacyPassword.Encode("admin", "123", Key, salt: "4071", position: 12);

        // Dựng lại bằng tay đúng các bước ở tài liệu 12 §2.5.
        var md5 = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("ADMIN" + Key + "123" + "4071")));
        var expected = md5.Insert(12, "4071").Insert(3, "12");
        Assert.Equal(expected, hash);
        Assert.Equal(LegacyPassword.HashLength, hash.Length);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(31)]
    public void Verify_accepts_every_salt_position(int position)
    {
        var hash = LegacyPassword.Encode("NguyenVanA", "Mật-khẩu 1", Key, salt: "8888", position: position);
        Assert.True(LegacyPassword.Verify("NguyenVanA", "Mật-khẩu 1", hash, Key));
    }

    [Fact]
    public void Verify_ignores_user_name_case_like_the_legacy_system()
    {
        var hash = LegacyPassword.Encode("Admin", "abc", Key);
        Assert.True(LegacyPassword.Verify("ADMIN", "abc", hash, Key));
        Assert.True(LegacyPassword.Verify("admin", "abc", hash, Key));
    }

    [Fact]
    public void Verify_rejects_wrong_password_key_or_format()
    {
        var hash = LegacyPassword.Encode("admin", "abc", Key);
        Assert.False(LegacyPassword.Verify("admin", "abd", hash, Key));
        Assert.False(LegacyPassword.Verify("admin", "abc", hash, "OTHER1"));
        Assert.False(LegacyPassword.Verify("other", "abc", hash, Key));
        Assert.False(LegacyPassword.Verify("admin", "abc", null, Key));
        Assert.False(LegacyPassword.Verify("admin", "abc", "plain-text", Key));
        Assert.False(LegacyPassword.Verify("admin", "abc", hash[..3] + "99" + hash[5..], Key));
        Assert.False(LegacyPassword.Verify("admin", "abc", hash[..3] + "xx" + hash[5..], Key));
    }

    [Fact]
    public void Empty_password_has_a_valid_hash()
    {
        // WinForms đổi mật khẩu rỗng thành hash của chuỗi rỗng (tài liệu 12 §2.5).
        var hash = LegacyPassword.Encode("user1", "", Key);
        Assert.True(LegacyPassword.Verify("user1", "", hash, Key));
    }

    [Fact]
    public void Random_salt_digits_are_0_to_8()
    {
        for (var i = 0; i < 200; i++)
        {
            var hash = LegacyPassword.Encode("u", "p", Key);
            var p = int.Parse(hash.AsSpan(3, 2));
            var salt = hash.Remove(3, 2).Substring(p, 4);
            Assert.All(salt, ch => Assert.InRange(ch, '0', '8'));
            Assert.InRange(p, 4, 31);
        }
    }
}
