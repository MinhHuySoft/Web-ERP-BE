namespace WebErp.Html.Roles;

/// <param name="Id">Tiền tố id của các ô (duy nhất trong trang, vd <c>flt-AP_PurposeRequestListFrm</c>).</param>
/// <param name="Hidden">Tham số giữ nguyên khi lọc lại (vd <c>para</c>): form GET bỏ query string của <c>action</c>.</param>
public sealed record FilterModel(string Id, string Action, IReadOnlyList<FieldModel> Fields,
    IReadOnlyList<(string Name, string Value)>? Hidden = null, string SubmitText = "Lọc");

/// <summary><c>form[data-erp=filter] method=get</c> (chuẩn 8): BE trả lại màn hình với lưới đã lọc.</summary>
public static class FilterRenderer
{
    public static void Write(HtmlWriter w, FilterModel f)
    {
        w.Open("form", ("data-erp", "filter"), ("method", "get"), ("action", f.Action)).Line();
        foreach (var (name, value) in f.Hidden ?? []) w.Void("input", ("type", "hidden"), ("name", name), ("value", value)).Line();
        foreach (var field in f.Fields) FieldRenderer.Write(w, f.Id, field);
        w.Element("button", f.SubmitText, ("type", "submit"), ("data-action", "find")).Line();
        w.Close("form").Line();
    }
}
