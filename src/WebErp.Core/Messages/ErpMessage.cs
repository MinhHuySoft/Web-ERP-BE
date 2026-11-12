namespace WebErp.Core.Messages;

/// <summary>Loại thông điệp theo chuẩn ERP-HTML 13.1 (<c>data-msg-type</c>).</summary>
public enum MessageType
{
    Error,
    Warning,
    Info,
    Success,
}

/// <param name="Field">Gắn vào ô (<c>data-field</c>).</param>
/// <param name="LegacyType"><c>MsgType</c> gốc khi thông điệp đến từ SP/hook (<c>data-legacy-type</c>).</param>
public sealed record ErpMessage(MessageType Type, string Text, string? Field = null, int? LegacyType = null)
{
    public static ErpMessage Error(string text, string? field = null) => new(MessageType.Error, text, field);
    public static ErpMessage Warning(string text, string? field = null) => new(MessageType.Warning, text, field);
    public static ErpMessage Info(string text) => new(MessageType.Info, text);
    public static ErpMessage Success(string text) => new(MessageType.Success, text);
}
