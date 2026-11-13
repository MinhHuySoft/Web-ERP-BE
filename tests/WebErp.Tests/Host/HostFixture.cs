using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using WebErp.Config;
using WebErp.Core.Auth;
using WebErp.Core.Errors;
using WebErp.Core.Menu;
using WebErp.Core.Metadata;
using WebErp.Core.Tenancy;
using WebErp.Tests.Support;

namespace WebErp.Tests.Host;

/// <summary>DB ERP giả trong bộ nhớ, thay cho <c>ErpUsers</c>/<c>ErpMenu</c> thật.</summary>
public sealed class FakeErp : IErpUsers, IErpMenu, IErpScreens
{
    public const string Key = "TEST01";
    public Dictionary<string, (ErpUser User, string Hash, bool Enabled)> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<MenuEntry> Menu { get; } = [];
    public bool Down { get; set; }

    public void AddUser(string tenant, string userName, string password, string fullName, bool enabled = true) =>
        Users[$"{tenant}|{userName}"] = (new ErpUser(userName, fullName, "G1", "HN"), LegacyPassword.Encode(userName, password, Key), enabled);

    public Task<ErpLoginRecord?> FindForLoginAsync(Tenant tenant, string userName, CancellationToken ct)
    {
        if (Down) throw new ErpUnavailableException(tenant.Key, new InvalidOperationException("down"));
        return Task.FromResult(Users.TryGetValue($"{tenant.Key}|{userName}", out var u) && u.Enabled ? new ErpLoginRecord(u.User, u.Hash) : null);
    }

    public Task<bool> IsEnabledAsync(Tenant tenant, string userName, CancellationToken ct) =>
        Task.FromResult(Users.TryGetValue($"{tenant.Key}|{userName}", out var u) && u.Enabled);

    public Task<IReadOnlyList<MenuEntry>> GetUserMenuAsync(Tenant tenant, string userName, string language, CancellationToken ct) =>
        Down ? throw new ErpUnavailableException(tenant.Key, new InvalidOperationException("down")) : Task.FromResult<IReadOnlyList<MenuEntry>>(Menu);

    /// <summary>Yêu cầu màn gần nhất (để test kiểm công ty/người dùng lấy từ phiên và query string).</summary>
    public ScreenRequest? LastScreen { get; private set; }

    public Task<ListView> OpenListAsync(ScreenRequest request, CancellationToken ct)
    {
        LastScreen = request;
        return Task.FromResult(request.FormName switch
        {
            "AP_PurposeRequestListFrm" => SampleScreens.List(),
            // Lỗi SQL có giá trị dữ liệu trong thông điệp: log không được chép thông điệp này.
            "NguoiDungFrm" => new ListView(ScreenStatus.ConfigError,
                Note: "Câu truy vấn dựng từ cấu hình form bị lỗi (SQL 245): Conversion failed when converting the varchar value 'GIA-TRI-RIENG' to data type int."),
            _ => new ListView(ScreenStatus.NotSupported),
        });
    }

    public Task<RecordView> OpenRecordAsync(ScreenRequest request, string key, CancellationToken ct) =>
        Task.FromResult(key == "PO/001" ? SampleScreens.Record(key) : new RecordView(ScreenStatus.NotFound));

    public Task<DetailView> OpenDetailAsync(ScreenRequest request, string key, string slotId, CancellationToken ct) =>
        Task.FromResult(slotId == "T1" ? SampleScreens.Detail() : new DetailView(ScreenStatus.NotFound));

    /// <summary>Tra cứu gần nhất: (ô, chữ gõ, tham số).</summary>
    public (string Field, string Text, IReadOnlyList<string> Paras)? LastLookup { get; private set; }

    public Task<LookupView> LookupAsync(ScreenRequest request, string field, string text, IReadOnlyList<string> paras, CancellationToken ct)
    {
        LastLookup = (field, text, paras);
        return Task.FromResult(field == "ObjectID" ? SampleScreens.Lookup() : new LookupView(ScreenStatus.NotFound));
    }
}

/// <summary>
/// Host thật (WebApplicationFactory) + cấu hình SQLite tạm + ERP giả. Program đọc cấu hình trước khi Build nên truyền thư mục
/// dữ liệu qua biến môi trường; vì vậy mọi test Host nằm chung một collection, chạy tuần tự.
/// </summary>
public sealed class HostFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "weberp-host-" + Guid.NewGuid().ToString("N"));
    public FakeErp Erp { get; } = new();
    public TestClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public CapturedLogs Logs { get; } = new();
    public WebApplicationFactory<Program> Factory { get; }
    public ConfigWriter Writer { get; }

    public HostFixture()
    {
        Environment.SetEnvironmentVariable("WebErp__Root", Root);
        Environment.SetEnvironmentVariable("WebErp__FrontendDir", Path.Combine(Root, "no-frontend"));
        Environment.SetEnvironmentVariable("WebErp__LegacyPassword__Key", FakeErp.Key);
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureLogging(l => l.AddProvider(Logs));
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IErpUsers>();
                s.RemoveAll<IErpMenu>();
                s.RemoveAll<IErpScreens>();
                s.AddSingleton<IErpUsers>(Erp);
                s.AddSingleton<IErpMenu>(Erp);
                s.AddSingleton<IErpScreens>(Erp);
                s.AddSingleton<TimeProvider>(Clock);
            });
        });
        var sp = Factory.Services;
        Writer = new ConfigWriter(sp.GetRequiredService<ConfigDatabase>(), sp.GetRequiredService<SecretProtector>(), Clock, "test");
        Writer.Initialize();
        Writer.AddTenant("MC", "Công ty Medcom", taxCode: "0300000000", databaseLabel: "MedData");
        Writer.SetConnection("MC", "Server=(fake);Database=MedData");
        Writer.AddHost("MC", "localhost");
        Writer.AddTenant("A", "Công ty A");
        Writer.AddTenant("B", "Công ty B");
        Writer.AddHost("A", "group.test");
        Writer.AddHost("B", "group.test");

        Erp.AddUser("MC", "admin", "123", "Quản trị hệ thống");
        Erp.AddUser("MC", "locked", "123", "Bị khóa");
        Erp.AddUser("MC", "space", " a b ", "Mật khẩu có dấu cách");
        Erp.AddUser("B", "kt", "abc", "Kế toán B");
        Erp.Menu.AddRange([
            new MenuEntry("01", null, "Hệ thống", null, null, false, false),
            new MenuEntry("0101", "01", "Người dùng", "NguoiDungFrm", null, false, false),
            new MenuEntry("09", null, "Quỹ", null, null, false, false),
            new MenuEntry("0901", "09", "Phiếu thu ngân hàng", "CS_ReceiptFrm", "Bank", false, false),
            new MenuEntry("0930", "09", "Sổ quỹ", "CS_CashBookReport", null, true, false),
            new MenuEntry("05", null, "Mua hàng", null, null, false, false),
            new MenuEntry("05011", "05", "Đề nghị mua hàng", "AP_PurposeRequestListFrm", null, false, false),
        ]);
    }

    public HttpClient Client(string host = "localhost") =>
        Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri($"http://{host}") });

    /// <summary>Mở trang đăng nhập, lấy token chống giả mạo, gửi form.</summary>
    public static async Task<HttpResponseMessage> LoginAsync(HttpClient c, string user, string password, string? company = null)
    {
        var page = await c.GetStringAsync("/erp/login");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        var form = new Dictionary<string, string> { ["UserName"] = user, ["Password"] = password, ["__RequestVerificationToken"] = token };
        if (company is not null) form["Database"] = company;
        return await c.PostAsync("/erp/login", new FormUrlEncodedContent(form));
    }

    public static HttpRequestMessage FrontendGet(string url)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.Add("X-ERP-Request", "fragment");
        return r;
    }

    public void Dispose()
    {
        Factory.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
        foreach (var v in new[] { "WebErp__Root", "WebErp__FrontendDir", "WebErp__LegacyPassword__Key" }) Environment.SetEnvironmentVariable(v, null);
    }
}

[CollectionDefinition("host")]
public sealed class HostCollection : ICollectionFixture<HostFixture>, ICollectionFixture<WebErp.Tests.Admin.AdminFixture>;

internal static class HttpAssert
{
    public static async Task<string> Status(HttpResponseMessage r, HttpStatusCode expected)
    {
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.StatusCode == expected, $"Mong {(int)expected}, nhận {(int)r.StatusCode}:\n{body}");
        return body;
    }
}
