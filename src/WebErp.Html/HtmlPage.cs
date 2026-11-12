namespace WebErp.Html;

/// <summary>
/// Trang HTML đầy đủ. FE chỉ lấy các vai trò <c>data-erp</c> trong <c>body</c>. Trả trang đầy đủ để mở thẳng bằng trình
/// duyệt (không JS) vẫn đọc được (chuẩn mục 2.3).
/// </summary>
public static class HtmlPage
{
    public const string ContractVersion = "1.0";

    public static string Render(string title, Action<HtmlWriter> body)
    {
        var w = new HtmlWriter();
        w.Open("html", ("lang", "vi")).Open("head")
            .Void("meta", ("charset", "utf-8"))
            .Void("meta", ("name", "erp-contract"), ("content", ContractVersion))
            .Element("title", title)
            .Close("head").Line()
            .Open("body").Line();
        body(w);
        w.Line().Close("body").Close("html");
        return "<!doctype html>\n" + w;
    }
}
