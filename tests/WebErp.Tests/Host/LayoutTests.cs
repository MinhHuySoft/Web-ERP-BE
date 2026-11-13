using System.Net;
using System.Text.RegularExpressions;

namespace WebErp.Tests.Host;

/// <summary>Vẽ layout form (chuẩn 15.3): nút trên chứng từ cho Admin, màn vẽ, JSON bố cục, lưu / khôi phục có token, chỉ xem khi không có quyền.</summary>
[Collection("host")]
public sealed class LayoutTests(HostFixture f)
{
    private const string Base = "/erp/screens/AP_PurposeRequestListFrm";

    private static async Task<string> Get(HttpClient c, string url, HttpStatusCode status = HttpStatusCode.OK) =>
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet(url)), status);

    private static HttpRequestMessage Post(string url, HttpContent body)
    {
        var r = HostFixture.FrontendGet(url);
        r.Method = HttpMethod.Post;
        r.Content = body;
        return r;
    }

    [Fact]
    public async Task Admin_opens_the_designer_from_the_record_and_saves_with_the_token()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);

        var rec = await Get(c, $"{Base}/PO%2F001");
        Assert.Contains($"data-action=\"layout\" href=\"{Base}/layout\" data-target=\"tab\"", rec);
        Assert.Contains("data-shortcut=\"Ctrl+F12\"", rec);

        var html = await Get(c, $"{Base}/layout");
        Assert.Contains("data-screen-key=\"layout:AP_PurposeRequestListFrm\"", html);
        Assert.Contains("data-mode=\"edit\"", html);
        Assert.Contains($"<section data-erp=\"layout-designer\" data-form=\"AP_PurposeRequestListFrm\" data-layout-src=\"{Base}/layout/data\" "
            + $"data-source-src=\"{Base}/layout/source?fid={{fid}}\">", html);
        Assert.Contains("\"fid\":\"CF_ItemListFrm\"", await Get(c, $"{Base}/layout/source?fid="));
        Assert.Contains("\"name\":\"ItemID\"", await Get(c, $"{Base}/layout/source?fid=CF_ItemListFrm"));
        await Get(c, $"{Base}/layout/source?fid=x%27%3Bdrop", HttpStatusCode.NotFound);
        Assert.Contains($"<form method=\"post\" action=\"{Base}/layout/save\">", html);
        Assert.Contains("<input type=\"hidden\" name=\"Form\" value=\"AP_PurposeRequestListFrm\">", html);
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        var res = await c.SendAsync(HostFixture.FrontendGet($"{Base}/layout/data"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", res.Headers.CacheControl?.ToString());
        Assert.Contains("\"$schema\":\"erp-form-layout/1\"", await res.Content.ReadAsStringAsync());

        const string json = """{"$schema":"erp-form-layout/1","form":"AP_PurposeRequestListFrm","revision":"r1","items":[{"tab":"1","group":"","name":"PurchaseRequestID","cfg":"1;6;1;1"}]}""";
        MultipartFormDataContent Form(string j, string? restore = null)
        {
            var m = new MultipartFormDataContent
            {
                { new StringContent(token), "__RequestVerificationToken" }, { new StringContent("AP_PurposeRequestListFrm"), "Form" },
                { new StringContent(j), "Json" }, { new StringContent("Rộng ô số"), "Comment" },
            };
            if (restore is not null) m.Add(new StringContent(restore), "Restore");
            return m;
        }
        using (var ok = Form(json))
        {
            var saved = await HttpAssert.Status(await c.SendAsync(Post($"{Base}/layout/save", ok)), HttpStatusCode.OK);
            Assert.Contains("data-msg-type=\"success\"", saved);
            Assert.Contains("phiên bản 2, 3 dòng cấu hình", saved);
            Assert.Equal(("Rộng ô số", (int?)null), (f.Erp.LastLayout!.Value.Comment, f.Erp.LastLayout.Value.Restore));
        }
        using (var stale = Form(json.Replace("\"r1\"", "\"cu\"", StringComparison.Ordinal)))
            Assert.Contains("đã bị đổi", await HttpAssert.Status(await c.SendAsync(Post($"{Base}/layout/save", stale)), HttpStatusCode.Conflict));
        using (var bad = Form("{\"$schema\":\"khac\"}"))
            Assert.Contains("erp-form-layout/1", await HttpAssert.Status(await c.SendAsync(Post($"{Base}/layout/save", bad)), HttpStatusCode.UnprocessableEntity));
        using (var restore = Form("", "1"))
        {
            Assert.Contains("Đã khôi phục bố cục", await HttpAssert.Status(await c.SendAsync(Post($"{Base}/layout/save", restore)), HttpStatusCode.OK));
            Assert.Equal(1, f.Erp.LastLayout!.Value.Restore);
        }
        using var noToken = new MultipartFormDataContent { { new StringContent("AP_PurposeRequestListFrm"), "Form" }, { new StringContent(json), "Json" } };
        await HttpAssert.Status(await c.SendAsync(Post($"{Base}/layout/save", noToken)), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Non_admin_sees_no_button_and_a_read_only_designer()
    {
        f.Erp.CannotDesign.Add("admin");
        try
        {
            using var c = f.Client();
            Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);
            Assert.DoesNotContain("data-action=\"layout\"", await Get(c, $"{Base}/PO%2F001"));
            var html = await Get(c, $"{Base}/layout");
            Assert.Contains("data-mode=\"view\"", html);
            Assert.DoesNotContain("<form method=\"post\"", html);
            Assert.DoesNotContain("data-source-src", html);
            Assert.Contains("quyền Admin trên form", html);
            await Get(c, $"{Base}/layout/source?fid=", HttpStatusCode.Forbidden);
        }
        finally { f.Erp.CannotDesign.Clear(); }
    }
}
