using WebErp.Core.Menu;
using WebErp.Core.Messages;
using WebErp.Html;
using WebErp.Html.Roles;

namespace WebErp.Tests.Html;

public sealed class HtmlWriterTests
{
    [Fact]
    public void Encodes_text_and_attributes()
    {
        var html = new HtmlWriter().Element("p", "<b>\"x\" & 'y'</b>", ("data-value", "a\"b<c")).ToString();
        Assert.Equal("<p data-value=\"a&quot;b&lt;c\">&lt;b&gt;&quot;x&quot; &amp; &#39;y&#39;&lt;/b&gt;</p>", html);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("style")]
    [InlineData("onclick")]
    [InlineData("srcdoc")]
    public void Refuses_presentation_and_event_attributes(string name) =>
        Assert.Throws<InvalidOperationException>(() => new HtmlWriter().Open("div", (name, "x")));

    [Theory]
    [InlineData("script")]
    [InlineData("style")]
    [InlineData("iframe")]
    public void Refuses_forbidden_tags(string tag) =>
        Assert.Throws<InvalidOperationException>(() => new HtmlWriter().Open(tag));

    [Fact]
    public void Refuses_javascript_urls() =>
        Assert.Throws<InvalidOperationException>(() => new HtmlWriter().Open("a", ("href", " JavaScript:alert(1)")));

    [Fact]
    public void Null_attributes_are_skipped_and_empty_ones_are_flags()
    {
        var html = new HtmlWriter().Void("input", ("name", "x"), ("value", null), Attr.Flag("required", true), Attr.Flag("disabled", false)).ToString();
        Assert.Equal("<input name=\"x\" required>", html);
    }

    [Fact]
    public void Login_with_one_company_sends_hidden_input_and_no_select()
    {
        var html = LoginRenderer.Render(new LoginModel("/erp/login", [new CompanyOption("MC", "Medcom")], null, "admin", "__RequestVerificationToken", "tok"));
        Assert.Contains("<input type=\"hidden\" name=\"Database\" value=\"MC\">", html);
        Assert.DoesNotContain("<select", html);
        Assert.Contains("value=\"admin\"", html);
    }

    [Fact]
    public void Login_with_many_companies_has_select_with_selection_and_messages()
    {
        var html = LoginRenderer.Render(new LoginModel("/erp/login",
            [new CompanyOption("A", "Công ty A"), new CompanyOption("B", "Công ty B")], "b", null, "__RequestVerificationToken", "tok",
            [ErpMessage.Error("Sai mật khẩu")]));
        Assert.Contains("<option value=\"B\" selected>Công ty B</option>", html);
        Assert.Contains("data-msg-type=\"error\"", html);
        Assert.DoesNotContain("type=\"password\" value", html);
    }

    [Fact]
    public void Menu_renders_groups_items_and_logout_command()
    {
        var tree = MenuTree.Build([
            new MenuEntry("01", null, "Hệ thống", null, null, false, false),
            new MenuEntry("0101", "01", "Người dùng", "NguoiDungFrm", null, true, false),
            new MenuEntry("0199", "01", "Đăng xuất", "LogOff", null, false, false),
        ]);
        var html = MenuRenderer.Render(tree, new Urls());
        Assert.Contains("data-erp=\"menu-group\" data-menu-id=\"01\" data-icon=\"system\"", html);
        Assert.Contains("data-kind=\"form\" data-screen-id=\"NguoiDungFrm\" data-begin-group", html);
        Assert.Contains("<a href=\"/x/LogOff\" data-action=\"logout\">", html);
    }

    private sealed class Urls : IMenuUrls
    {
        public string For(MenuNode node) => "/x/" + node.Entry.FormName;
    }
}
