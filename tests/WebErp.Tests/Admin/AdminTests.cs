using System.Net;
using WebErp.Tests.Host;

namespace WebErp.Tests.Admin;

/// <summary>Toàn bộ luồng trang quản trị chạy tuần tự trong một test, vì mỗi bước dựa trên bước trước (tài khoản, công ty…).</summary>
[Collection("host")]
public sealed class AdminTests(AdminFixture f)
{
    [Fact]
    public async Task Full_admin_flow()
    {
        using var c = f.Client();
        Assert.Contains("\"admins\":0", await c.GetStringAsync("/health"));

        // Chưa có quản trị viên: từ xa không thiết lập được, chỉ báo cách làm.
        var remote = await c.SendAsync(AdminFixture.Fe(HttpMethod.Get, $"https://{AdminFixture.AdminHost}/admin/login", "203.0.113.7"));
        var remoteBody = await HttpAssert.Status(remote, HttpStatusCode.OK);
        Assert.Contains("127.0.0.1:8082", remoteBody);
        Assert.DoesNotContain("__setup", remoteBody);

        // Thiết lập trên chính máy chủ: có mã QR.
        var setupPage = await c.GetStringAsync("/admin/login");
        Assert.Contains("Tạo tài khoản quản trị đầu tiên", setupPage);
        Assert.Contains("src=\"data:image/png;base64,", setupPage);
        var setup = await f.SetupAsync(c);
        Assert.Equal(HttpStatusCode.SeeOther, setup.StatusCode);
        Assert.Contains("data-form-type=\"DASHBOARD\"", await HttpAssert.Status(await c.SendAsync(AdminFixture.Fe(HttpMethod.Get, "/admin/home")), HttpStatusCode.OK));
        Assert.Contains("Chưa đặt hằng mật khẩu ERP", await c.GetStringAsync("/admin/home"));

        // Thêm công ty: chuỗi kết nối kiểu SSMS được chuẩn hóa; cần mã xác thực.
        var newPage = await c.GetStringAsync("/admin/tenants/new");
        var form = new Dictionary<string, string>
        {
            ["Key"] = "MINHHUY", ["Name"] = "Công ty thử", ["Module"] = "SY", ["Hosts"] = "minhhuy.bms7.net\nMINHHUY.bms7.net", ["Enabled"] = "true",
            ["Connection"] = "Data Source=sql.example.test,14333;Initial Catalog=ErpDemo;User ID=u;Password=p;Pooling=False;Encrypt=False;TrustServerCertificate=True",
            ["Code"] = "000000", ["__RequestVerificationToken"] = AdminFixture.Token(newPage),
        };
        var badCode = await c.PostAsync("/admin/tenants/new", new FormUrlEncodedContent(form));
        Assert.Contains("Mã xác thực không đúng", await HttpAssert.Status(badCode, HttpStatusCode.UnprocessableEntity));
        form["Code"] = f.NextCode();
        var created = await HttpAssert.Status(await c.PostAsync("/admin/tenants/new", new FormUrlEncodedContent(form)), HttpStatusCode.OK);
        Assert.Contains("Đã tạo công ty MINHHUY", created);
        Assert.Contains("bật mã hóa", created);
        Assert.DoesNotContain("Password=p", created);

        var list = await c.GetStringAsync("/admin/tenants");
        Assert.Contains("minhhuy.bms7.net", list);
        Assert.DoesNotContain("MINHHUY.bms7.net, ", list); // domain trùng (khác hoa thường) đã gộp

        // Sửa với __version cũ → 409.
        var edit = await c.GetStringAsync("/admin/tenants/MINHHUY");
        var version = AdminFixture.Hidden(edit, "__version");
        var save = new Dictionary<string, string>
        {
            ["Name"] = "Công ty thử (đổi tên)", ["Module"] = "SY", ["Hosts"] = "minhhuy.bms7.net", ["Enabled"] = "true",
            ["__version"] = version, ["__RequestVerificationToken"] = AdminFixture.Token(edit),
        };
        Assert.Contains("Đã lưu", await HttpAssert.Status(await c.PostAsync("/admin/tenants/MINHHUY/save", new FormUrlEncodedContent(save)), HttpStatusCode.OK));
        await HttpAssert.Status(await c.PostAsync("/admin/tenants/MINHHUY/save", new FormUrlEncodedContent(save)), HttpStatusCode.Conflict);

        // Đăng xuất mọi người: hộp thoại, sai mã thì hộp thoại hiện lại.
        var dialog = await c.GetStringAsync("/admin/tenants/MINHHUY/logout-all");
        Assert.Contains("data-erp=\"dialog\"", dialog);
        var wrong = await c.PostAsync("/admin/tenants/MINHHUY/logout-all", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Code"] = "123456", ["__RequestVerificationToken"] = AdminFixture.Token(dialog) }));
        Assert.Contains("data-erp=\"dialog\"", await HttpAssert.Status(wrong, HttpStatusCode.UnprocessableEntity));
        var ok = await c.PostAsync("/admin/tenants/MINHHUY/logout-all", new FormUrlEncodedContent(new Dictionary<string, string>
            { ["Code"] = f.NextCode(), ["__RequestVerificationToken"] = AdminFixture.Token(dialog) }));
        Assert.Contains("bị từ chối trong vài giây", await HttpAssert.Status(ok, HttpStatusCode.OK));

        // Cài đặt chung: hằng mật khẩu phải đúng 6 ký tự, và chỉ lưu khi thử khớp với một tài khoản ERP thật của công ty.
        f.Erp.AddUser("MINHHUY", "Admin", " mk 1 ", "Quản trị ERP");
        var settings = await c.GetStringAsync("/admin/settings");
        Assert.Contains("name=\"TestTenant\"", settings);
        Task<HttpResponseMessage> SaveKey(string key, string user, string password) =>
            c.PostAsync("/admin/settings/save", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["LegacyKey"] = key, ["TestTenant"] = "MINHHUY", ["TestUser"] = user, ["TestPassword"] = password, ["Code"] = f.NextCode(),
                ["__version"] = AdminFixture.Hidden(settings, "__version"), ["__RequestVerificationToken"] = AdminFixture.Token(settings),
            }));
        Assert.Contains("CHƯA lưu", await HttpAssert.Status(await SaveKey("ABC123", "admin", " mk 1 "), HttpStatusCode.UnprocessableEntity));
        Assert.Contains("CHƯA lưu", await HttpAssert.Status(await SaveKey(FakeErp.Key, "admin", "mk 1"), HttpStatusCode.UnprocessableEntity));
        Assert.Contains("Không có tài khoản", await HttpAssert.Status(await SaveKey(FakeErp.Key, "khong-co", "x"), HttpStatusCode.UnprocessableEntity));
        f.Erp.Down = true;
        Assert.Contains("Không kết nối được DB", await HttpAssert.Status(await SaveKey(FakeErp.Key, "admin", " mk 1 "), HttpStatusCode.UnprocessableEntity));
        f.Erp.Down = false;
        Assert.Contains("Chưa đặt hằng mật khẩu ERP", await c.GetStringAsync("/admin/home")); // các lần trên không lưu gì
        var setKey = await HttpAssert.Status(await SaveKey(FakeErp.Key, "admin", " mk 1 "), HttpStatusCode.OK);
        Assert.Contains("Đã lưu hằng mật khẩu (đã thử với MINHHUY/Admin)", setKey);
        Assert.DoesNotContain(FakeErp.Key, await c.GetStringAsync("/admin/settings"));
        var audit = await c.GetStringAsync("/admin/audit");
        Assert.Contains("Thêm công ty", audit);
        Assert.Contains("Đặt cài đặt", audit);

        // Đăng xuất rồi đăng nhập lại: cần mật khẩu + mã TOTP mới.
        Assert.Equal(HttpStatusCode.SeeOther, (await c.GetAsync("/admin/logout")).StatusCode);
        await HttpAssert.Status(await c.SendAsync(AdminFixture.Fe(HttpMethod.Get, "/admin/home")), HttpStatusCode.Unauthorized);
        Assert.Contains("không đúng", await (await f.LoginAsync(c, "000000")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.SeeOther, (await f.LoginAsync(c)).StatusCode);
        await HttpAssert.Status(await c.SendAsync(AdminFixture.Fe(HttpMethod.Get, "/admin/tenants")), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Remote_requests_need_a_declared_host_and_https()
    {
        using var c = f.Client();
        var unknown = await c.SendAsync(AdminFixture.Fe(HttpMethod.Get, "https://customer.bms7.net/admin/login", "203.0.113.7"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var plain = await c.SendAsync(AdminFixture.Fe(HttpMethod.Get, $"http://{AdminFixture.AdminHost}/admin/login", "203.0.113.7"));
        Assert.Contains("HTTPS", await HttpAssert.Status(plain, HttpStatusCode.Forbidden));
        var https = await c.SendAsync(AdminFixture.Fe(HttpMethod.Get, $"https://{AdminFixture.AdminHost}/config.js", "203.0.113.7"));
        Assert.Contains("\"loginUrl\":\"/admin/login\"", await HttpAssert.Status(https, HttpStatusCode.OK));
    }
}
