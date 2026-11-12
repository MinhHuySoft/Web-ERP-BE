using System.Data.Common;
using System.Text;
using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Metadata;
using WebErp.Erp.Query;

namespace WebErp.Erp.Screens;

/// <summary>
/// Dựng câu SELECT cho một slot bảng như <c>TableInfoX</c> + <c>Storer.get_Source</c> (tài liệu 10 §4.5):
/// <c>Select [Distinct] top N {SE | Source.*} [,EX] [,SE1..4] From Source [joins] Where … Order By SO</c>.
/// Mọi đoạn lấy từ metadata qua <see cref="SqlTemplate.EnsureReadOnly"/> rồi <see cref="SqlTemplate.Bind"/>; giá trị người dùng
/// luôn là tham số.
/// </summary>
internal sealed class SlotQuery(SlotMeta slot, Func<string, (bool, object?)> resolve)
{
    private int _n;

    public SlotMeta Slot { get; } = slot;
    public DynamicParameters Parameters { get; } = new();
    public List<string> Where { get; } = [];

    /// <summary>Cột của bảng/view nguồn, có tên bảng phía trước (tránh trùng tên khi có join).</summary>
    public string Col(string column) => $"{Slot.Source}.[{column.Replace("]", "]]", StringComparison.Ordinal)}]";

    public string Param(object? value) => SqlTemplate.Add(Parameters, value, ref _n);

    /// <summary>Đoạn SQL từ metadata: kiểm chỉ đọc rồi thay token bằng tham số.</summary>
    public string Fragment(string sql, string where, Func<string, (bool, object?)>? extra = null)
    {
        SqlTemplate.EnsureReadOnly(sql, where);
        var r = extra is null ? resolve : t => extra(t) is (true, var v) ? (true, v) : resolve(t);
        return SqlTemplate.Bind(sql, r, Parameters, ref _n);
    }

    public string SelectList()
    {
        var sb = new StringBuilder(Slot.Select is null ? $"{Slot.Source}.*" : Fragment(Slot.Select, $"{Slot.Id}.SE"));
        if (Slot.Expression is not null) sb.Append(", ").Append(Fragment(Slot.Expression, $"{Slot.Id}.EX"));
        foreach (var j in Slot.Joins.Where(j => j.Select is not null)) sb.Append(", ").Append(Fragment(j.Select!, $"{Slot.Id}.SE"));
        return sb.ToString();
    }

    public string From()
    {
        var sb = new StringBuilder(Fragment(Slot.FromOverride ?? Slot.Source, $"{Slot.Id}.TN/TV"));
        foreach (var j in Slot.Joins)
        {
            var left = j.LeftTable ?? Slot.Source;
            sb.Append(j.Inner ? " INNER JOIN " : " LEFT JOIN ").Append(Fragment(j.Table, $"{Slot.Id}.TN")).Append(' ').Append(j.Alias)
              .Append(" ON ").Append(Fragment($"{left}.{j.LeftKey} = {j.Alias}.{j.RightKey}", $"{Slot.Id}.FK"));
            if (j.Extra is not null) sb.Append(' ').Append(Fragment(j.Extra, $"{Slot.Id}.JC"));
        }
        return sb.ToString();
    }

    public string WhereSql => Where.Count == 0 ? "" : " WHERE " + string.Join(" AND ", Where.Select(w => $"({w})"));

    public string OrderBy() => Slot.Sort is null ? Col(Slot.PrimaryKey) : Fragment(Slot.Sort, $"{Slot.Id}.SO");

    public string ListSql(int take) =>
        $"SELECT {(Slot.Distinct ? "DISTINCT " : "")}TOP ({take}) {SelectList()} FROM {From()}{WhereSql} ORDER BY {OrderBy()}";

    /// <summary>Đếm (và cộng các cột của bảng nguồn) trên toàn bộ dữ liệu khớp lọc.</summary>
    public string CountSql(IReadOnlyList<string> sumColumns)
    {
        if (Slot.Distinct) return $"SELECT COUNT(*) FROM (SELECT DISTINCT {SelectList()} FROM {From()}{WhereSql}) q";
        var sums = string.Concat(sumColumns.Select((c, i) => $", SUM(CAST({Col(c)} AS decimal(38,6))) AS [s{i}]"));
        return $"SELECT COUNT(*){sums} FROM {From()}{WhereSql}";
    }
}

/// <summary>Kết quả đọc: tên cột (đã bỏ trùng tên, giữ cột đầu), kiểu, các dòng.</summary>
internal sealed record SqlRows(string[] Names, Type[] Types, List<object?[]> Rows);

internal static class SqlReader
{
    public static async Task<SqlRows> ReadAsync(SqlConnection c, SqlTransaction tx, string sql, DynamicParameters p, int timeout, int max, CancellationToken ct)
    {
        await using var r = (DbDataReader)await c.ExecuteReaderAsync(new CommandDefinition(sql, p, tx, timeout, cancellationToken: ct));
        var keep = new List<int>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < r.FieldCount; i++)
        {
            if (seen.Add(r.GetName(i))) keep.Add(i);
        }
        var names = keep.Select(r.GetName).ToArray();
        var types = keep.Select(r.GetFieldType).ToArray();
        var rows = new List<object?[]>();
        var buffer = new object[r.FieldCount];
        while (rows.Count < max && await r.ReadAsync(ct))
        {
            r.GetValues(buffer);
            rows.Add(keep.Select(i => buffer[i] is DBNull ? null : buffer[i]).ToArray());
        }
        return new SqlRows(names, types, rows);
    }
}
