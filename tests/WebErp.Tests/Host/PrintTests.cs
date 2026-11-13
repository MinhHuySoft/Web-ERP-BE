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
        Assert.Contains($"data-erp=\"print-document\" data-template-src=\"{Base}/print/templates/AP_GoodsDeliveryReceiptReport.rpx\" data-template-format=\"rpx\"", html);
        Assert.DoesNotContain("data-design-href", html); // trình thiết kế: GĐ2
        Assert.Contains("<dl data-erp=\"print-params\">", html);
        Assert.Contains("<dt>Giám đốc</dt><dd data-name=\"UInput1\">Nguyễn Văn A</dd>", html);
        Assert.Contains("<dd data-name=\"Sys.ServerDate\" data-type=\"datetime\" data-value=\"2026-11-13T09:30:00\">", html);
        Assert.Contains("<dd data-name=\"Sys.ReportPrintDate\" data-type=\"bool\" data-value=\"true\">", html);
        Assert.Contains("<table data-erp=\"grid\" data-grid-id=\"R0\"", html);
        Assert.Contains("<table data-erp=\"grid\" data-grid-id=\"R1\"", html);
        Assert.Contains("<th scope=\"col\" data-field=\"Amount\" data-type=\"number\">", html);
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
}
