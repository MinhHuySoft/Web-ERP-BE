using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using WebErp.Core.Auth;

namespace WebErp.ContractTests;

/// <summary>Trang quản trị thật, dữ liệu tạm: thiết lập quản trị viên, thêm một công ty, để có đủ các kiểu trang.</summary>
public sealed class AdminSite : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "weberp-admincontract-" + Guid.NewGuid().ToString("N"));
    private byte[]? _secret;
    private long _step;

    public WebApplicationFactory<WebErp.Admin.Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;

    /// <summary>Trang thiết lập lần đầu (trước khi có quản trị viên).</summary>
    public string SetupPage { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        Environment.SetEnvironmentVariable("WebErp__Root", _root);
        Environment.SetEnvironmentVariable("WebErp__FrontendDir", Path.Combine(_root, "no-frontend"));
        Factory = new WebApplicationFactory<WebErp.Admin.Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

        SetupPage = await Client.GetStringAsync("/admin/login");
        var setup = Hidden(SetupPage, "__setup");
        _secret = Convert.FromBase64String(Factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("WebErp.Admin.Setup.v1").ToTimeLimitedDataProtector().Unprotect(setup));
        _step = Totp.StepAt(DateTimeOffset.UtcNow);
        await Post("/admin/login", SetupPage, new() { ["UserName"] = "quan.tri", ["Password"] = "Quan-Tri-2026", ["Password2"] = "Quan-Tri-2026", ["Code"] = Code(), ["__setup"] = setup });

        var newPage = await Client.GetStringAsync("/admin/tenants/new");
        await Post("/admin/tenants/new", newPage, new()
        {
            ["Key"] = "MINHHUY", ["Name"] = "Công ty thử", ["Module"] = "SY", ["Hosts"] = "minhhuy.bms7.net", ["Enabled"] = "true",
            ["Connection"] = "Server=sql.example.test,14333;Database=ErpDemo;User ID=u;Password=p", ["Code"] = Code(),
        });
    }

    /// <summary>Mã TOTP mỗi lần một bước khác (cửa sổ cho phép lệch +1 bước).</summary>
    private string Code() => Totp.Code(_secret!, _step++);

    private async Task Post(string url, string page, Dictionary<string, string> form)
    {
        form["__RequestVerificationToken"] = Hidden(page, "__RequestVerificationToken");
        var r = await Client.PostAsync(url, new FormUrlEncodedContent(form));
        if ((int)r.StatusCode >= 400) throw new InvalidOperationException($"{url} trả {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
    }

    public async Task<string> Get(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-ERP-Request", "fragment");
        var r = await Client.SendAsync(req);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"{url} trả {(int)r.StatusCode}");
        return await r.Content.ReadAsStringAsync();
    }

    private static string Hidden(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html, $"name=\"{name}\" value=\"([^\"]+)\"").Groups[1].Value);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Mọi trang của trang quản trị phải qua validator ERP-HTML của FE: 0 lỗi, 0 cảnh báo.</summary>
public sealed class AdminContractTests(FrontendValidator validator, AdminSite site) : IClassFixture<FrontendValidator>, IClassFixture<AdminSite>
{
    [Theory]
    [InlineData("/admin/login")]
    [InlineData("/admin/session")]
    [InlineData("/admin/menu")]
    [InlineData("/admin/home")]
    [InlineData("/admin/tenants")]
    [InlineData("/admin/tenants/new")]
    [InlineData("/admin/tenants/MINHHUY")]
    [InlineData("/admin/tenants/MINHHUY/connection")]
    [InlineData("/admin/tenants/MINHHUY/maintenance")]
    [InlineData("/admin/tenants/MINHHUY/logout-all")]
    [InlineData("/admin/settings")]
    [InlineData("/admin/audit")]
    public async Task Admin_page_passes_frontend_validator(string url) => await AssertValid(await site.Get(url));

    [Fact]
    public async Task First_time_setup_page_passes_frontend_validator() => await AssertValid(site.SetupPage);

    private async Task AssertValid(string html)
    {
        var issues = await validator.ValidateAsync(html);
        var errors = issues.Where(i => i.Level == "error").ToList();
        Assert.True(errors.Count == 0, "Vi phạm ERP-HTML:\n" + string.Join("\n", errors.Select(e => $"[{e.Rule}] {e.Msg} — {e.Where}")));
        Assert.DoesNotContain(issues, i => i.Level == "warning");
    }
}
