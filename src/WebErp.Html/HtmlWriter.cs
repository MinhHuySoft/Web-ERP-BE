using System.Text;
using System.Text.RegularExpressions;

namespace WebErp.Html;

/// <summary>Thuộc tính HTML. <c>Value = null</c>: không ghi. <c>Value = ""</c>: ghi dạng cờ (<c>required</c>, <c>data-begin-group</c>).</summary>
public readonly record struct Attr(string Name, string? Value)
{
    public static Attr Flag(string name, bool on) => new(name, on ? "" : null);

    public static implicit operator Attr((string Name, string? Value) t) => new(t.Name, t.Value);
}

/// <summary>
/// Ghi HTML theo chuẩn ERP-HTML (mục 2.2, 18): mọi chữ và giá trị thuộc tính tự encode. Không ghi được <c>class</c>,
/// <c>style</c>, <c>on*</c>, <c>srcdoc</c>, thẻ <c>script</c>/<c>style</c>, hay URL <c>javascript:</c>, nên renderer không thể lỡ vi
/// phạm chuẩn.
/// </summary>
public sealed partial class HtmlWriter
{
    private static readonly HashSet<string> ForbiddenTags = new(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "link", "iframe", "object", "embed", "base", "frame", "frameset", "applet" };

    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
        { "href", "action", "formaction", "src", "data-src", "data-lookup-src", "data-query-src", "data-refresh-src", "data-options-src" };

    private readonly StringBuilder _sb = new();

    [GeneratedRegex("^[a-z][a-z0-9]*$")]
    private static partial Regex TagPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex AttrPattern();

    public HtmlWriter Open(string tag, params ReadOnlySpan<Attr> attrs)
    {
        StartTag(tag, attrs);
        return this;
    }

    public HtmlWriter Close(string tag)
    {
        CheckTag(tag);
        _sb.Append("</").Append(tag).Append('>');
        return this;
    }

    /// <summary>Thẻ rỗng (<c>input</c>, <c>meta</c>, <c>br</c>).</summary>
    public HtmlWriter Void(string tag, params ReadOnlySpan<Attr> attrs) => Open(tag, attrs);

    /// <summary>Thẻ có chữ bên trong: <c>&lt;tag …&gt;text&lt;/tag&gt;</c>.</summary>
    public HtmlWriter Element(string tag, string? text, params ReadOnlySpan<Attr> attrs)
    {
        StartTag(tag, attrs);
        Text(text);
        return Close(tag);
    }

    public HtmlWriter Text(string? text)
    {
        if (!string.IsNullOrEmpty(text)) _sb.Append(Encode(text));
        return this;
    }

    public HtmlWriter Line()
    {
        _sb.Append('\n');
        return this;
    }

    public override string ToString() => _sb.ToString();

    public static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length + 16);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '"': sb.Append("&quot;"); break;
                case '\'': sb.Append("&#39;"); break;
                default:
                    // Bỏ ký tự điều khiển (trừ tab/xuống dòng): HTML5 không cho phép và có thể làm lệch trình phân tích.
                    if (ch < ' ' && ch is not ('\t' or '\n' or '\r')) continue;
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private void StartTag(string tag, ReadOnlySpan<Attr> attrs)
    {
        CheckTag(tag);
        _sb.Append('<').Append(tag);
        foreach (var a in attrs)
        {
            if (a.Value is null) continue;
            CheckAttribute(a.Name, a.Value);
            _sb.Append(' ').Append(a.Name);
            if (a.Value.Length > 0) _sb.Append("=\"").Append(Encode(a.Value)).Append('"');
        }
        _sb.Append('>');
    }

    private static void CheckTag(string tag)
    {
        if (!TagPattern().IsMatch(tag) || ForbiddenTags.Contains(tag))
            throw new InvalidOperationException($"Thẻ <{tag}> không được phép trong ERP-HTML.");
    }

    private static void CheckAttribute(string name, string value)
    {
        if (!AttrPattern().IsMatch(name) || name is "class" or "style" or "srcdoc" || name.StartsWith("on", StringComparison.Ordinal))
            throw new InvalidOperationException($"Thuộc tính '{name}' không được phép trong ERP-HTML (mục 2.2).");
        if (UrlAttributes.Contains(name) && value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"URL javascript: không được phép trong '{name}'.");
    }
}
