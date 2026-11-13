using System.Net;

namespace WebErp.Tests.Host;

/// <summary>In theo mẫu (M6, chuẩn 15.1): nút In trên chứng từ, màn in print-document, tệp mẫu, chặn chứng từ không xem được.</summary>
[Collection("host")]
public sealed class PrintTests(HostFixture f)
{
    private const string Base = "/erp/screens/AP_PurposeRequestListFrm";

    private static async Task<string> Get(HttpClient c, string url, HttpStatusCode status = HttpStatusCode.OK) =>
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet(url)), status);

    [Fact]
    public async Task Record_has_a_print_button_and_the_print_screen_follows_the_contract()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);

        var rec = await Get(c, $"{Base}/PO%2F001");
        Assert.Contains($"data-action=\"print\" href=\"{Base}/print?DocumentID=PO%2F001\" data-target=\"tab\"", rec);

        var html = await Get(c, $"{Base}/print?DocumentID=PO%2F001&Template=AP_GoodsDeliveryReceiptReport&UInput1=Tr%E1%BA%A7n%20B");
        Assert.Contains("data-form-type=\"REPORT\"", html);
        Assert.Contains("data-screen-key=\"print:AP_PurposeRequestListFrm:PO/001\"", html);
        Assert.Contains($"<form data-erp=\"filter\" method=\"get\" action=\"{Base}/print\">", html);
        Assert.Contains("<input type=\"hidden\" name=\"DocumentID\" value=\"PO/001\">", html);
        Assert.Contains($"data-erp=\"print-document\" data-template-src=\"{Base}/print/templates/AP_GoodsDeliveryReceiptReport.rpx?v=r20261113000000\" data-template-format=\"rpx\"", html);
        Assert.Contains($"data-design-href=\"{Base}/print/designer?Template=AP_GoodsDeliveryReceiptReport&amp;DocumentID=PO%2F001\"", html);
        Assert.Contains("<dl data-erp=\"print-params\">", html);
        Assert.Contains("<dt>Giám đốc</dt><dd data-name=\"UInput1\">Nguyễn Văn A</dd>", html);
        Assert.Contains("<dd data-name=\"Sys.ServerDate\" data-type=\"datetime\" data-value=\"2026-11-13T09:30:00\">", html);
        Assert.Contains("<dd data-name=\"Sys.ReportPrintDate\" data-type=\"bool\" data-value=\"true\">", html);
        Assert.Contains("<table data-erp=\"grid\" data-grid-id=\"R0\"", html);
        Assert.Contains("<table data-erp=\"grid\" data-grid-id=\"R1\"", html);
        Assert.Contains("<th scope=\"col\" data-field=\"Amount\" data-type=\"number\" data-format=\"#,##0\">", html);
        Assert.Contains("<th scope=\"col\" data-field=\"DocumentID\" data-type=\"text\">", html);
        Assert.Contains("<td data-value=\"1234567.5\">1.234.567,5</td>", html);
        Assert.Contains("<td data-value=\"2026-11-10\">10/11/2026</td>", html);
        Assert.Contains("<td data-null></td>", html);
        Assert.Contains("Ghi chú &lt;b&gt;", html);
        var p = f.Erp.LastPrint!.Value;
        Assert.Equal(["PO/001"], p.Ids);
        Assert.Equal("AP_GoodsDeliveryReceiptReport", p.Template);
        Assert.Equal("Trần B", p.Inputs["UInput1"]);
    }

    [Fact]
    public async Task Template_file_is_served_as_xml_and_unknown_templates_or_documents_are_refused()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);

        var res = await c.SendAsync(HostFixture.FrontendGet($"{Base}/print/templates/AP_PurposeRequestListReport.rpx"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/xml", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("ActiveReportsLayout", await res.Content.ReadAsStringAsync());
        Assert.True(res.Headers.CacheControl is { NoCache: true, Private: true });

        await Get(c, $"{Base}/print/templates/Khac.rpx", HttpStatusCode.NotFound);
        await Get(c, $"{Base}/print/templates/..%2F..%2Fweb.config", HttpStatusCode.NotFound);
        // chứng từ không xem được (hoặc không có): không in
        await Get(c, $"{Base}/print?DocumentID=PO%2F999", HttpStatusCode.NotFound);
        await Get(c, $"{Base}/print?DocumentID=PO%2F001%3BPO%2F999", HttpStatusCode.NotFound);
        // màn không có trong menu: không in
        await Get(c, "/erp/screens/NguoiDungKhacFrm/print?DocumentID=PO%2F001", HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Designer_screen_follows_the_contract_and_saves_a_version_with_the_antiforgery_token()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);

        var html = await Get(c, $"{Base}/print/designer?Template=AP_PurposeRequestListReport&DocumentID=PO%2F001");
        Assert.Contains("data-screen-key=\"design:AP_PurposeRequestListFrm:AP_PurposeRequestListReport\"", html);
        Assert.Contains("data-mode=\"edit\"", html);
        Assert.Contains($"data-erp=\"print-designer\" data-template-name=\"AP_PurposeRequestListReport\" data-template-src=\"{Base}/print/templates/AP_PurposeRequestListReport.json?v=w2\" data-template-format=\"json\"", html);
        Assert.Contains($"data-preview-src=\"{Base}/print?DocumentID=PO%2F001&amp;Template=AP_PurposeRequestListReport\"", html);
        Assert.Contains($"<form method=\"post\" action=\"{Base}/print/designer/save\">", html);
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);

        using var ok = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }, { new StringContent("AP_PurposeRequestListReport"), "Template" },
            { new StringContent(SampleScreens.TemplateJson), "Json" }, { new StringContent("Dời ô số chứng từ"), "Comment" },
        };
        var saved = await HttpAssert.Status(await c.SendAsync(Post($"{Base}/print/designer/save", ok)), HttpStatusCode.OK);
        Assert.Contains("data-msg-type=\"success\"", saved);
        Assert.Contains("phiên bản 3", saved);
        Assert.Equal(("AP_PurposeRequestListReport", "Dời ô số chứng từ"), (f.Erp.LastSave!.Value.Template, f.Erp.LastSave.Value.Comment));

        using var bad = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }, { new StringContent("AP_PurposeRequestListReport"), "Template" },
            { new StringContent("{\"$schema\":\"khac\"}"), "Json" },
        };
        var rejected = await HttpAssert.Status(await c.SendAsync(Post($"{Base}/print/designer/save", bad)), HttpStatusCode.UnprocessableEntity);
        Assert.Contains("erp-print-template/1", rejected);

        using var noToken = new MultipartFormDataContent { { new StringContent("AP_PurposeRequestListReport"), "Template" }, { new StringContent(SampleScreens.TemplateJson), "Json" } };
        await HttpAssert.Status(await c.SendAsync(Post($"{Base}/print/designer/save", noToken)), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Users_without_EPER_rights_get_a_read_only_designer_and_cannot_save()
    {
        f.Erp.CannotDesign.Add("admin");
        try
        {
            using var c = f.Client();
            Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);
            var html = await Get(c, $"{Base}/print/designer?Template=AP_PurposeRequestListReport");
            Assert.Contains("data-mode=\"view\"", html);
            Assert.DoesNotContain("<form method=\"post\"", html);
            Assert.DoesNotContain("data-preview-src", html);
            Assert.Contains("chỉ được xem mẫu", html);
        }
        finally { f.Erp.CannotDesign.Clear(); }
    }

    private static HttpRequestMessage Post(string url, HttpContent body)
    {
        var r = HostFixture.FrontendGet(url);
        r.Method = HttpMethod.Post;
        r.Content = body;
        return r;
    }
}
