using System.Globalization;
using System.Text;

namespace WebErp.Erp.Write;

/// <summary>
/// Mã dòng giống WinForms (QĐ-01: web ghi phải không phân biệt được với WinForms; đặc tả M4 §3.1):
/// <list type="bullet">
/// <item><c>UserAutoID</c> = 25 chữ số: <c>yyMMddHHmmss</c> (giờ địa phương của công ty) + mili giây + mili giây sau ~10 ms +
/// ba số ngẫu nhiên (0–8888, 0–7777, 0–6789) + đệm <c>0</c>, cắt 25 (<c>Functions.GetCurrentUserAutoID</c>).</item>
/// <item><c>UID</c> = 19 chữ số đầu của một UserAutoID mới đổi sang cơ số 35, chữ số hàng thấp trước, chữ số cuối lặp lại đúng như
/// mã gốc (<c>Functions.GetUserAutoIDForUID</c>), phép chia dùng số thực như VB.</item>
/// </list>
/// Không sleep 10 ms như WinForms: mili giây thứ hai lấy bằng mili giây đầu + 10..15.
/// </summary>
public sealed class ErpIds(TimeProvider time, TimeZoneInfo zone)
{
    private readonly Lock _gate = new();
    private string _last = "";

    /// <summary>Giờ Việt Nam (SE Asia Standard Time / Asia/Ho_Chi_Minh), dùng khi công ty không đặt múi giờ khác.</summary>
    public static TimeZoneInfo VietnamZone { get; } = FindZone();

    public string NewUserAutoId()
    {
        lock (_gate)
        {
            while (true)
            {
                var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
                var ms1 = now.Millisecond;
                var ms2 = (ms1 + 10 + Random.Shared.Next(0, 6)) % 1000;
                var sb = new StringBuilder(48)
                    .Append(now.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture))
                    .Append(ms1.ToString("000", CultureInfo.InvariantCulture))
                    .Append(ms2.ToString("000", CultureInfo.InvariantCulture))
                    .Append(Rnd(8888)).Append(Rnd(7777)).Append(Rnd(6789))
                    .Append("00000000000");
                var id = sb.ToString(0, 25);
                if (id != _last)
                {
                    _last = id;
                    return id;
                }
            }
        }
    }

    public string NewUid() => Base35(long.Parse(NewUserAutoId()[..19], CultureInfo.InvariantCulture));

    /// <summary>Cơ số 35 theo đúng mã gốc (gồm cả chỗ lặp chữ số cuối và phép chia bằng số thực).</summary>
    internal static string Base35(long n)
    {
        var sb = new StringBuilder(16);
        int d;
        do
        {
            d = (int)(n % 35);
            n = (long)Math.Round((double)(n - d) / 35d);
            sb.Append(Digit(d));
        }
        while (n > 35);
        return sb.Append(Digit(d)).ToString();
    }

    private static char Digit(int d) => d < 10 ? (char)('0' + d) : (char)(d + 55);

    private static string Rnd(int max) => ((int)Math.Round(Random.Shared.NextSingle() * max)).ToString(CultureInfo.InvariantCulture);

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "SE Asia Standard Time", "Asia/Ho_Chi_Minh", "Asia/Bangkok" })
        {
            if (TimeZoneInfo.TryFindSystemTimeZoneById(id, out var z)) return z;
        }
        return TimeZoneInfo.CreateCustomTimeZone("UTC+07", TimeSpan.FromHours(7), "UTC+07", "UTC+07");
    }
}
