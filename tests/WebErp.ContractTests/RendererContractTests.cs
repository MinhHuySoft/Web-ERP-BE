using WebErp.Core.Menu;
using WebErp.Core.Messages;
using WebErp.Html.Roles;
using WebErp.Html.Screens;
using WebErp.Tests.Host;

namespace WebErp.ContractTests;

/// <summary>Mọi kiểu trang BE sinh ra ở M1 phải qua validator ERP-HTML của FE: 0 lỗi (AGENTS.md của repo FE).</summary>
public sealed class RendererContractTests(FrontendValidator validator) : IClassFixture<FrontendValidator>
{
    private static readonly ScreenUrls MetaUrls = new("/erp/screens/AP_PurposeRequestListFrm", null,
        k => "/erp/screens/AP_PurposeRequestListFrm/" + Uri.EscapeDataString(k),
        (k, s) => $"/erp/screens/AP_PurposeRequestListFrm/{Uri.EscapeDataString(k)}/tabs/{s}", "/erp/screens/AP_PurposeRequestListFrm?FromDate=2026-10-13",
        Page: n => $"/erp/screens/AP_PurposeRequestListFrm?FromDate=2026-10-13&page={n}",
        Lookup: (f, p) => $"/erp/screens/AP_PurposeRequestListFrm/lookups/{f}?q={{q}}" + string.Concat(p.Select((x, i) => $"&p{i}={Uri.EscapeDataString(x)}")));

    public static TheoryData<string> Pages()
    {
        var menu = MenuTree.Build([
            new MenuEntry("01", null, "Hệ thống", null, null, false, false),
            new MenuEntry("0101", "01", "Quản trị", null, null, false, true),
            new MenuEntry("010101", "0101", "Người dùng", "NguoiDungFrm", null, false, false),
            new MenuEntry("0199", "01", "Đăng xuất", "LogOff", null, true, false),
            new MenuEntry("09", null, "Quỹ", null, null, false, false),
            new MenuEntry("0901", "09", "Phiếu thu ngân hàng", "CS_ReceiptFrm", "Bank", false, false),
            new MenuEntry("0930", "09", "Sổ quỹ <tiền mặt>", "CS_CashBookReport", null, true, false),
        ]);
        return new TheoryData<string>
        {
            LoginRenderer.Render(new LoginModel("/erp/login", [new CompanyOption("MC", "Medcom")], null, null, "__RequestVerificationToken", "t")),
            LoginRenderer.Render(new LoginModel("/erp/login", [new CompanyOption("A", "Công ty A"), new CompanyOption("B", "Công ty B")], "B", "admin",
                "__RequestVerificationToken", "t", [ErpMessage.Error("Người dùng không tồn tại hoặc mật mã không đúng.")])),
            SessionRenderer.Render(new SessionModel(
                [new SessionItem("company", "Công ty", "Medcom"), new SessionItem("period", "Kỳ", "11/2026", "202611"), new SessionItem("user", "Người dùng", "Quản trị", "admin")],
                [new SessionLink("logout", "Đăng xuất", "/erp/logout")])),
            MenuRenderer.Render(menu, new Urls()),
            BasicScreens.Home("Quản trị hệ thống", "Medcom"),
            BasicScreens.NotAvailable("CS_ReceiptFrm", "Bank", false, "Phiếu thu ngân hàng"),
            BasicScreens.NotAvailable("CS_CashBookReport", null, true),
            BasicScreens.Error("Lỗi", ErpMessage.Error("Có lỗi khi xử lý yêu cầu. Mã lỗi: 00-abc")),
            MetaScreens.List(SampleScreens.List(), "Đề nghị mua hàng", "05011", MetaUrls),
            MetaScreens.Record(SampleScreens.Record("PO/001"), "Đề nghị mua hàng", "05011", MetaUrls, "__RequestVerificationToken", "t"),
            MetaScreens.Detail(SampleScreens.Detail(), "Hàng hóa đề nghị"),
            MetaScreens.Lookup(SampleScreens.Lookup(), "Nhà cung cấp"),
            MetaScreens.List(SampleScreens.Dialog(), "Sao lưu dữ liệu", "010118", MetaUrls),
            MetaScreens.List(SampleScreens.ListWithTabs(), "Khai báo lịch tài chính", "010107", MetaUrls),
            MetaScreens.List(SampleScreens.List() with { Page = 2 }, "Đề nghị mua hàng", "05011", MetaUrls),
            MetaScreens.ConfigError("BAD_ConfigFrm", null, "Form cấu hình xấu", "Đoạn SQL 'T0.FTX' trong cấu hình form không chạy được."),
        };
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task Page_passes_frontend_validator_with_no_errors(string html)
    {
        var issues = await validator.ValidateAsync(html);
        var errors = issues.Where(i => i.Level == "error").ToList();
        Assert.True(errors.Count == 0, "Vi phạm ERP-HTML:\n" + string.Join("\n", errors.Select(e => $"[{e.Rule}] {e.Msg} — {e.Where}")));
        Assert.DoesNotContain(issues, i => i.Level == "warning");
    }

    [Fact]
    public async Task Validator_really_runs_and_catches_violations()
    {
        var issues = await validator.ValidateAsync("<section data-erp=\"screen\" style=\"x\"><script></script></section>");
        Assert.Contains(issues, i => i.Level == "error" && i.Rule == "2.2");
        Assert.Contains(issues, i => i.Level == "error" && i.Rule == "6");
    }

    private sealed class Urls : IMenuUrls
    {
        public string For(MenuNode node) => node.Kind == MenuKind.Command ? "/erp/logout" : "/erp/screens/" + node.Entry.FormName;
    }
}
