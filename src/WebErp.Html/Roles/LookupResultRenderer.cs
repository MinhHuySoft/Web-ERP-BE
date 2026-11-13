using System.Globalization;

namespace WebErp.Html.Roles;

/// <summary>Một dòng kết quả tra cứu: giá trị gán vào ô, chữ hiển thị kèm, các ô theo cột.</summary>
public sealed record LookupResultRow(string Value, string Text, IReadOnlyList<GridCell> Cells);

/// <param name="Columns">Cột <c>Hidden</c>: dùng để điền trường nhưng không hiển thị (chuẩn 10).</param>
public sealed record LookupResultModel(IReadOnlyList<GridColumn> Columns, IReadOnlyList<LookupResultRow> Rows, int Total);

/// <summary><c>table[data-erp=lookup-result]</c> (chuẩn 10, response của <c>data-lookup-src</c>).</summary>
public static class LookupResultRenderer
{
    public static void Write(HtmlWriter w, LookupResultModel m)
    {
        w.Open("table", ("data-erp", "lookup-result"), ("data-total", m.Total.ToString(CultureInfo.InvariantCulture))).Line();
        w.Open("thead").Open("tr");
        foreach (var c in m.Columns) w.Element("th", c.Title, ("scope", "col"), ("data-field", c.Field), Attr.Flag("data-hidden", c.Hidden));
        w.Close("tr").Close("thead").Line().Open("tbody").Line();
        foreach (var r in m.Rows)
        {
            if (r.Cells.Count != m.Columns.Count) throw new InvalidOperationException($"Dòng tra cứu '{r.Value}' có {r.Cells.Count} ô, có {m.Columns.Count} cột.");
            w.Open("tr", ("data-value", r.Value), ("data-text", r.Text));
            foreach (var cell in r.Cells) w.Element("td", cell.Text, ("data-value", cell.Value));
            w.Close("tr").Line();
        }
        w.Close("tbody").Line().Close("table").Line();
    }
}
