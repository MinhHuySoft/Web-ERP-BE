using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WebErp.Core.Auth;
using WebErp.Tests.Support;

namespace WebErp.Tests.Admin;

/// <summary>
/// Trang quản trị thật (WebApplicationFactory) trên thư mục dữ liệu tạm. Header <c>X-Test-Remote-Ip</c> giả lập request từ xa
/// (mặc định TestServer coi là từ chính máy chủ).
/// </summary>
public sealed class AdminFixture : IDisposable
{
    public const string AdminHost = "admin.example.test";
    public const string UserName = "quan.tri";
    public const string Password = "Quan-Tri-2026";

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "weberp-adminweb-" + Guid.NewGuid().ToString("N"));
    public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public WebApplicationFactory<WebErp.Admin.Program> Factory { get; }
    public byte[]? Secret { get; set; }

    public AdminFixture()
    {
        Environment.SetEnvironmentVariable("WebErp__Root", Root);
        Environment.SetEnvironmentVariable("WebErp__FrontendDir", Path.Combine(Root, "no-frontend"));
        Environment.SetEnvironmentVariable("WebErp__Admin__Hosts__0", AdminHost);
        Factory = new WebApplicationFactory<WebErp.Admin.Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureServices(s =>
            {
                s.AddSingleton<TimeProvider>(Clock);
                s.AddSingleton<IStartupFilter, RemoteIpFilter>();
            });
        });
        _ = Factory.Services;
        Environment.SetEnvironmentVariable("WebErp__Admin__Hosts__0", null);
    }

    public HttpClient Client(string host = "localhost") =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri($"http://{host}") });

    /// <summary>Mã TOTP của bước kế tiếp (tiến đồng hồ 30 giây để mã chưa từng dùng).</summary>
    public string NextCode()
    {
        Clock.Advance(TimeSpan.FromSeconds(Totp.StepSeconds));
        return Totp.Code(Secret!, Totp.StepAt(Clock.GetUtcNow()));
    }

    public static string Token(string html) => Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;

    public static string Hidden(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, $"name=\"{name}\" value=\"([^\"]+)\"").Groups[1].Value);

    /// <summary>Lần đầu: đọc khóa TOTP trong form thiết lập (giải mã như server) rồi tạo tài khoản và đăng nhập.</summary>
    public async Task<HttpResponseMessage> SetupAsync(HttpClient c)
    {
        var page = await c.GetStringAsync("/admin/login");
        var setup = Hidden(page, "__setup");
        var dp = Factory.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("WebErp.Admin.Setup.v1").ToTimeLimitedDataProtector();
        Secret = Convert.FromBase64String(dp.Unprotect(setup));
        return await c.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserName"] = UserName, ["Password"] = Password, ["Password2"] = Password,
            ["Code"] = Totp.Code(Secret, Totp.StepAt(Clock.GetUtcNow())), ["__setup"] = setup, ["__RequestVerificationToken"] = Token(page),
        }));
    }

    public async Task<HttpResponseMessage> LoginAsync(HttpClient c, string? code = null)
    {
        var page = await c.GetStringAsync("/admin/login");
        return await c.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserName"] = UserName, ["Password"] = Password, ["Code"] = code ?? NextCode(), ["__RequestVerificationToken"] = Token(page),
        }));
    }

    public static HttpRequestMessage Fe(HttpMethod m, string url, string? remoteIp = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("X-ERP-Request", "fragment");
        if (remoteIp is not null) r.Headers.Add("X-Test-Remote-Ip", remoteIp);
        return r;
    }

    public void Dispose()
    {
        Factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }

    private sealed class RemoteIpFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext ctx, Func<Task> n) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Test-Remote-Ip", out var ip)) ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                return n();
            });
            next(app);
        };
    }
}
