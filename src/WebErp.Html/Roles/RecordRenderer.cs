using WebErp.Core.Messages;

namespace WebErp.Html.Roles;

/// <param name="Id">Mã tab (vd <c>1</c>, <c>A</c> theo LYT của ERP). Trống thì đánh số 1, 2, 3…</param>
public sealed record FieldsetModel(string Legend, IReadOnlyList<FieldModel> Fields, string? Id = null);

/// <param name="Version"><c>__version</c> để phát hiện xung đột khi lưu (chuẩn 9).</param>
public sealed record RecordModel(
    string Id,
    string Action,
    string AntiforgeryField,
    string AntiforgeryToken,
    IReadOnlyList<FieldsetModel> Tabs,
    string? KeyField = null,
    string? Key = null,
    string? Version = null);

/// <summary><c>form[data-erp=record]</c> (chuẩn 9) với các <c>fieldset[data-erp=tab]</c>.</summary>
public static class RecordRenderer
{
    public static void Write(HtmlWriter w, RecordModel r)
    {
        w.Open("form", ("data-erp", "record"), ("id", r.Id), ("method", "post"), ("action", r.Action),
            ("data-key-field", r.KeyField), ("data-key", r.Key)).Line();
        w.Void("input", ("type", "hidden"), ("name", r.AntiforgeryField), ("value", r.AntiforgeryToken)).Line();
        if (r.Version is not null) w.Void("input", ("type", "hidden"), ("name", "__version"), ("value", r.Version)).Line();
        var i = 1;
        foreach (var tab in r.Tabs)
        {
            var tabId = tab.Id ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            i++;
            w.Open("fieldset", ("data-erp", "tab"), ("data-tab-id", tabId))
                .Element("legend", tab.Legend).Line();
            foreach (var f in tab.Fields) FieldRenderer.Write(w, r.Id, f);
            w.Close("fieldset").Line();
        }
        w.Close("form").Line();
    }
}

/// <param name="Size"><c>sm</c> / <c>md</c> / <c>lg</c>.</param>
public sealed record DialogModel(
    string Title,
    string Action,
    string AntiforgeryField,
    string AntiforgeryToken,
    IReadOnlyList<FieldModel> Fields,
    string SubmitText,
    string SubmitAction = "save",
    string Size = "sm",
    IReadOnlyList<string>? Paragraphs = null,
    IReadOnlyList<ErpMessage>? Messages = null);

/// <summary><c>section[data-erp=dialog]</c> chứa một form tham số (chuẩn 14.2).</summary>
public static class DialogRenderer
{
    public static string Render(DialogModel d) => HtmlPage.Render(d.Title, w => Write(w, d));

    public static void Write(HtmlWriter w, DialogModel d)
    {
        w.Open("section", ("data-erp", "dialog"), ("data-size", d.Size)).Line();
        w.Element("h1", d.Title).Line();
        MessagesRenderer.Write(w, d.Messages);
        foreach (var p in d.Paragraphs ?? []) w.Element("p", p).Line();
        const string formId = "dlg";
        w.Open("form", ("id", formId), ("method", "post"), ("action", d.Action)).Line();
        foreach (var f in d.Fields) FieldRenderer.Write(w, formId, f);
        w.Void("input", ("type", "hidden"), ("name", d.AntiforgeryField), ("value", d.AntiforgeryToken)).Line();
        w.Element("button", d.SubmitText, ("type", "submit"), ("data-action", d.SubmitAction));
        w.Text(" ");
        w.Element("button", "Đóng", ("type", "button"), ("data-action", "close")).Line();
        w.Close("form").Line();
        w.Close("section");
    }
}
