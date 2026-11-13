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
/// <param name="Names">Tên thay mã (danh sách chọn của <c>SY_FrmDrdwTbl</c>): khóa <see cref="CodeKey"/> của giá trị → chữ hiển thị.
/// Ô vẫn giữ mã làm giá trị máy (<c>td[data-value]</c>).</param>
public sealed record ViewColumn(string Field, string Title, string Type, int? Decimals, bool Hidden, bool Sum, int? Width = null,
    IReadOnlyDictionary<string, string>? Names = null)
{
    /// <summary>Khóa so mã (bất biến, bỏ khoảng trắng cuối như SQL Server so chuỗi).</summary>
    public static string? CodeKey(object? value) => value switch
    {
        null => null,
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture).TrimEnd(),
        _ => value.ToString()?.TrimEnd(),
    };

    /// <summary>Chữ thay cho mã, null nếu cột không có tên hoặc mã không có trong danh sách.</summary>
    public string? NameOf(object? value) => Names is not null && CodeKey(value) is { Length: > 0 } k && Names.TryGetValue(k, out var n) ? n : null;
}

/// <summary>Một ô lọc đã dựng (giá trị hiện tại đã áp mặc định).</summary>
/// <param name="Control"><c>date</c>, <c>text</c>, <c>number</c>, <c>select</c>, <c>checkbox</c>, <c>search</c>.</param>
public sealed record ViewFilter(string Name, string Caption, string Control, string? Value, IReadOnlyList<(string Value, string Text)>? Options = null);

/// <summary><c>ConfigError</c>: cấu hình form có SQL web không chạy được (lệnh ghi, token lạ, lỗi cú pháp); chi tiết ở <c>Note</c>.</summary>
public enum ScreenStatus { Ok, NotSupported, NotFound, Forbidden, ConfigError }

/// <summary>Kết quả mở màn danh sách.</summary>
/// <param name="Rows">Giá trị thô theo thứ tự <see cref="Columns"/>.</param>
/// <param name="Keys">Khóa chính từng dòng (song song với <see cref="Rows"/>).</param>
/// <param name="Total">Tổng số dòng khớp bộ lọc (có thể lớn hơn số dòng gửi).</param>
/// <param name="Page">Trang đang xem (từ 1).</param>
/// <param name="PageSize">Số dòng mỗi trang; 0 = không phân trang (slot có <c>TRS</c>: WinForms chỉ lấy top N).</param>
/// <param name="Tabs">Tab độc lập (key <c>IND</c>) hiện dưới lưới danh sách, nạp lười (khóa chứng từ là <c>-</c>).</param>
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
    string? Note = null,
    int Page = 1,
    int PageSize = 0,
    string? GroupBy = null,
    IReadOnlyList<ViewDetailTab>? Tabs = null);

/// <summary>Một ô của chứng từ đang xem.</summary>
/// <param name="Control"><c>text</c>, <c>textarea</c>, <c>label</c>, <c>checkbox</c>, <c>date</c>, <c>datetime</c>, số…, hoặc <c>lookup</c>
/// (ô có danh sách chọn / tra cứu F4 của <c>SY_FrmDrdwTbl</c>).</param>
/// <param name="Text">Chữ kèm mã của ô <c>lookup</c> (tên thay mã hoặc cột hiển thị thứ hai).</param>
/// <param name="LookupParas">Giá trị tham số <c>{0}</c>, <c>{1}</c>… của nguồn tra cứu, lấy từ chứng từ đang xem.</param>
public sealed record ViewField(string Field, string Caption, string Control, object? Value, int Span, int Rows, bool RowStart, int? Decimals,
    string? Text = null, IReadOnlyList<string>? LookupParas = null);

public sealed record ViewTab(string Id, string Title, IReadOnlyList<ViewField> Fields);

/// <summary>Một trang chi tiết của chứng từ.</summary>
/// <param name="Fields">Ô của chứng từ đặt trên trang này (<c>LYT5…LYT13</c>), hiện trước lưới; null = không có.</param>
/// <param name="Lazy">Có lưới nạp lười (<c>data-src</c>). <c>false</c> = trang chỉ có ô (vd "Công nợ &amp; Giảm giá / CK" của hóa đơn bán).</param>
public sealed record ViewDetailTab(string Id, string Title, IReadOnlyList<ViewField>? Fields = null, bool Lazy = true);

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
    string? Note = null,
    string? GroupBy = null);

/// <summary>Kết quả tra cứu F4 (chuẩn 10, <c>table[data-erp=lookup-result]</c>).</summary>
/// <param name="Values">Giá trị gán vào ô (song song với <see cref="Rows"/>).</param>
/// <param name="Texts">Chữ hiển thị kèm mã.</param>
/// <param name="Total">Tổng số dòng khớp (có thể lớn hơn số dòng gửi).</param>
public sealed record LookupView(
    ScreenStatus Status,
    IReadOnlyList<ViewColumn>? Columns = null,
    IReadOnlyList<object?[]>? Rows = null,
    IReadOnlyList<string>? Values = null,
    IReadOnlyList<string>? Texts = null,
    int Total = 0,
    string? Note = null);

/// <summary>Màn hình dựng từ metadata ERP (M2 danh sách, M3 xem chứng từ). Chỉ đọc DB ERP.</summary>
public interface IErpScreens
{
    Task<ListView> OpenListAsync(ScreenRequest request, CancellationToken ct);

    Task<RecordView> OpenRecordAsync(ScreenRequest request, string key, CancellationToken ct);

    Task<DetailView> OpenDetailAsync(ScreenRequest request, string key, string slotId, CancellationToken ct);

    /// <summary>
    /// Tra cứu F4 của một ô chứng từ: tìm <paramref name="text"/> trên các cột chữ của danh sách (như <c>SearchTopRecord</c>).
    /// <paramref name="paras"/>: giá trị <c>{0}</c>, <c>{1}</c>… theo <c>ParaArr</c>.
    /// </summary>
    Task<LookupView> LookupAsync(ScreenRequest request, string field, string text, IReadOnlyList<string> paras, CancellationToken ct);
}
