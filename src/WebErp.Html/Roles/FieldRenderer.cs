using System.Globalization;

namespace WebErp.Html.Roles;

/// <summary>Một ô nhập (chuẩn 9.2).</summary>
/// <param name="Control"><c>text</c>, <c>password</c>, <c>textarea</c>, <c>number</c> / <c>money</c> / <c>quantity</c> / <c>percent</c>,
/// <c>date</c>, <c>datetime</c>, <c>search</c>, <c>checkbox</c>, <c>select</c>, <c>lookup</c>, <c>label</c>, <c>button</c> (nút trên form,
/// <see cref="Label"/> là chữ nút; hiện khóa, bấm không gửi gì).</param>
/// <param name="Decimals">Số chữ số thập phân hiển thị của ô số (<c>data-decimals</c>).</param>
/// <param name="Display">Chữ hiển thị kèm mã: tên của ô <c>lookup</c> (<c>output[data-erp=lookup-text]</c>), hoặc nội dung ô <c>label</c>.</param>
/// <param name="LookupSrc">URL tra cứu F4 (có <c>{q}</c>), bắt buộc với ô <c>lookup</c>.</param>
/// <param name="Value">Giá trị bất biến. Với <c>password</c> không bao giờ được ghi ra.</param>
/// <param name="Span">Độ rộng trên lưới 40 cột.</param>
/// <param name="Title">Chú thích khi rê chuột (nút).</param>
public sealed record FieldModel(
    string Name,
    string Label,
    string Control = "text",
    string? Value = null,
    int Span = 20,
    bool Required = false,
    bool ReadOnly = false,
    int? MaxLength = null,
    string? Placeholder = null,
    string? Hint = null,
    int? Rows = null,
    bool RowStart = false,
    IReadOnlyList<(string Value, string Text)>? Options = null,
    string? Autocomplete = null,
    string? Pattern = null,
    string? InputMode = null,
    string? Error = null,
    int? Decimals = null,
    string? Display = null,
    string? LookupSrc = null,
    string? Title = null);

/// <summary><c>div[data-erp=field]</c>: một <c>label[for]</c> + đúng một control chính.</summary>
public static class FieldRenderer
{
    /// <param name="external">Ô nằm ngoài <c>form</c> (ô của chứng từ trên trang chi tiết, chuẩn 12): control mang thuộc tính
    /// <c>form</c> = <paramref name="formId"/> để vẫn thuộc form đó.</param>
    public static void Write(HtmlWriter w, string formId, FieldModel f, bool external = false)
    {
        var id = $"{formId}-{f.Name}";
        Attr owner = ("form", external ? formId : null);
        w.Open("div", ("data-erp", "field"), ("data-control", f.Control), ("data-span", f.Span.ToString(CultureInfo.InvariantCulture)),
            ("data-rows", f.Rows?.ToString(CultureInfo.InvariantCulture)), Attr.Flag("data-row-start", f.RowStart), Attr.Flag("data-invalid", f.Error is not null));
        if (f.Control == "button")
        {
            // Nút trên form (chuẩn 9.2): ô trống thay nhãn để nút thẳng hàng với ô nhập; type=button nên không gửi form.
            w.Element("span", "");
            w.Element("button", f.Label, ("type", "button"), ("id", id), Attr.Flag("disabled", true), ("title", f.Title));
            w.Close("div").Line();
            return;
        }
        w.Element("label", f.Label, ("for", id));
        var common = new Attr[]
        {
            ("id", id), ("name", f.Name), owner, Attr.Flag("required", f.Required), Attr.Flag("readonly", f.ReadOnly),
            ("maxlength", f.MaxLength?.ToString(CultureInfo.InvariantCulture)), ("placeholder", f.Placeholder),
            ("autocomplete", f.Autocomplete), ("pattern", f.Pattern), ("inputmode", f.InputMode),
        };
        switch (f.Control)
        {
            case "textarea":
                w.Element("textarea", f.Value, [.. common, ("rows", (f.Rows ?? 3).ToString(CultureInfo.InvariantCulture))]);
                break;
            case "password":
                w.Void("input", [.. common, ("type", "password")]);
                break;
            case "checkbox":
                // Input ẩn "false" đặt trước, để bỏ tích vẫn gửi giá trị (chuẩn 9.2).
                w.Void("input", ("type", "hidden"), ("name", f.Name), owner, ("value", "false"));
                w.Void("input", ("id", id), ("name", f.Name), owner, ("type", "checkbox"), ("value", "true"),
                    Attr.Flag("checked", f.Value == "true"), Attr.Flag("disabled", f.ReadOnly));
                break;
            case "select":
                w.Open("select", ("id", id), ("name", f.Name), owner, Attr.Flag("required", f.Required), Attr.Flag("disabled", f.ReadOnly));
                foreach (var (value, text) in f.Options ?? [])
                    w.Element("option", text, ("value", value), Attr.Flag("selected", value == f.Value));
                w.Close("select");
                break;
            case "label":
                w.Element("output", f.Display ?? f.Value, ("id", id));
                break;
            case "number" or "money" or "quantity" or "percent":
                w.Void("input", [.. common, ("inputmode", f.InputMode ?? "decimal"), ("value", f.Value),
                    ("data-decimals", f.Decimals?.ToString(CultureInfo.InvariantCulture))]);
                break;
            case "date":
                w.Void("input", [.. common, ("type", "date"), ("value", f.Value is { Length: >= 10 } d ? d[..10] : f.Value)]);
                break;
            case "datetime":
                w.Void("input", [.. common, ("type", "datetime-local"), ("value", f.Value is { Length: >= 16 } t ? t[..16] : f.Value)]);
                break;
            case "search":
                w.Void("input", [.. common, ("type", "search"), ("value", f.Value)]);
                break;
            case "lookup":
                w.Void("input", [.. common, ("value", f.Value), ("data-lookup-src", f.LookupSrc ?? throw new InvalidOperationException($"Ô lookup '{f.Name}' thiếu LookupSrc."))]);
                w.Element("output", f.Display, ("data-erp", "lookup-text"), ("for", id));
                break;
            default:
                w.Void("input", [.. common, ("value", f.Value)]);
                break;
        }
        if (f.Hint is not null) w.Element("small", f.Hint, ("data-erp", "hint"));
        if (f.Error is not null) w.Element("small", f.Error, ("data-erp", "error"));
        w.Close("div").Line();
    }
}
