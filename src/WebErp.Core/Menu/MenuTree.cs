namespace WebErp.Core.Menu;

public enum MenuKind
{
    Group,
    Form,
    Report,
    Command,
}

public sealed class MenuNode(MenuEntry entry, MenuKind kind, int depth)
{
    public MenuEntry Entry { get; } = entry;
    public MenuKind Kind { get; } = kind;

    /// <summary>1 = cấp đầu (tab Ribbon).</summary>
    public int Depth { get; } = depth;

    public List<MenuNode> Children { get; } = [];

    /// <summary>Mã màn hình duy nhất: <c>FormName</c> hoặc <c>FormName_Para</c> (tài liệu 12 §4.2).</summary>
    public string? ScreenKey => string.IsNullOrEmpty(Entry.FormName) ? null
        : string.IsNullOrEmpty(Entry.Para) ? Entry.FormName : $"{Entry.FormName}_{Entry.Para}";
}

/// <summary>Dựng cây menu từ danh sách phẳng, giữ thứ tự theo <c>MenuID</c> (chuẩn ERP-HTML 5.3).</summary>
public static class MenuTree
{
    /// <summary>Chuẩn ERP-HTML 5.3: tối đa 5 cấp (hàm menu của ERP cũng chỉ thêm cha 4 lần).</summary>
    public const int MaxDepth = 5;

    private static readonly HashSet<string> LogoutForms = new(StringComparer.OrdinalIgnoreCase) { "Exit", "LogOff" };

    public static MenuKind KindOf(MenuEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.FormName)) return MenuKind.Group;
        if (LogoutForms.Contains(entry.FormName)) return MenuKind.Command;
        // Mọi báo cáo của ERP (mở bằng ReportViewerFrm) đều có FormName kết thúc bằng "Report" (đối chiếu menu Medcom).
        return entry.FormName.EndsWith("Report", StringComparison.OrdinalIgnoreCase) ? MenuKind.Report : MenuKind.Form;
    }

    public static IReadOnlyList<MenuNode> Build(IEnumerable<MenuEntry> entries)
    {
        var list = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.MenuId))
            .GroupBy(e => e.MenuId, StringComparer.Ordinal).Select(g => g.First())
            .OrderBy(e => e.MenuId, StringComparer.Ordinal)
            .ToList();
        var ids = list.Select(e => e.MenuId).ToHashSet(StringComparer.Ordinal);
        var byParent = list
            .Where(e => !string.IsNullOrWhiteSpace(e.Parent) && ids.Contains(e.Parent!) && e.Parent != e.MenuId)
            .ToLookup(e => e.Parent!, StringComparer.Ordinal);
        var roots = list.Where(e => string.IsNullOrWhiteSpace(e.Parent) || !ids.Contains(e.Parent!) || e.Parent == e.MenuId);

        var result = new List<MenuNode>();
        foreach (var root in roots)
            if (Make(root, 1, byParent) is { } node) result.Add(node);
        return result;
    }

    private static MenuNode? Make(MenuEntry entry, int depth, ILookup<string, MenuEntry> byParent)
    {
        var kind = KindOf(entry);
        var node = new MenuNode(entry, kind, depth);
        if (kind != MenuKind.Group) return node;
        if (depth >= MaxDepth) return null;
        foreach (var child in byParent[entry.MenuId])
            if (Make(child, depth + 1, byParent) is { } c) node.Children.Add(c);
        // Nhóm không còn mục lá nào (sau khi lọc quyền) thì bỏ, giống WinForms không hiện popup rỗng.
        return node.Children.Count > 0 ? node : null;
    }
}
