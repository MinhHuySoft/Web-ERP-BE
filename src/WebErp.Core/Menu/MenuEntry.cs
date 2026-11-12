namespace WebErp.Core.Menu;

/// <summary>
/// Một dòng menu đã được phép thấy (kết quả <c>&lt;Module&gt;_GetUserMenuFnc</c>, tài liệu 12 §4.2, §5.2).
/// </summary>
/// <param name="Caption">Nhãn theo ngôn ngữ của phiên (VN / EN / CH).</param>
/// <param name="FormName">Rỗng = nhóm.</param>
/// <param name="Para">Tham số menu; mã màn thật là <c>FormName_Para</c>.</param>
public sealed record MenuEntry(
    string MenuId,
    string? Parent,
    string Caption,
    string? FormName,
    string? Para,
    bool BeginGroup,
    bool Bold);
