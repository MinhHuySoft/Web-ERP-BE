using System.Net;

namespace WebErp.Tests.Host;

[Collection("host")]
public sealed class HostTests(HostFixture f)
{
    [Fact]
    public async Task Health_and_frontend_config_need_no_login()
    {
        using var c = f.Client();
        var health = await HttpAssert.Status(await c.GetAsync("/health"), HttpStatusCode.OK);
        Assert.Contains("\"status\":\"ok\"", health);
        var cfg = await HttpAssert.Status(await c.GetAsync("/config.js"), HttpStatusCode.OK);
        Assert.Contains("\"loginUrl\":\"/erp/login\"", cfg);
        Assert.Contains("\"homeUrl\":\"/erp/home\"", cfg);
    }

    [Fact]
    public async Task Unknown_host_is_404_and_never_falls_back_to_another_company()
    {
        using var c = f.Client("unknown.test");
        var body = await HttpAssert.Status(await c.GetAsync("/erp/login"), HttpStatusCode.NotFound);
        Assert.Contains("chưa được cấu hình", body);
        Assert.DoesNotContain("Medcom", body);
    }

    [Fact]
    public async Task Anonymous_frontend_call_gets_401_and_browser_gets_redirected()
    {
        using var c = f.Client();
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/session")), HttpStatusCode.Unauthorized);
        var r = await c.GetAsync("/erp/session");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.StartsWith("http://localhost/erp/login", r.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Login_page_hides_company_choice_for_one_company_and_lists_many()
    {
        using var single = f.Client();
        var one = await single.GetStringAsync("/erp/login");
        Assert.Contains("name=\"Database\" value=\"MC\"", one);
        Assert.DoesNotContain("<select", one);

        using var multi = f.Client("group.test");
        var many = await multi.GetStringAsync("/erp/login");
        Assert.Contains("<option value=\"A\">Công ty A</option>", many);
        Assert.Contains("<option value=\"B\">Công ty B</option>", many);
    }

    [Fact]
    public async Task Wrong_password_returns_form_with_legacy_message()
    {
        using var c = f.Client();
        var body = await HttpAssert.Status(await HostFixture.LoginAsync(c, "admin", "sai"), HttpStatusCode.OK);
        Assert.Contains("data-erp=\"login\"", body);
        Assert.Contains("Người dùng không tồn tại hoặc mật mã không đúng.", body);
        Assert.Contains("value=\"admin\"", body);
    }

    [Fact]
    public async Task Login_then_session_menu_home_and_screens()
    {
        using var c = f.Client();
        var r = await HostFixture.LoginAsync(c, " admin ", " 123 ");
        Assert.Equal(HttpStatusCode.SeeOther, r.StatusCode);
        Assert.Equal("/erp/session", r.Headers.Location!.ToString());

        var session = await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/session")), HttpStatusCode.OK);
        Assert.Contains("<dd data-value=\"admin\">Quản trị hệ thống</dd>", session);
        Assert.Contains("<dd>Công ty Medcom</dd>", session);
        Assert.Contains("<dd>0300000000</dd>", session);
        Assert.Contains("data-action=\"logout\" href=\"/erp/logout\"", session);

        var menu = await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/menu")), HttpStatusCode.OK);
        Assert.Contains("href=\"/erp/screens/CS_ReceiptFrm?para=Bank\"", menu);
        Assert.Contains("href=\"/erp/reports/CS_CashBookReport\"", menu);

        Assert.Contains("data-form-type=\"DASHBOARD\"", await c.GetStringAsync("/erp/home"));

        var screen = await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/screens/CS_ReceiptFrm?para=Bank")), HttpStatusCode.OK);
        Assert.Contains("data-screen-key=\"CS_ReceiptFrm_Bank\"", screen);
        Assert.Contains("<h1>Phiếu thu ngân hàng</h1>", screen);
        var report = await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/reports/CS_CashBookReport")), HttpStatusCode.OK);
        Assert.Contains("data-form-type=\"REPORT\"", report);

        // Không có trong menu của người dùng → không mở thẳng bằng URL được.
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/screens/AR_InvoiceFrm")), HttpStatusCode.Forbidden);
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/screens/CS_ReceiptFrm")), HttpStatusCode.Forbidden);
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/screens/bad%20name")), HttpStatusCode.NotFound);

        var logout = await c.GetAsync("/erp/logout");
        Assert.Equal(HttpStatusCode.SeeOther, logout.StatusCode);
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/session")), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Company_must_be_chosen_on_shared_domain_and_cookie_is_bound_to_that_company()
    {
        using var c = f.Client("group.test");
        Assert.Contains("Vui lòng chọn công ty.", await (await HostFixture.LoginAsync(c, "kt", "abc")).Content.ReadAsStringAsync());
        Assert.Contains("Người dùng không tồn tại", await (await HostFixture.LoginAsync(c, "kt", "abc", "A")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "kt", "abc", "B")).StatusCode);
        Assert.Contains("Công ty B", await c.GetStringAsync("/erp/session"));
    }

    [Fact]
    public async Task Missing_antiforgery_token_is_rejected()
    {
        using var c = f.Client();
        var r = await c.PostAsync("/erp/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["UserName"] = "admin", ["Password"] = "123" }));
        var body = await HttpAssert.Status(r, HttpStatusCode.BadRequest);
        Assert.Contains("hết hạn", body);
    }

    [Fact]
    public async Task Logout_all_mark_rejects_existing_cookies()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/session")), HttpStatusCode.OK);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        f.Writer.LogoutAll("MC");
        f.Clock.Advance(TimeSpan.FromSeconds(10)); // qua chu kỳ nạp lại cấu hình
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/session")), HttpStatusCode.Unauthorized);

        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet("/erp/session")), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_temporarily()
    {
        using var c = f.Client();
        for (var i = 0; i < 5; i++) await HostFixture.LoginAsync(c, "locked", "sai");
        var body = await HttpAssert.Status(await HostFixture.LoginAsync(c, "locked", "123"), (HttpStatusCode)429);
        Assert.Contains("quá nhiều lần", body);
    }

    [Fact]
    public async Task Database_outage_returns_503_with_message()
    {
        using var c = f.Client();
        f.Erp.Down = true;
        try
        {
            var r = await HostFixture.LoginAsync(c, "admin", "123");
            var login = await HttpAssert.Status(r, HttpStatusCode.ServiceUnavailable);
            Assert.Contains("Không kết nối được máy chủ dữ liệu", login);
            Assert.NotNull(r.Headers.RetryAfter);
        }
        finally
        {
            f.Erp.Down = false;
        }
    }
}
