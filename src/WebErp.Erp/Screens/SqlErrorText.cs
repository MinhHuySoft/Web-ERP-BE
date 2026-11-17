using Microsoft.Data.SqlClient;

namespace WebErp.Erp.Screens;

/// <summary>
/// Mô tả lỗi SQL để GHI LOG (không gửi trình duyệt). Lỗi về tên đối tượng (cột / bảng / hàm không có, cú pháp) ghi nguyên văn để biết
/// sai ở đâu; lỗi khác chỉ ghi mã, vì thông điệp của SQL Server có thể chứa giá trị dữ liệu của khách (vd 245 "Conversion failed
/// when converting the varchar value '…'", 2627 trùng khóa kèm giá trị khóa).
/// </summary>
public static class SqlErrorText
{
    /// <summary>Mã lỗi mà thông điệp chỉ chứa tên đối tượng / từ khóa trong câu lệnh, không chứa dữ liệu.</summary>
    public static readonly IReadOnlySet<int> SchemaErrors = new HashSet<int>
    {
        102,  // Incorrect syntax near '…'
        156,  // Incorrect syntax near the keyword '…'
        207,  // Invalid column name '…'
        208,  // Invalid object name '…'
        209,  // Ambiguous column name '…'
        2812, // Could not find stored procedure '…'
        4104, // The multi-part identifier "…" could not be bound
        4121, // Cannot find either column "dbo" or the user-defined function or aggregate "…"
        4145, // An expression of non-boolean type specified in a context where a condition is expected, near '…'
        8120, // Column '…' is invalid in the select list because it is not contained in … GROUP BY
        8156, // The column '…' was specified multiple times for '…'
    };

    public static string Describe(SqlException ex) => Describe(ex.Number, ex.Errors.Count > 0 ? ex.Errors[0].Message : ex.Message);

    public static string Describe(int number, string message)
    {
        if (!SchemaErrors.Contains(number)) return $"SQL {number}; nội dung lỗi không ghi vì có thể chứa dữ liệu";
        var one = string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return $"SQL {number}: {(one.Length <= 300 ? one : one[..300])}";
    }
}
