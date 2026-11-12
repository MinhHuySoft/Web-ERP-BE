using System.Globalization;
using WebErp.Core.Messages;

namespace WebErp.Html.Roles;

/// <summary><c>div[data-erp=messages]</c> (chuẩn 13.1).</summary>
public static class MessagesRenderer
{
    public static void Write(HtmlWriter w, IReadOnlyList<ErpMessage>? messages)
    {
        if (messages is null || messages.Count == 0) return;
        w.Open("div", ("data-erp", "messages"));
        foreach (var m in messages)
        {
            w.Element("p", m.Text,
                ("data-erp", "message"),
                ("data-msg-type", TypeName(m.Type)),
                ("data-field", m.Field),
                ("data-legacy-type", m.LegacyType?.ToString(CultureInfo.InvariantCulture)));
        }
        w.Close("div").Line();
    }

    public static string TypeName(MessageType type) => type switch
    {
        MessageType.Error => "error",
        MessageType.Warning => "warning",
        MessageType.Success => "success",
        _ => "info",
    };
}
