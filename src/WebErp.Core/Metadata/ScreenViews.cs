namespace WebErp.Core.Metadata;

/// <summary>Quyền của người dùng trên một form (<c>Functions.GetPermission</c>, tài liệu 12 §5.3).</summary>
/// <param name="HasRow">Có dòng quyền. Không có dòng: web KHÔNG mở rộng quyền như WinForms (fail-open), chỉ cho xem nếu màn có trong menu.</param>
public sealed record FormPermission(bool HasRow, bool Run, bool Add, bool Update, bool Delete, bool Manager, bool Admin, bool HideAmount, bool ExportExcel)
{
    /// <summary>Không có dòng quyền nào (vd menu <c>isNotCheckPermission</c>): chỉ xem, chỉ thấy chứng từ của mình.</summary>
    public static readonly FormPermission ViewOnly = new(false, true, false, false, false, false, false, false, false);

    public bool View => Run || Add || Update || Delete || Manager || Admin;

    /// <summary>Thấy chứng từ của mọi người (không lọc theo <c>UserCreate</c>).</summary>
    public bool SeesAll => Manager || Admin;

    /// <summary>Chỉ có quyền Thêm: WinForms không cho thấy dữ liệu nào (<c>1=0</c>, tài liệu 10 §3.6).</summary>
    public bool AddOnly => HasRow && Add && !Run && !Update && !Delete && !Manager && !Admin;
}

/// <summary>Ai đang mở màn và với giá trị lọc nào. Công ty và người dùng luôn lấy từ phiên, không từ tham số FE (QĐ-07).</summary>
public sealed record ScreenRequest(
    Tenancy.Tenant Tenant,
    string UserName,
    string? BranchId,
    string FormName,
    string? Para,
    IReadOnlyDictionary<string, string> Query)
{
    public string RuntimeName => string.IsNullOrEmpty(Para) ? FormName : $"{FormName}_{Para}";
}

/// <summary>Một cột đã quyết định xong (thứ tự, tiêu đề, kiểu, ẩn theo quyền đã loại ra từ trước).</summary>
/// <param name="Type"><c>text</c>, <c>number</c>, <c>money</c>, <c>quantity</c>, <c>percent</c>, <c>date</c>, <c>datetime</c>, <c>bool</c>.</param>
public sealed record ViewColumn(string Field, string Title, string Type, int? Decimals, bool Hidden, bool Sum, int? Width = null);

/// <summary>Một ô lọc đã dựng (giá trị hiện tại đã áp mặc định).</summary>
/// <param name="Control"><c>date</c>, <c>text</c>, <c>number</c>, <c>select</c>, <c>checkbox</c>, <c>search</c>.</param>
public sealed record ViewFilter(string Name, string Caption, string Control, string? Value, IReadOnlyList<(string Value, string Text)>? Options = null);

/// <summary><c>ConfigError</c>: cấu hình form có SQL web không chạy được (lệnh ghi, token lạ, lỗi cú pháp); chi tiết ở <c>Note</c>.</summary>
public enum ScreenStatus { Ok, NotSupported, NotFound, Forbidden, ConfigError }

/// <summary>Kết quả mở màn danh sách.</summary>
/// <param name="Rows">Giá trị thô theo thứ tự <see cref="Columns"/>.</param>
/// <param name="Keys">Khóa chính từng dòng (song song với <see cref="Rows"/>).</param>
/// <param name="Total">Tổng số dòng khớp bộ lọc (có thể lớn hơn số dòng gửi).</param>
public sealed record ListView(
    ScreenStatus Status,
    FormMeta? Form = null,
    IReadOnlyList<ViewColumn>? Columns = null,
    IReadOnlyList<object?[]>? Rows = null,
    IReadOnlyList<string>? Keys = null,
    int Total = 0,
    IReadOnlyDictionary<string, decimal>? Sums = null,
    IReadOnlyList<ViewFilter>? Filters = null,
    string? KeyField = null,
    bool CanOpenRecord = false,
    string? Note = null);

/// <summary>Một ô của chứng từ đang xem.</summary>
public sealed record ViewField(string Field, string Caption, string Control, object? Value, int Span, int Rows, bool RowStart, int? Decimals);

public sealed record ViewTab(string Id, string Title, IReadOnlyList<ViewField> Fields);

public sealed record ViewDetailTab(string Id, string Title);

/// <summary>Kết quả xem một chứng từ (EDIT, <c>data-mode=view</c>).</summary>
public sealed record RecordView(
    ScreenStatus Status,
    FormMeta? Form = null,
    string? Key = null,
    string? KeyField = null,
    IReadOnlyList<ViewTab>? Tabs = null,
    IReadOnlyList<ViewDetailTab>? Details = null,
    string? Note = null);

/// <summary>Lưới của một tab chi tiết (nạp lười).</summary>
public sealed record DetailView(
    ScreenStatus Status,
    string? SlotId = null,
    IReadOnlyList<ViewColumn>? Columns = null,
    IReadOnlyList<object?[]>? Rows = null,
    IReadOnlyList<string>? Keys = null,
    int Total = 0,
    IReadOnlyDictionary<string, decimal>? Sums = null,
    string? KeyField = null,
    string? Note = null);

/// <summary>Màn hình dựng từ metadata ERP (M2 danh sách, M3 xem chứng từ). Chỉ đọc DB ERP.</summary>
public interface IErpScreens
{
    Task<ListView> OpenListAsync(ScreenRequest request, CancellationToken ct);

    Task<RecordView> OpenRecordAsync(ScreenRequest request, string key, CancellationToken ct);

    Task<DetailView> OpenDetailAsync(ScreenRequest request, string key, string slotId, CancellationToken ct);
}
