namespace WebErp.Html.Roles;

/// <param name="Key">Khóa chuẩn: <c>company</c>, <c>tax-code</c>, <c>branch</c>, <c>period</c>, <c>database</c>, <c>user</c>,
/// <c>language</c>, <c>server-time</c> (chuẩn 5.2).</param>
public sealed record SessionItem(string Key, string Label, string Text, string? Value = null);

public sealed record SessionLink(string Action, string Text, string Href, string? Target = null);

public sealed record SessionModel(IReadOnlyList<SessionItem> Items, IReadOnlyList<SessionLink> Links);

/// <summary><c>section[data-erp=session]</c> (chuẩn 5.2).</summary>
public static class SessionRenderer
{
    public static string Render(SessionModel m) => HtmlPage.Render("Phiên làm việc", w => Write(w, m));

    public static void Write(HtmlWriter w, SessionModel m)
    {
        w.Open("section", ("data-erp", "session")).Line().Open("dl").Line();
        foreach (var it in m.Items)
        {
            w.Open("div", ("data-key", it.Key))
                .Element("dt", it.Label)
                .Element("dd", it.Text, ("data-value", it.Value))
                .Close("div").Line();
        }
        w.Close("dl").Line();
        foreach (var l in m.Links)
            w.Element("a", l.Text, ("data-action", l.Action), ("href", l.Href), ("data-target", l.Target)).Line();
        w.Close("section");
    }
}
