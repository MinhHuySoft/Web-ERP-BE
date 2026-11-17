using System.Net;
using WebErp.Core.Metadata;

namespace WebErp.Tests.Host;

/// <summary>Route màn metadata: danh sách, chứng từ, tab nạp lười; công ty và người dùng luôn lấy từ phiên.</summary>
[Collection("host")]
public sealed class ScreenTests(HostFixture f)
{
    private const string Base = "/erp/screens/AP_PurposeRequestListFrm";

    private static async Task<string> Get(HttpClient c, string url, HttpStatusCode status = HttpStatusCode.OK) =>
        await HttpAssert.Status(await c.SendAsync(HostFixture.FrontendGet(url)), status);

    [Fact]
    public async Task List_record_and_lazy_tab_are_served_for_a_menu_screen()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);

        var list = await Get(c, $"{Base}?FromDate=2026-10-01&BranchID=HN");
        Assert.Contains("data-erp=\"filter\"", list);
        Assert.Contains("data-form-type=\"EDIT\" data-mode=\"find\"", list);
        Assert.Contains($"href=\"{Base}/PO%2F001\" data-target=\"tab\"", list);
        Assert.Contains("data-total-rows=\"1500\"", list);
        // Phân trang: trang sau giữ bộ lọc đang áp; tên thay mã giữ mã làm giá trị máy.
        Assert.Contains("data-erp=\"pager\" data-page=\"1\" data-page-size=\"1000\" data-total=\"1500\"", list);
        Assert.Contains($"rel=\"next\" href=\"{Base}?FromDate=2026-10-01&amp;BranchID=HN&amp;page=2\" data-target=\"self\"", list);
        Assert.DoesNotContain("Đang hiện 2 trên", list);
        Assert.Contains("<td data-value=\"1\">Đã duyệt</td>", list);
        Assert.Contains("<td>9</td>", list);
        Assert.Contains("<tfoot>", list);
        var req = f.Erp.LastScreen!;
        Assert.Equal(("MC", "admin", "2026-10-01", "HN"), (req.Tenant.Key, req.UserName, req.Query["FromDate"], req.Query["BranchID"]));

        var rec = await Get(c, $"{Base}/PO%2F001");
        Assert.Contains("data-mode=\"view\"", rec);
        Assert.Contains("data-screen-key=\"AP_PurposeRequestListFrm:PO/001\"", rec);
        Assert.Contains($"data-src=\"{Base}/PO%2F001/tabs/T1\"", rec);
        Assert.Contains($"data-lookup-src=\"{Base}/lookups/ObjectID?q={{q}}&amp;p0=HN\"", rec);
        Assert.Contains("<output data-erp=\"lookup-text\" for=", rec);
        // Nút trên form: hiện như ERP nhưng khóa, không gửi form.
        Assert.Contains("<div data-erp=\"field\" data-control=\"button\" data-span=\"3\"><span></span><button type=\"button\" "
            + "id=\"rec-AP-PurposeRequestListFrm-StatusButtonCtl_1\" disabled title=\"Chưa dùng được trên bản web (chờ chức năng ghi dữ liệu)\">Chuyển trạng thái</button></div>", rec);
        // Trang chi tiết: ô của chứng từ đứng trước lưới nạp lười, gắn form chứng từ; trang chỉ có ô thì không có data-src.
        Assert.Matches($"data-tab-id=\"T1\" data-title=\"Hàng hóa đề nghị\" data-src=\"{Base}/PO%2F001/tabs/T1\">\\s*<fieldset>\\s*<div data-erp=\"field\"", rec);
        Assert.Contains("name=\"PaymentTermID\" form=\"rec-AP-PurposeRequestListFrm\" readonly value=\"30N\"", rec);
        Assert.Contains("data-tab-id=\"L6\" data-title=\"Công nợ &amp; Giảm giá / CK\">", rec);
        Assert.DoesNotContain("/tabs/L6", rec);
        Assert.Contains("name=\"DueDate\" form=\"rec-AP-PurposeRequestListFrm\" readonly type=\"date\" value=\"2026-12-09\"", rec);

        var lk = await Get(c, $"{Base}/lookups/ObjectID?q=c%C3%B4ng&p0=HN");
        Assert.Contains("data-erp=\"lookup-result\" data-total=\"7\"", lk);
        Assert.Contains("<tr data-value=\"NCC02\" data-text=\"Công ty &lt;B&gt;\">", lk);
        Assert.Contains("data-field=\"TaxCode\" data-hidden", lk);
        Assert.Equal(("ObjectID", "công", "HN"), (f.Erp.LastLookup!.Value.Field, f.Erp.LastLookup.Value.Text, string.Join(",", f.Erp.LastLookup.Value.Paras)));
        await Get(c, $"{Base}/lookups/Khac?q=", HttpStatusCode.NotFound);

        Assert.Contains("data-grid-id=\"T1\"", await Get(c, $"{Base}/PO%2F001/tabs/T1"));
        await Get(c, $"{Base}/PO%2F999", HttpStatusCode.NotFound);
        await Get(c, $"{Base}/PO%2F001/tabs/X1", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Config_error_page_is_logged_without_data_values()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);
        var page = await Get(c, "/erp/screens/NguoiDungFrm");
        Assert.Contains("chưa mở được trên bản web", page);
        Assert.Contains("Mã lỗi:", page);
        Assert.DoesNotContain("SQL", page); // trình duyệt không bao giờ thấy chi tiết lỗi SQL
        var log = Assert.Single(f.Logs.Entries, e => e.Category == "WebErp.Screens" && e.Message.Contains("NguoiDungFrm", StringComparison.Ordinal)
                                                     && e.Message.Contains("SQL 245", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, log.Level);
        Assert.Contains("MC", log.Message);
        Assert.Contains("[T0, bảng SY_User]", log.Message);
        Assert.DoesNotContain("GIA-TRI-RIENG", log.Message);
    }

    [Fact]
    public async Task Schema_error_logs_column_name_but_page_does_not()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);
        f.Erp.NextList = new ListView(ScreenStatus.ConfigError,
            Note: "Câu truy vấn dựng từ cấu hình form bị lỗi (SQL 207: Invalid column name 'TenNgan'.) [T0, bảng SY_User]");
        var page = await Get(c, "/erp/screens/NguoiDungFrm");
        Assert.DoesNotContain("TenNgan", page);
        Assert.Contains(f.Logs.Entries, e => e.Category == "WebErp.Screens" && e.Message.Contains("Invalid column name 'TenNgan'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dropped_display_columns_are_logged_and_screen_opens()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);
        f.Erp.NextList = SampleScreens.List() with { Note = "bỏ cột DB không có: T0: SY_User.TenNgan" };
        var page = await Get(c, "/erp/screens/NguoiDungFrm");
        Assert.Contains("data-erp=\"grid\"", page);
        Assert.DoesNotContain("TenNgan", page);
        var log = Assert.Single(f.Logs.Entries, e => e.Category == "WebErp.Screens" && e.Message.Contains("bỏ cột DB không có: T0: SY_User.TenNgan", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Information, log.Level);
    }

    [Fact]
    public async Task Web_version_not_matching_company_database_is_not_available_for_this_company()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);
        f.Erp.NextList = new ListView(ScreenStatus.SchemaMismatch, Note: "T0: thiếu cột SY_User.CeoID, SY_User.TenNgan");
        var page = await Get(c, "/erp/screens/NguoiDungFrm");
        Assert.Contains("chưa có trên bản web cho dữ liệu của công ty này", page);
        Assert.DoesNotContain("CeoID", page);
        var log = Assert.Single(f.Logs.Entries, e => e.Category == "WebErp.Screens" && e.Message.Contains("SY_User.CeoID", StringComparison.Ordinal));
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, log.Level);
        Assert.Contains("MC", log.Message);
    }

    [Fact]
    public async Task Form_without_web_support_shows_not_available_and_unknown_screen_is_forbidden()
    {
        using var c = f.Client();
        Assert.Equal(HttpStatusCode.SeeOther, (await HostFixture.LoginAsync(c, "admin", "123")).StatusCode);
        Assert.Contains("chưa có trên bản web", await Get(c, "/erp/screens/CS_ReceiptFrm?para=Bank"));
        // Mỗi lần mở màn chưa có trên web được ghi lại (form, menu, công ty) để xếp ưu tiên.
        Assert.Contains(f.Logs.Entries, e => e.Category == "WebErp.Screens" && e.Message.Contains("Mở màn chưa có trên web: CS_ReceiptFrm_Bank", StringComparison.Ordinal)
                                             && e.Message.Contains("0901", StringComparison.Ordinal));
        await Get(c, "/erp/screens/AP_KhongCoTrongMenuFrm", HttpStatusCode.Forbidden);
        await Get(c, "/erp/screens/AP_KhongCoTrongMenuFrm/1", HttpStatusCode.Forbidden);
    }
}
