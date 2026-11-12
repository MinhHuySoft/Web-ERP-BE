using System.Text;
using System.Text.RegularExpressions;
using Dapper;

namespace WebErp.Erp.Query;

/// <summary>Đoạn SQL lấy từ metadata ERP có lệnh ghi / nhiều câu lệnh: web chỉ đọc nên từ chối.</summary>
public sealed class UnsafeMetadataSqlException(string where, string reason)
    : Exception($"Đoạn SQL '{where}' trong cấu hình form không chạy được trên web chỉ đọc: {reason}.")
{
    public string Where { get; } = where;
}

/// <summary>
/// SQL lấy từ metadata ERP (FTR, FTX, SE, join, nguồn combo…). Hai việc:
/// <list type="number">
/// <item><see cref="EnsureReadOnly"/>: chặn đoạn có lệnh ghi, nhiều câu lệnh, gọi thủ tục (lớp 1; lớp 2 là giao dịch luôn rollback).</item>
/// <item><see cref="Bind"/>: thay token <c>{User}</c>, <c>{BranchID}</c>… (<c>SystemPara.ReplaceSourceSQL</c>, tài liệu 10 §7.2) bằng THAM SỐ
/// SQL thay vì nối chuỗi như WinForms (QĐ-03, ngoại lệ bảo mật 2). Token trong chuỗi <c>'%{User}%'</c> thành <c>('%' + @p + '%')</c>.</item>
/// </list>
/// </summary>
public static partial class SqlTemplate
{
    [GeneratedRegex(@"\b(insert|update|delete|merge|exec|execute|drop|alter|create|truncate|grant|revoke|deny|backup|restore|shutdown|dbcc|waitfor|into|openrowset|opendatasource|openquery|openxml|bulk|xp_\w+|sp_\w+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Forbidden();

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*|\d+)\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"@(User|USER)(?![A-Za-z0-9_@#$])")]
    private static partial Regex BareUser();

    /// <summary>Ném <see cref="UnsafeMetadataSqlException"/> nếu đoạn SQL (ngoài chuỗi và chú thích) có <c>;</c> hoặc từ khóa ghi.</summary>
    public static void EnsureReadOnly(string? sql, string where)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        var code = StripLiteralsAndComments(sql);
        if (code.Contains(';', StringComparison.Ordinal)) throw new UnsafeMetadataSqlException(where, "có nhiều câu lệnh (;)");
        var m = Forbidden().Match(code);
        if (m.Success) throw new UnsafeMetadataSqlException(where, $"có từ khóa '{m.Value}'");
    }

    /// <summary>
    /// Thay token bằng tham số. <paramref name="resolve"/> trả <c>(true, giá trị)</c> cho token biết; token không biết giữ nguyên
    /// (giống WinForms: câu SQL sẽ lỗi và màn báo lỗi cấu hình).
    /// </summary>
    public static string Bind(string sql, Func<string, (bool Known, object? Value)> resolve, DynamicParameters parameters, ref int counter)
    {
        var sb = new StringBuilder(sql.Length + 16);
        var i = 0;
        var n = counter;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'')
            {
                // Chuỗi: tìm dấu đóng ('' là nháy trong chuỗi). Tiền tố N (nếu có) đã được chép ở vòng trước.
                var unicode = sb.Length > 0 && (sb[^1] == 'N' || sb[^1] == 'n') && (sb.Length == 1 || !IsIdent(sb[^2]));
                var j = i + 1;
                var content = new StringBuilder();
                while (j < sql.Length)
                {
                    if (sql[j] == '\'' && j + 1 < sql.Length && sql[j + 1] == '\'') { content.Append("''"); j += 2; continue; }
                    if (sql[j] == '\'') break;
                    content.Append(sql[j]);
                    j++;
                }
                var text = content.ToString();
                if (!Token().IsMatch(text)) sb.Append(sql, i, Math.Min(j + 1, sql.Length) - i);
                else
                {
                    if (unicode) sb.Length--; // bỏ N: các mảnh tự mang N
                    sb.Append('(').Append(LiteralWithParams(text, unicode, resolve, parameters, ref n)).Append(')');
                }
                i = j + 1;
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                sb.Append(sql, i, end - i);
                i = end;
                continue;
            }
            if (c == '{')
            {
                var m = Token().Match(sql, i);
                if (m.Success && m.Index == i)
                {
                    var (known, value) = resolve(m.Groups[1].Value);
                    if (known) sb.Append(Add(parameters, value, ref n));
                    else sb.Append(m.Value);
                    i += m.Length;
                    continue;
                }
            }
            if (c == '@')
            {
                var m = BareUser().Match(sql, i);
                if (m.Success && m.Index == i && (i == 0 || !IsIdent(sql[i - 1])))
                {
                    var (known, value) = resolve("User");
                    if (known) { sb.Append(Add(parameters, value, ref n)); i += m.Length; continue; }
                }
            }
            sb.Append(c);
            i++;
        }
        counter = n;
        return sb.ToString();
    }

    private static string LiteralWithParams(string text, bool unicode, Func<string, (bool, object?)> resolve, DynamicParameters p, ref int n)
    {
        var parts = new List<string>();
        var last = 0;
        foreach (Match m in Token().Matches(text))
        {
            var (known, value) = resolve(m.Groups[1].Value);
            if (!known) continue;
            if (m.Index > last) parts.Add(Quote(text[last..m.Index], unicode));
            parts.Add(Add(p, value, ref n));
            last = m.Index + m.Length;
        }
        if (last < text.Length) parts.Add(Quote(text[last..], unicode));
        return parts.Count == 0 ? Quote("", unicode) : string.Join(" + ", parts);
    }

    private static string Quote(string escapedContent, bool unicode) => (unicode ? "N'" : "'") + escapedContent + "'";

    /// <summary>Thêm tham số; chuỗi ASCII đi kiểu varchar để không làm SQL Server đổi kiểu cả cột khi so sánh.</summary>
    public static string Add(DynamicParameters p, object? value, ref int n)
    {
        var name = $"@__p{n++}";
        switch (value)
        {
            case null:
                p.Add(name, null);
                break;
            case string s:
                p.Add(name, new DbString { Value = s, IsAnsi = s.All(ch => ch < 128), Length = Math.Max(s.Length, 1) <= 4000 ? 4000 : -1 });
                break;
            default:
                p.Add(name, value);
                break;
        }
        return name;
    }

    private static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$' or ']';

    /// <summary>Thay nội dung chuỗi và chú thích bằng khoảng trắng để kiểm từ khóa không bị nhầm (vd <c>'Update xong'</c>).</summary>
    public static string StripLiteralsAndComments(string sql)
    {
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '\'')
            {
                i++;
                while (i < sql.Length && !(sql[i] == '\'' && (i + 1 >= sql.Length || sql[i + 1] != '\''))) i += sql[i] == '\'' ? 2 : 1;
                i++;
                sb.Append(" '' ");
                continue;
            }
            if (c == '[')
            {
                // Tên trong ngoặc vuông (vd [Update Date]) không phải từ khóa.
                var end = sql.IndexOf(']', i);
                end = end < 0 ? sql.Length : end + 1;
                sb.Append(" [x] ");
                i = end;
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? sql.Length : end + 2;
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
