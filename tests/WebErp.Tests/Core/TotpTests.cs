using System.Text;
using WebErp.Core.Auth;

namespace WebErp.Tests.Core;

public sealed class TotpTests
{
    // Bộ số kiểm thử của RFC 6238, phụ lục B (SHA1, khóa ASCII "12345678901234567890"), lấy 6 chữ số cuối.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    public void Matches_rfc6238_vectors(long unix, string expected) =>
        Assert.Equal(expected, Totp.Code(RfcSecret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unix))));

    [Fact]
    public void Accepts_one_step_of_clock_drift_and_rejects_replay()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);
        Assert.Equal(step, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step), now, 0));
        Assert.Equal(step - 1, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 1), now, 0));
        Assert.Equal(step + 1, Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step + 1), now, 0));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step + 2), now, 0));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step), now, lastUsedStep: step)); // đã dùng
        Assert.Null(Totp.Verify(RfcSecret, "12345", now, 0));
        Assert.Null(Totp.Verify(RfcSecret, "abcdef", now, 0));
        Assert.Equal(step, Totp.Verify(RfcSecret, " " + Totp.Code(RfcSecret, step)[..3] + " " + Totp.Code(RfcSecret, step)[3..], now, 0));
    }

    [Fact]
    public void Base32_and_provisioning_uri_follow_the_key_uri_format()
    {
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.Base32(RfcSecret));
        var uri = Totp.ProvisioningUri("WebErp admin.example.test", "quan.tri", RfcSecret);
        Assert.StartsWith("otpauth://totp/WebErp%20admin.example.test:quan.tri?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", uri);
        Assert.Contains("&digits=6&period=30", uri);
    }

    [Fact]
    public void Admin_password_hash_round_trips_and_strength_is_checked()
    {
        var hash = AdminPasswordHasher.Hash("Quan-Tri-2026");
        Assert.StartsWith("pbkdf2-sha256$600000$", hash);
        Assert.True(AdminPasswordHasher.Verify("Quan-Tri-2026", hash));
        Assert.False(AdminPasswordHasher.Verify("quan-tri-2026", hash));
        Assert.NotNull(AdminPasswordHasher.Weakness("ngan"));
        Assert.NotNull(AdminPasswordHasher.Weakness("chuthuongdai"));
        Assert.Null(AdminPasswordHasher.Weakness("Quan-Tri-2026"));
    }
}
