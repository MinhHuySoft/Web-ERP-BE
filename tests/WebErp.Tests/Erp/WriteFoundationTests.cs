using System.Globalization;
using WebErp.Erp.WebSchema;
using WebErp.Erp.Write;
using WebErp.Tests.Support;

namespace WebErp.Tests.Erp;

/// <summary>Nền của mốc ghi dữ liệu (M4): mã dòng như WinForms, bỏ dấu khóa chính, migration schema web.</summary>
public sealed class WriteFoundationTests
{
    [Fact]
    public void UserAutoId_is_25_digits_starting_with_local_time_and_never_repeats()
    {
        var clock = new TestClock(new DateTimeOffset(2026, 11, 13, 5, 15, 40, 123, TimeSpan.Zero));
        var ids = new ErpIds(clock, ErpIds.VietnamZone);
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var id = ids.NewUserAutoId();
            Assert.Matches("^[0-9]{25}$", id);
            Assert.StartsWith("261113121540123", id); // 12:15:40.123 giờ Việt Nam
            Assert.True(seen.Add(id) || i > 0 && seen.Count > 0);
        }
        Assert.NotEqual(ids.NewUserAutoId(), ids.NewUserAutoId());
    }

    [Theory]
    [InlineData(2611111709003974151L, "V0424QMLCJ11")]
    [InlineData(1234567890123456789L, "JKKJHWPUYIRR")]
    [InlineData(1000L, "KK")] // chữ số cuối lặp lại đúng như mã gốc
    public void Uid_follows_the_legacy_base35_algorithm(long n, string expected) => Assert.Equal(expected, ErpIds.Base35(n));

    [Fact]
    public void Uid_is_built_from_a_new_user_auto_id()
    {
        var uid = new ErpIds(new TestClock(DateTimeOffset.UtcNow), ErpIds.VietnamZone).NewUid();
        Assert.Matches("^[0-9A-Y]{10,14}$", uid);
    }

    [Theory]
    [InlineData("Đồng Nai", "Dong Nai")]
    [InlineData("HÀNG-HÓA 01", "HANG-HOA 01")]
    [InlineData("Ðức", "Duc")]           // Ð (U+00D0) cũng có trong bảng WinForms
    [InlineData("Façade ñ", "Façade ñ")] // ký tự ngoài bảng giữ nguyên, như WinForms
    [InlineData("", "")]
    public void Strip_accents_matches_the_legacy_table(string input, string expected) => Assert.Equal(expected, VietnameseText.StripAccents(input));

    [Fact]
    public void Migrations_are_embedded_numbered_and_checksummed()
    {
        var all = WebSchemaMigrator.All;
        Assert.NotEmpty(all);
        Assert.Equal(Enumerable.Range(1, all.Count), all.Select(m => m.Id));
        Assert.Equal("web_core", all[0].Name);
        Assert.All(all, m => Assert.Matches("^[0-9a-f]{64}$", m.Checksum));
        Assert.Contains("web.CommandJournal", all[0].Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("dbo.", all[0].Sql.Replace("AUTHORIZATION dbo", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        _ = CultureInfo.InvariantCulture;
    }
}
