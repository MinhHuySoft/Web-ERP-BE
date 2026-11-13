using System.Globalization;
using WebErp.Core.Messages;
using WebErp.Core.Metadata;
using WebErp.Html.Roles;

namespace WebErp.Html.Screens;

/// <summary>Đường dẫn của một màn (Host dựng; Html không biết route).</summary>
/// <param name="Self">URL màn danh sách, không query string (action của form lọc).</param>
/// <param name="Record">URL xem một chứng từ theo khóa.</param>
/// <param name="Detail">URL tab chi tiết theo (khóa, slot).</param>
/// <param name="Page">URL trang n của danh sách, giữ bộ lọc đang áp (chuẩn 11.5).</param>
/// <param name="Lookup">URL tra cứu F4 của một ô (có chỗ <c>{q}</c>) theo (tên ô, giá trị tham số); null = không có tra cứu.</param>
/// <param name="Print">URL màn in theo mẫu của một chứng từ (M6); null = form không có báo cáo in.</param>
public sealed record ScreenUrls(string Self, string? Para, Func<string, string> Record, Func<string, string, string> Detail, string Refresh,
    Func<int, string>? Page = null, Func<string, IReadOnlyList<string>, string>? Lookup = null, Func<string, string>? Print = null);

/// <summary>Màn dựng từ metadata ERP: danh sách (M2), xem chứng từ và tab chi tiết (M3). Chỉ đọc.</summary>
public static class MetaScreens
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    public static string List(ListView v, string title, string? menuId, ScreenUrls urls)
    {
        var form = v.Form!;
        var isEdit = form.IsEdit;
        var actions = Actions(form);
        var messages = new List<ErpMessage>();
        if (actions.Count > 0) messages.Add(ErpMessage.Info(PendingNote));
        var type = isEdit ? FormTypes.Edit : form.IsDialog ? FormTypes.Custom : FormType(form.FormType);
        return ScreenRenderer.Render(new ScreenModel(form.FormName, type, form.RuntimeName, title,
            Mode: isEdit ? "find" : "view", Para: urls.Para, MenuId: menuId, RefreshSrc: urls.Refresh, Messages: messages), w =>
        {
            ToolbarRenderer.Write(w, [new ToolbarItem("reload", "Nạp lại", Href: urls.Refresh, Target: "self", Shortcut: "F5"), .. actions]);
            // Mô tả chức năng của hộp thoại (key web WNOTE, nhiều đoạn cách nhau "|").
            foreach (var p in (form.Setting("WNOTE") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                w.Element("p", p).Line();
            if (form.IsDialog && v.Columns is not { Count: > 0 }) return;
            FilterRenderer.Write(w, new FilterModel($"flt-{form.RuntimeName}", urls.Self, (v.Filters ?? []).Select(FilterField).ToList(),
                urls.Para is null ? null : [("para", urls.Para)]));
            var rows = v.Rows ?? [];
            var paged = v.PageSize > 0 && urls.Page is not null && v.Total > v.PageSize;
            if (!paged && v.Total > rows.Count)
                w.Element("p", $"Đang hiện {rows.Count.ToString("N0", Vi)} trên {v.Total.ToString("N0", Vi)} dòng. Thu hẹp bộ lọc để xem phần còn lại.").Line();
            GridRenderer.Write(w, Grid("T0", v.Columns ?? [], rows, v.Keys ?? [], v.KeyField, v.Total, v.Sums,
                v.CanOpenRecord ? urls.Record : null, v.GroupBy));
            if (paged) PagerRenderer.Write(w, new PagerModel(v.Page, v.PageSize, v.Total, urls.Page!));
            // Tab độc lập (IND): lưới không nối dòng nào của danh sách, nạp lười như tab chi tiết (khóa "-").
            if (v.Tabs is { Count: > 0 })
                TabsRenderer.Write(w, v.Tabs.Select(d => new TabPanelModel(d.Id, d.Title, urls.Detail("-", d.Id))).ToList());
        });
    }

    public static string Record(RecordView v, string title, string? menuId, ScreenUrls urls, string antiforgeryField, string antiforgeryToken)
    {
        var form = v.Form!;
        var key = v.Key!;
        var recordId = "rec-" + Slug(form.RuntimeName);
        return ScreenRenderer.Render(new ScreenModel(form.FormName, FormTypes.Edit, $"{form.RuntimeName}:{key}", $"{title} — {key}",
            Mode: "view", Para: urls.Para, MenuId: menuId, RefreshSrc: urls.Record(key)), w =>
        {
            var tools = new List<ToolbarItem> { new("reload", "Nạp lại", Href: urls.Record(key), Target: "self", Shortcut: "F5") };
            if (urls.Print is not null) tools.Add(new ToolbarItem("print", "In", Href: urls.Print(key), Target: "tab", Shortcut: "Ctrl+P"));
            ToolbarRenderer.Write(w, tools);
            // Chế độ xem: mọi ô readonly / disabled (chuẩn 9). Web chưa ghi (M4) nên action là chính URL chứng từ.
            RecordRenderer.Write(w, new RecordModel(recordId, urls.Record(key), antiforgeryField, antiforgeryToken,
                (v.Tabs ?? []).Select(t => new FieldsetModel(t.Title, t.Fields.Select(f => ViewField(f, urls)).ToList(), t.Id)).ToList(), v.KeyField, key));
            TabsRenderer.Write(w, (v.Details ?? []).Select(d => new TabPanelModel(d.Id, d.Title, urls.Detail(key, d.Id))).ToList());
        });
    }

    /// <summary>Nội dung một tab chi tiết nạp lười: chỉ có lưới.</summary>
    public static string Detail(DetailView v, string title) =>
        HtmlPage.Render(title, w => GridRenderer.Write(w, Grid(v.SlotId ?? "T1", v.Columns ?? [], v.Rows ?? [], v.Keys ?? [], v.KeyField, v.Total, v.Sums, null, v.GroupBy)));

    private const string PendingNote =
        "Bản web hiện chỉ xem dữ liệu. Các nút thực hiện của màn này (giống ERP) sẽ dùng được khi web có chức năng ghi dữ liệu.";

    /// <summary>
    /// Nút thực hiện của form viết tay (key web <c>WACT</c> trong tệp mô tả, cách nhau <c>;</c>): hiện đúng như ERP nhưng khóa, vì web chưa
    /// ghi (M4). Không gắn form, không URL: bấm không gửi gì.
    /// </summary>
    private static List<ToolbarItem> Actions(FormMeta form) =>
        (form.Setting("WACT") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => new ToolbarItem("custom", a, Disabled: true, Title: "Chưa dùng được trên bản web (chờ chức năng ghi dữ liệu)")).ToList();

    /// <summary>Kết quả tra cứu F4 (chuẩn 10): chỉ có bảng <c>lookup-result</c>.</summary>
    public static string Lookup(LookupView v, string title) =>
        HtmlPage.Render(title, w => LookupResultRenderer.Write(w, new LookupResultModel(
            (v.Columns ?? []).Select(c => new GridColumn(c.Field, c.Title, Hidden: c.Hidden)).ToList(),
            (v.Rows ?? []).Select((r, i) => new LookupResultRow(v.Values![i], v.Texts![i],
                (v.Columns ?? []).Select((c, ci) => Cell(c, r[ci])).ToList())).ToList(), v.Total)));

    /// <summary>Lỗi cấu hình form (SQL không chạy được trên web chỉ đọc): vẫn là screen để FE mở thành tab.</summary>
    public static string ConfigError(string formName, string? para, string title, string note) =>
        ScreenRenderer.Render(new ScreenModel(formName, FormTypes.Custom, string.IsNullOrEmpty(para) ? formName : $"{formName}_{para}", title,
            Para: string.IsNullOrEmpty(para) ? null : para), w =>
        {
            w.Element("p", "Màn hình này chưa mở được trên bản web vì cấu hình form có phần web chưa hỗ trợ. Vui lòng dùng ERP WinForms.").Line();
            w.Element("p", note).Line();
        });

    private static GridModel Grid(string id, IReadOnlyList<ViewColumn> columns, IReadOnlyList<object?[]> rows, IReadOnlyList<string> keys,
        string? keyField, int total, IReadOnlyDictionary<string, decimal>? sums, Func<string, string>? recordUrl, string? groupBy = null)
    {
        var keyColumn = keyField is null ? -1 : columns.ToList().FindIndex(c => c.Field.Equals(keyField, StringComparison.OrdinalIgnoreCase) && !c.Hidden);
        var gridColumns = columns.Select((c, i) => new GridColumn(c.Field, c.Title, recordUrl is not null && i == keyColumn ? "link" : c.Type, c.Width,
            Decimals: c.Decimals, Hidden: c.Hidden, Sum: c.Sum ? "sum" : null)).ToList();
        var gridRows = rows.Select((r, ri) => new GridRow(keys.Count > ri ? keys[ri] : null, columns.Select((c, ci) =>
        {
            var cell = c.NameOf(r[ci]) is { } name ? new GridCell(name, ViewColumn.CodeKey(r[ci])) : Cell(c, r[ci]);
            return recordUrl is not null && ci == keyColumn && keys.Count > ri && keys[ri].Length > 0
                ? cell with { Href = recordUrl(keys[ri]), LinkTarget = "tab" } : cell;
        }).ToList())).ToList();
        var totals = sums?.Where(s => columns.Any(c => c.Field.Equals(s.Key, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(s => s.Key, s => Number(s.Value, columns.First(c => c.Field.Equals(s.Key, StringComparison.OrdinalIgnoreCase)).Decimals),
                StringComparer.OrdinalIgnoreCase);
        return new GridModel(id, gridColumns, gridRows, keyField is null || keys.Count == 0 ? null : keyField, Selectable: "single",
            TotalRows: total, Totals: totals, GroupBy: groupBy);
    }

    /// <summary>Giá trị → (chữ hiển thị, giá trị máy bất biến) theo chuẩn 2.5.</summary>
    internal static GridCell Cell(ViewColumn c, object? v) => v switch
    {
        null => new GridCell(null),
        bool b => GridCell.Bool(b),
        DateTime d when c.Type == "datetime" => new GridCell(d.ToString("dd/MM/yyyy HH:mm", Vi), d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
        DateTime d => new GridCell(d.ToString("dd/MM/yyyy", Vi), d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        DateTimeOffset d => new GridCell(d.ToString("dd/MM/yyyy HH:mm", Vi), d.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)),
        decimal or double or float or int or long or short or byte when c.Type != "text" => Number(Convert.ToDecimal(v, CultureInfo.InvariantCulture), c.Decimals),
        IFormattable f => new GridCell(f.ToString(null, CultureInfo.InvariantCulture)),
        _ => new GridCell(v.ToString()),
    };

    private static GridCell Number(decimal d, int? decimals)
    {
        var fmt = "#,##0" + (decimals is > 0 ? "." + new string('#', decimals.Value) : "");
        return new GridCell(d.ToString(fmt, Vi), d.ToString("0.############", CultureInfo.InvariantCulture));
    }

    private static FieldModel FilterField(ViewFilter f) => f.Control switch
    {
        "date" => new FieldModel(f.Name, f.Caption, "date", f.Value, Span: 6),
        "select" => new FieldModel(f.Name, f.Caption, "select", f.Value ?? "", Span: 8, Options: f.Options),
        "checkbox" => new FieldModel(f.Name, f.Caption, "checkbox", f.Value == "true" ? "true" : "false", Span: 6),
        "number" => new FieldModel(f.Name, f.Caption, "number", f.Value, Span: 6),
        "search" => new FieldModel(f.Name, f.Caption, "search", f.Value, Span: 14, Placeholder: "Nhập mã, số chứng từ…"),
        _ => new FieldModel(f.Name, f.Caption, "text", f.Value, Span: 8),
    };

    private static FieldModel ViewField(ViewField f, ScreenUrls urls)
    {
        var span = Math.Clamp(f.Span, 1, 40);
        return f.Control switch
        {
            "lookup" when urls.Lookup is not null => new FieldModel(f.Field, f.Caption, "lookup", Text(f.Value), span, ReadOnly: true, RowStart: f.RowStart,
                Display: f.Text, LookupSrc: urls.Lookup(f.Field, f.LookupParas ?? [])),
            "checkbox" => new FieldModel(f.Field, f.Caption, "checkbox", f.Value is true ? "true" : "false", span, ReadOnly: true, RowStart: f.RowStart),
            "date" or "datetime" => new FieldModel(f.Field, f.Caption, f.Control, f.Value is DateTime d
                ? d.ToString(f.Control == "date" ? "yyyy-MM-dd" : "yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture) : null, span, ReadOnly: true, RowStart: f.RowStart),
            "number" or "money" or "quantity" or "percent" => new FieldModel(f.Field, f.Caption, f.Control,
                f.Value is null ? null : Convert.ToDecimal(f.Value, CultureInfo.InvariantCulture).ToString("0.############", CultureInfo.InvariantCulture),
                span, ReadOnly: true, RowStart: f.RowStart, Decimals: f.Decimals),
            "label" => new FieldModel(f.Field, f.Caption, "label", Display: Text(f.Value), Span: span, RowStart: f.RowStart),
            "textarea" => new FieldModel(f.Field, f.Caption, "textarea", Text(f.Value), span, ReadOnly: true, Rows: Math.Max(f.Rows, 2), RowStart: f.RowStart),
            _ => new FieldModel(f.Field, f.Caption, "text", Text(f.Value), span, ReadOnly: true, RowStart: f.RowStart),
        };
    }

    private static string? Text(object? v) => v switch
    {
        null => null,
        DateTime d => d.ToString("dd/MM/yyyy HH:mm", Vi),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    private static string FormType(string t) => t switch
    {
        "LISTEDIT" or "LISTEDIT2" => FormTypes.ListEdit,
        _ => FormTypes.List,
    };

    private static string Slug(string s) => new(s.Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray());
}
