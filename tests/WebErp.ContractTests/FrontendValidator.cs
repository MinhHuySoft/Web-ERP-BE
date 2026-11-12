using System.Text.Json.Serialization;
using Microsoft.Playwright;

namespace WebErp.ContractTests;

public sealed record Issue(
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("rule")] string Rule,
    [property: JsonPropertyName("msg")] string Msg,
    [property: JsonPropertyName("where")] string Where);

/// <summary>
/// Chạy <c>web/js/tools/validate.js</c> của repo FE (đúng bản ghim trong <c>fe.ref</c>) trong trình duyệt không giao diện. Dùng
/// Edge có sẵn trên Windows (kênh <c>msedge</c>), không phải tải trình duyệt.
/// Tìm thư mục FE: biến môi trường <c>WEBERP_FE_WEB</c> → <c>_fe/web</c> (CI) → <c>../Web-ERP/web</c> (máy dev).
/// </summary>
public sealed class FrontendValidator : IAsyncLifetime
{
    private const string Origin = "http://fe.test";
    private IPlaywright? _playwright;
    private IBrowser? _browser;
    private IPage? _page;

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var web = Locate();
        if (web is null)
        {
            SkipReason = "Không tìm thấy thư mục web/ của repo FE (đặt WEBERP_FE_WEB).";
            return;
        }

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Channel = "msedge", Headless = true });
        _page = await _browser.NewPageAsync();
        await _page.RouteAsync(Origin + "/**", async route =>
        {
            var path = new Uri(route.Request.Url).AbsolutePath;
            if (path == "/blank.html")
            {
                await route.FulfillAsync(new RouteFulfillOptions { ContentType = "text/html", Body = "<!doctype html><title>validator</title>" });
                return;
            }
            var file = Path.GetFullPath(Path.Combine(web, path.TrimStart('/')));
            if (!file.StartsWith(web, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            {
                await route.FulfillAsync(new RouteFulfillOptions { Status = 404 });
                return;
            }
            await route.FulfillAsync(new RouteFulfillOptions { ContentType = "text/javascript", Body = await File.ReadAllTextAsync(file) });
        });
        await _page.GotoAsync(Origin + "/blank.html");
    }

    public async Task<IReadOnlyList<Issue>> ValidateAsync(string html)
    {
        if (SkipReason is not null)
        {
            // Trên CI bắt buộc có FE; trên máy dev thiếu FE thì bỏ qua.
            if (Environment.GetEnvironmentVariable("CI") is not null) Assert.Fail(SkipReason);
            Assert.Skip(SkipReason);
        }
        var json = await _page!.EvaluateAsync<string>(
            "async (html) => { const m = await import('/js/tools/validate.js'); return JSON.stringify(m.validate(new DOMParser().parseFromString(html, 'text/html'))); }",
            html);
        return System.Text.Json.JsonSerializer.Deserialize<Issue[]>(json) ?? [];
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null) await _browser.DisposeAsync();
        _playwright?.Dispose();
    }

    private static string? Locate()
    {
        var env = Environment.GetEnvironmentVariable("WEBERP_FE_WEB");
        if (!string.IsNullOrWhiteSpace(env)) return Valid(env);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "WebErp.slnx"))) continue;
            return Valid(Path.Combine(dir.FullName, "_fe", "web")) ?? Valid(Path.Combine(dir.FullName, "..", "Web-ERP", "web"));
        }
        return null;
    }

    private static string? Valid(string dir)
    {
        var full = Path.GetFullPath(dir);
        return File.Exists(Path.Combine(full, "js", "tools", "validate.js")) ? full + Path.DirectorySeparatorChar : null;
    }
}
