using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebErp.Core.Errors;
using WebErp.Core.Menu;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Host;
using WebErp.Host.Auth;
using WebErp.Tests.Support;

namespace WebErp.Tests.Host;

/// <summary>Cache menu, menu cũ khi DB chậm, gộp request đồng thời, bỏ cache khi đăng xuất; phân loại lỗi SQL.</summary>
public sealed class MenuServiceTests
{
    private static readonly Tenant Tp = new("TP", "Tín Phong", "SY", true, null, null, null, null);
    private static readonly ErpSession User = new(Tp, "Admin", "Quản trị", null);

    private readonly TestClock _time = new(new DateTimeOffset(2026, 11, 17, 3, 0, 0, TimeSpan.Zero));
    private readonly SlowMenu _menu = new();
    private readonly MenuService _service;

    public MenuServiceTests()
    {
        var options = Options.Create(new WebErpOptions { Session = new SessionOptions { MenuCacheMinutes = 5, MenuStaleMinutes = 60 } });
        _service = new MenuService(_menu, new MemoryCache(new MemoryCacheOptions()), options, _time, NullLogger<MenuService>.Instance);
    }

    private sealed class SlowMenu : IErpMenu
    {
        public int Calls;
        public Exception? Fail { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public string Caption { get; set; } = "Người dùng";

        public async Task<IReadOnlyList<MenuEntry>> GetUserMenuAsync(Tenant tenant, string userName, string language, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Gate is { } g) await g.Task;
            if (Fail is { } f) throw f;
            return [new MenuEntry("01", null, "Hệ thống", null, null, false, false), new MenuEntry("0101", "01", Caption, "NguoiDungFrm", null, false, false)];
        }
    }

    private static ErpTimeoutException Timeout() => new("TP", "đọc menu", 60, new InvalidOperationException("timeout"));

    private static string Caption(IReadOnlyList<MenuNode> tree) => tree[0].Children[0].Entry.Caption;

    [Fact]
    public async Task Menu_is_cached_for_menu_cache_minutes()
    {
        await _service.GetTreeAsync(User, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(4));
        await _service.GetTreeAsync(User, CancellationToken.None);
        Assert.Equal(1, _menu.Calls);

        _time.Advance(TimeSpan.FromMinutes(2));
        _menu.Caption = "Người dùng (mới)";
        Assert.Equal("Người dùng (mới)", Caption(await _service.GetTreeAsync(User, CancellationToken.None)));
        Assert.Equal(2, _menu.Calls);
    }

    [Fact]
    public async Task Slow_database_serves_last_menu()
    {
        await _service.GetTreeAsync(User, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(10));
        _menu.Fail = Timeout();
        Assert.Equal("Người dùng", Caption(await _service.GetTreeAsync(User, CancellationToken.None)));

        _menu.Fail = new ErpUnavailableException("TP", new InvalidOperationException("down"));
        Assert.NotNull(await _service.GetTreeAsync(User, CancellationToken.None));
        Assert.Equal(3, _menu.Calls); // vẫn thử đọc lại mỗi lần (menu cũ không được coi là mới)

        _menu.Fail = null;
        _menu.Caption = "Đã đọc lại";
        Assert.Equal("Đã đọc lại", Caption(await _service.GetTreeAsync(User, CancellationToken.None)));
    }

    [Fact]
    public async Task Without_cached_menu_the_timeout_is_reported()
    {
        _menu.Fail = Timeout();
        await Assert.ThrowsAsync<ErpTimeoutException>(() => _service.GetTreeAsync(User, CancellationToken.None));
    }

    [Fact]
    public async Task Menu_older_than_stale_window_is_not_used()
    {
        await _service.GetTreeAsync(User, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(61));
        _menu.Fail = Timeout();
        await Assert.ThrowsAsync<ErpTimeoutException>(() => _service.GetTreeAsync(User, CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_requests_share_one_database_call()
    {
        _menu.Gate = new TaskCompletionSource();
        var calls = Enumerable.Range(0, 5).Select(_ => _service.GetTreeAsync(User, CancellationToken.None)).ToList();
        _menu.Gate.SetResult();
        var trees = await Task.WhenAll(calls);
        Assert.Equal(1, _menu.Calls);
        Assert.All(trees, t => Assert.Equal("Người dùng", Caption(t)));
    }

    [Fact]
    public async Task Cancelled_request_does_not_break_the_shared_load()
    {
        _menu.Gate = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var cancelled = _service.GetTreeAsync(User, cts.Token);
        var other = _service.GetTreeAsync(User, CancellationToken.None);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        _menu.Gate.SetResult();
        Assert.Equal("Người dùng", Caption(await other));
        Assert.Equal(1, _menu.Calls);
    }

    [Fact]
    public async Task Logout_invalidates_cached_menu()
    {
        await _service.GetTreeAsync(User, CancellationToken.None);
        _service.Invalidate("tp", "ADMIN"); // không phân biệt hoa thường
        await _service.GetTreeAsync(User, CancellationToken.None);
        Assert.Equal(2, _menu.Calls);
    }

    [Fact]
    public void Command_timeout_is_slow_not_disconnected()
    {
        var timeout = SqlErrors.Create(-2);
        Assert.True(ErpConnectionFactory.IsConnectivity(timeout));
        var slow = Assert.IsType<ErpTimeoutException>(ErpConnectionFactory.Translate("TP", timeout, "đọc menu", 60));
        Assert.Equal("đọc menu", slow.Operation);
        Assert.Equal(60, slow.TimeoutSeconds);
        Assert.Contains("phản hồi chậm", slow.Message);

        Assert.IsType<ErpUnavailableException>(ErpConnectionFactory.Translate("TP", SqlErrors.Create(10054), "đọc menu", 60));
        Assert.IsType<ErpUnavailableException>(ErpConnectionFactory.Translate("TP", SqlErrors.Create(4060), "đọc menu", 60));
    }
}
