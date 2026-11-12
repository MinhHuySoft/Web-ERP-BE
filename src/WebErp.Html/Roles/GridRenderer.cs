using System.Globalization;

namespace WebErp.Html.Roles;

/// <param name="Type"><c>text</c>, <c>number</c>, <c>money</c>, <c>date</c>, <c>datetime</c>, <c>bool</c>, <c>link</c>, <c>status</c>.</param>
public sealed record GridColumn(string Field, string Title, string Type = "text", int? Width = null, bool Frozen = false, bool Wrap = false);

/// <summary>Một ô. <see cref="Value"/>: giá trị máy bất biến (chuẩn 2.5). <see cref="Href"/>: ô là link mở chi tiết.</summary>
/// <param name="StatusTone">Có giá trị thì ô là <c>data[data-erp=status]</c>: success / warning / danger / info / muted.</param>
public sealed record GridCell(string? Text, string? Value = null, string? Href = null, string? LinkTarget = null, string? StatusTone = null)
{
    public static GridCell Bool(bool value) => new(value ? "Có" : "", value ? "true" : "false");

    public static GridCell DateTime(DateTimeOffset? t, TimeZoneInfo zone) => t is null ? new GridCell(Text: null)
        : new GridCell(TimeZoneInfo.ConvertTime(t.Value, zone).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            TimeZoneInfo.ConvertTime(t.Value, zone).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
}

public sealed record GridRow(string? Key, IReadOnlyList<GridCell> Cells, string? Tone = null);

/// <param name="Selectable"><c>none</c> / <c>single</c> / <c>multi</c>.</param>
public sealed record GridModel(
    string GridId,
    IReadOnlyList<GridColumn> Columns,
    IReadOnlyList<GridRow> Rows,
    string? KeyField = null,
    string? Caption = null,
    string Selectable = "none",
    string? Sort = null);

/// <summary><c>table[data-erp=grid]</c> (chuẩn 11). Mỗi dòng đúng số ô bằng số cột.</summary>
public static class GridRenderer
{
    public static void Write(HtmlWriter w, GridModel g)
    {
        w.Open("table", ("data-erp", "grid"), ("data-grid-id", g.GridId), ("data-key-field", g.KeyField),
            ("data-selectable", g.Selectable), ("data-total-rows", g.Rows.Count.ToString(CultureInfo.InvariantCulture)), ("data-sort", g.Sort)).Line();
        if (!string.IsNullOrEmpty(g.Caption)) w.Element("caption", g.Caption).Line();
        w.Open("thead").Open("tr");
        foreach (var c in g.Columns)
        {
            w.Element("th", c.Title, ("scope", "col"), ("data-field", c.Field), ("data-type", c.Type),
                ("data-width", c.Width?.ToString(CultureInfo.InvariantCulture)), Attr.Flag("data-frozen", c.Frozen), Attr.Flag("data-wrap", c.Wrap));
        }
        w.Close("tr").Close("thead").Line().Open("tbody").Line();
        foreach (var r in g.Rows)
        {
            if (r.Cells.Count != g.Columns.Count)
                throw new InvalidOperationException($"Dòng '{r.Key}' có {r.Cells.Count} ô, lưới có {g.Columns.Count} cột.");
            w.Open("tr", ("data-key", g.KeyField is null ? null : r.Key ?? ""), ("data-tone", r.Tone));
            foreach (var cell in r.Cells)
            {
                w.Open("td", ("data-value", cell.Value));
                if (cell.StatusTone is not null) w.Element("data", cell.Text, ("data-erp", "status"), ("value", cell.Value ?? cell.Text ?? ""), ("data-tone", cell.StatusTone));
                else if (cell.Href is not null) w.Element("a", cell.Text, ("href", cell.Href), ("data-target", cell.LinkTarget));
                else w.Text(cell.Text);
                w.Close("td");
            }
            w.Close("tr").Line();
        }
        w.Close("tbody").Close("table").Line();
    }
}
