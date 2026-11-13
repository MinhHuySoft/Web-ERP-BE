using System.Globalization;
using WebErp.Core.Print;
using WebErp.Html.Roles;

namespace WebErp.Html.Screens;

/// <summary>Đường dẫn của màn in (Host dựng).</summary>
/// <param name="Self">URL màn in (action của form chọn mẫu), không query string.</param>
/// <param name="Template">URL tệp mẫu theo tên mẫu (<c>data-template-src</c>).</param>
/// <param name="Design">URL trình thiết kế theo tên mẫu; null = người dùng không được sửa mẫu.</param>
public sealed record PrintUrls(string Self, string? Para, Func<string, string> Template, Func<string, string>? Design = null);

/// <summary>Màn in theo mẫu (chuẩn ERP-HTML 15.1): screen REPORT + <c>print-document</c> (tham số in, lưới R0, R1…).</summary>
public static class PrintScreens
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static string Print(PrintView v, string formName, string title, string? menuId, PrintUrls urls)
    {
        var docs = v.DocumentIds ?? [];
        var docText = string.Join(", ", docs.Take(3)) + (docs.Count > 3 ? $" (+{docs.Count - 3})" : "");
        var heading = $"In {title.Trim()} — {docText}";
        var template = (v.Templates ?? []).FirstOrDefault(t => t.File == v.Template);
        return ScreenRenderer.Render(new ScreenModel(v.Report, FormTypes.Report, $"print:{formName}:{string.Join(";", docs)}", heading,
            Para: urls.Para, MenuId: menuId, Messages: v.Messages), w =>
        {
            var fields = new List<FieldModel>
            {
                new("Template", "Mẫu in", "select", v.Template, Span: 14, Options: (v.Templates ?? []).Select(t => (t.File, t.Title)).ToList()),
            };
            foreach (var u in v.UserInputs ?? []) fields.Add(new FieldModel(u.Name, u.Caption, "text", u.Value, Span: 8, MaxLength: 200));
            var hidden = new List<(string, string)> { ("DocumentID", string.Join(";", docs)) };
            if (urls.Para is not null) hidden.Add(("para", urls.Para));
            FilterRenderer.Write(w, new FilterModel($"prn-{formName}", urls.Self, fields, hidden, "Xem"));

            w.Open("section", ("data-erp", "print-document"), ("data-template-src", urls.Template(v.Template)), ("data-template-format", v.TemplateFormat),
                ("data-design-href", urls.Design?.Invoke(v.Template)), ("data-number-group", "."), ("data-number-decimal", ","),
                ("data-title", $"{template?.Title ?? v.Template} {docText}")).Line();
            w.Open("dl", ("data-erp", "print-params")).Line();
            foreach (var p in v.Params ?? [])
            {
                if (p.Caption is not null) w.Element("dt", p.Caption);
                var (text, value) = Format(p.Value, p.Type);
                w.Element("dd", text, ("data-name", p.Name), ("data-type", p.Type == "text" ? null : p.Type), ("data-value", value)).Line();
            }
            w.Close("dl").Line();
            var sets = v.Sets ?? [];
            for (var i = 0; i < sets.Count; i++) WriteSet(w, $"R{i}", sets[i]);
            w.Close("section").Line();
        });
    }

    /// <summary>Lưới dữ liệu in: đúng cột SP trả, giá trị máy bất biến, NULL là <c>td[data-null]</c>.</summary>
    private static void WriteSet(HtmlWriter w, string id, PrintDataSet s)
    {
        w.Open("table", ("data-erp", "grid"), ("data-grid-id", id), ("data-selectable", "none")).Line();
        w.Open("thead").Open("tr");
        foreach (var c in s.Columns) w.Element("th", c.Field, ("scope", "col"), ("data-field", c.Field), ("data-type", c.Type));
        w.Close("tr").Close("thead").Line();
        w.Open("tbody").Line();
        foreach (var r in s.Rows)
        {
            w.Open("tr");
            for (var i = 0; i < s.Columns.Count; i++)
            {
                if (r[i] is null) { w.Element("td", null, Attr.Flag("data-null", true)); continue; }
                var (text, value) = Format(r[i], s.Columns[i].Type);
                w.Element("td", text, ("data-value", value));
            }
            w.Close("tr").Line();
        }
        w.Close("tbody").Close("table").Line();
    }

    /// <summary>(chữ hiển thị, giá trị máy) theo chuẩn 2.5; chữ thường không cần <c>data-value</c>.</summary>
    private static (string? Text, string? Value) Format(object? v, string type) => v switch
    {
        null => (null, null),
        bool b => (b ? "Có" : "Không", b ? "true" : "false"),
        DateTime d when d.TimeOfDay == TimeSpan.Zero => (d.ToString("dd/MM/yyyy", Vi), d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        DateTime d => (d.ToString("dd/MM/yyyy HH:mm", Vi), d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
        DateTimeOffset d => (d.ToString("dd/MM/yyyy HH:mm", Vi), d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
        double d when !double.IsFinite(d) || Math.Abs(d) > 7.9e27 => (d.ToString(CultureInfo.InvariantCulture), null),
        float f when !float.IsFinite(f) => (f.ToString(CultureInfo.InvariantCulture), null),
        decimal or double or float or int or long or short or byte when type == "number" => Number(Convert.ToDecimal(v, CultureInfo.InvariantCulture)),
        IFormattable f => (f.ToString(null, CultureInfo.InvariantCulture), null),
        _ => (v.ToString(), null),
    };

    private static (string, string) Number(decimal d) =>
        (d.ToString("#,##0.############", Vi), d.ToString("0.############", CultureInfo.InvariantCulture));
}
