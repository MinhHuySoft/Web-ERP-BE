using WebErp.Core.Messages;
using WebErp.Core.Metadata;

namespace WebErp.Core.Print;

/// <summary>Một mẫu chọn được của báo cáo (<c>SY_RpDTbl</c>, <c>PName = 'Template'</c>, chuỗi "tệp;Tên;tệp;Tên…").</summary>
public sealed record PrintTemplateOption(string File, string Title);

/// <summary>Một result set của SP báo cáo: cột (kiểu chuẩn 11.2) và giá trị thô.</summary>
public sealed record PrintDataSet(IReadOnlyList<ViewColumn> Columns, IReadOnlyList<object?[]> Rows, bool Truncated = false);

/// <summary>Một tham số in (chuẩn 15.1): tên trơn = tham số báo cáo; <c>Sys.</c>, <c>Lang.</c>, <c>Caption.</c> theo đặc tả.</summary>
/// <param name="Type"><c>text</c>, <c>number</c>, <c>date</c>, <c>datetime</c>, <c>bool</c>.</param>
public sealed record PrintParam(string Name, string Type, object? Value, string? Caption = null);

/// <summary>Ô người ký (<c>UInput1..6</c> của <c>SY_RpDTbl</c>): tên hiển thị + giá trị người dùng gõ.</summary>
public sealed record PrintUserInput(string Name, string Caption, string? Value);

/// <summary>Kết quả chuẩn bị một bản in.</summary>
/// <param name="Report">Tên báo cáo (<c>&lt;Form bỏ Frm&gt;Report</c> hoặc <c>BPRN</c>).</param>
/// <param name="Template">Mẫu đang chọn (tên tệp không đuôi).</param>
/// <param name="TemplateFormat"><c>rpx</c> (tệp WinForms) hoặc <c>json</c> (mẫu web).</param>
public sealed record PrintView(
    ScreenStatus Status,
    string Report = "",
    IReadOnlyList<PrintTemplateOption>? Templates = null,
    string Template = "",
    string TemplateFormat = "rpx",
    IReadOnlyList<string>? DocumentIds = null,
    IReadOnlyList<PrintParam>? Params = null,
    IReadOnlyList<PrintUserInput>? UserInputs = null,
    IReadOnlyList<PrintDataSet>? Sets = null,
    IReadOnlyList<ErpMessage>? Messages = null,
    string? Note = null);

/// <summary>Nội dung mẫu in để FE dựng (<c>data-template-src</c>).</summary>
/// <param name="Source"><c>db</c> (bản mới nhất <c>SY_RpFTbl</c>), <c>folder</c> (thư mục Reports của công ty).</param>
public sealed record PrintTemplateFile(string FileName, byte[] Content, DateTime? FileDate, string Source, string ContentType = "application/xml");

/// <summary>In theo mẫu (M6, docs FE 06-mau-in.md): như <c>ReportViewerFrm</c> của WinForms, chỉ đọc DB ERP.</summary>
public interface IErpPrint
{
    /// <summary>Tên báo cáo của một form chứng từ, null nếu form không có SP báo cáo (<c>&lt;Report&gt;Stp</c>).</summary>
    Task<string?> ReportOfAsync(ScreenRequest request, CancellationToken ct);

    /// <summary>
    /// Chuẩn bị bản in các chứng từ <paramref name="documentIds"/> của form: chọn mẫu, chạy <c>&lt;Report&gt;Stp</c> (<c>@Template</c>,
    /// <c>@DocumentID</c> nối bằng <c>;</c>) trong giao dịch luôn rollback, tham số hệ thống mẫu cần.
    /// </summary>
    Task<PrintView> PrepareAsync(ScreenRequest request, IReadOnlyList<string> documentIds, string? template,
        IReadOnlyDictionary<string, string> userInputs, CancellationToken ct);

    /// <summary>Tệp mẫu <paramref name="template"/> của báo cáo của form; null nếu mẫu không thuộc báo cáo hoặc không tìm thấy.</summary>
    Task<PrintTemplateFile?> TemplateAsync(ScreenRequest request, string template, CancellationToken ct);
}
