using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Errors;
using WebErp.Core.Metadata;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;

namespace WebErp.Erp.Metadata;

/// <summary>Định dạng một cột (<c>SY_FmtFldTbl</c> ⋈ <c>SY_FmatTbl</c>).</summary>
/// <param name="Kind"><c>N</c> số, <c>S</c> chuỗi/ngày (cột <c>SY_FmatTbl.Type</c>).</param>
public sealed record FieldFormat(string? Caption, string? FormatId, int? Decimals, string? Kind);

/// <summary>Bảng tiêu đề + định dạng của cả công ty (khoảng vài nghìn dòng, đọc một lần rồi cache).</summary>
public sealed class FieldFormats(IReadOnlyDictionary<string, FieldFormat> global, IReadOnlyDictionary<string, FieldFormat> perForm)
{
    public static readonly FieldFormats Empty = new(new Dictionary<string, FieldFormat>(), new Dictionary<string, FieldFormat>());

    /// <summary>Theo form trước (khớp tên form chạy hoặc tên form gốc), rồi toàn cục.</summary>
    public FieldFormat? Get(string field, string runtimeName, string formName) =>
        perForm.GetValueOrDefault($"{runtimeName}|{field}") ?? perForm.GetValueOrDefault($"{formName}|{field}") ?? global.GetValueOrDefault(field);
}

/// <summary>
/// Đọc metadata form từ DB ERP của công ty, cache theo công ty + tên form (5 phút, QĐ-01: không đổi schema, đọc lúc chạy).
/// Chỉ SELECT. Không đọc/chạy hook (ESL, EBS…) và nút lệnh.
/// </summary>
public sealed class ErpMetadata(ErpConnectionFactory connections, TimeProvider time)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, object Value)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<FormMeta?> GetFormAsync(Tenant tenant, string formName, string runtimeName, CancellationToken ct) =>
        await Cached($"form:{tenant.Key}:{runtimeName}", async () =>
        {
            await using var c = await Open(tenant, ct);
            var lst = await c.QuerySingleOrDefaultAsync<FrmLstRow>(Cmd(
                "SELECT TOP 1 FormID,FormType,CaptionVN,TableName,PrimaryKey,FilterEx,HideColumnArr,SummaryColumnArr,TableDetailLeftJoinField,TableDetailSelectFrom " +
                "FROM dbo.SY_FrmLstTbl WHERE FormID=@f", formName, ct));
            var cfg = (await c.QueryAsync<FrmCfgRow>(Cmd(
                "SELECT KeyID,SubID,KeyValue,SubValue FROM dbo.SY_FrmCfg WHERE FID=@f AND COALESCE(KeyValue,'')<>'' ORDER BY UserAutoID", runtimeName, ct))).ToList();
            // Form viết tay có tệp mô tả: dùng dòng SY_FrmLstTbl của tệp (lớp viết tay luôn che dòng cùng tên trong DB). Cấu hình
            // SY_FrmCfg của DB chỉ giữ khi form viết tay có đọc nó (useDbConfig), và đứng trước để cấu hình của khách thắng.
            var para = runtimeName.Length > formName.Length + 1 ? runtimeName[(formName.Length + 1)..] : null;
            if (FormOverlays.Get(formName, para) is { } overlay)
            {
                lst = overlay.Lst;
                if (!overlay.UseDbConfig) cfg.Clear();
                cfg.AddRange(overlay.Rows);
            }
            if (lst is null && cfg.Count == 0) return (FormMeta?)null;
            var flt = (await c.QueryAsync<FrmFltRow>(Cmd(
                "SELECT KeyID,ColumnID,Caption,Type,Source,ValueColumn,DisplayColumn,Operator,WhereString,DefaultValue,IsDisable " +
                "FROM dbo.SY_FrmFltTbl WHERE FormID=@f ORDER BY KeyID,UserAutoID", runtimeName, ct))).ToList();
            return MetadataCompiler.Compile(formName, runtimeName, lst, cfg, flt);
        });

    public async Task<FieldFormats> GetFormatsAsync(Tenant tenant, CancellationToken ct) =>
        await Cached($"formats:{tenant.Key}", async () =>
        {
            await using var c = await Open(tenant, ct);
            var rows = await c.QueryAsync<(string FieldName, string? FormName, string? CaptionVN, string? FormatID, int? NumberDecimal, string? Type)>(
                new CommandDefinition("SELECT A.FieldName,A.FormName,A.CaptionVN,A.FormatID,B.NumberDecimal,B.Type FROM dbo.SY_FmtFldTbl A " +
                                      "LEFT JOIN dbo.SY_FmatTbl B ON B.FormatID=A.FormatID WHERE A.FieldName IS NOT NULL",
                    commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct));
            var global = new Dictionary<string, FieldFormat>(StringComparer.OrdinalIgnoreCase);
            var perForm = new Dictionary<string, FieldFormat>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in rows)
            {
                var f = new FieldFormat(Blank(r.CaptionVN), Blank(r.FormatID), r.NumberDecimal, Blank(r.Type));
                if (string.IsNullOrWhiteSpace(r.FormName)) global.TryAdd(r.FieldName.Trim(), f);
                else perForm.TryAdd($"{r.FormName.Trim()}|{r.FieldName.Trim()}", f);
            }
            return new FieldFormats(global, perForm);
        });

    /// <summary>Cột của bảng/view (để biết có <c>UserCreate</c>, <c>BranchID</c>, cột ngày… không). Rỗng nếu không phải tên bảng/view.</summary>
    public async Task<IReadOnlySet<string>> GetColumnsAsync(Tenant tenant, string tableOrView, CancellationToken ct) =>
        await Cached($"cols:{tenant.Key}:{tableOrView}", async () =>
        {
            await using var c = await Open(tenant, ct);
            var names = await c.QueryAsync<string>(new CommandDefinition("SELECT name FROM sys.columns WHERE object_id=OBJECT_ID(@t)",
                new { t = new DbString { Value = tableOrView, IsAnsi = false, Length = 300 } },
                commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct));
            return (IReadOnlySet<string>)new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        });

    /// <summary>
    /// Giá trị token phiên (tài liệu 10 §7.2): cột của <c>SY_User</c> (EmployeeID, CostCenterID…) và tham số <c>{Module}_Setup</c>.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, object?>> GetUserTokensAsync(Tenant tenant, string userName, CancellationToken ct) =>
        await Cached($"tokens:{tenant.Key}:{userName}", async () =>
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            await using var c = await Open(tenant, ct);
            var setupTable = $"dbo.{tenant.ModuleId.ToUpperInvariant()}_Setup";
            if (await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT CASE WHEN OBJECT_ID(@t) IS NULL THEN 0 ELSE 1 END",
                    new { t = setupTable }, cancellationToken: ct)) == 1)
            {
                var hasCols = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                    "SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID(@t) AND name IN ('CodeID','CodeValue')", new { t = setupTable }, cancellationToken: ct));
                if (hasCols == 2)
                {
                    foreach (var (code, value) in await c.QueryAsync<(string, string?)>(new CommandDefinition(
                                 $"SELECT CodeID, CAST(CodeValue AS nvarchar(4000)) FROM {setupTable} WHERE CodeID IS NOT NULL", cancellationToken: ct)))
                    {
                        d.TryAdd(code.Trim(), value);
                        d.TryAdd("Sys" + code.Trim(), value);
                    }
                }
            }
            var row = await c.QuerySingleOrDefaultAsync(new CommandDefinition("SELECT * FROM dbo.SY_User WHERE UserName=@u",
                new { u = new DbString { Value = userName, IsAnsi = true, Length = 100 } }, cancellationToken: ct));
            if (row is IDictionary<string, object?> u)
            {
                foreach (var (k, v) in u)
                {
                    if (k.Equals("Password", StringComparison.OrdinalIgnoreCase)) continue;
                    d[k] = v is DBNull ? null : v;
                }
            }
            return (IReadOnlyDictionary<string, object?>)d;
        });

    private async Task<T> Cached<T>(string key, Func<Task<T>> load)
    {
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(key, out var hit) && now - hit.At < Ttl) return (T)hit.Value!;
        try
        {
            var value = await load();
            if (value is not null) _cache[key] = (now, value);
            return value;
        }
        catch (SqlException ex) when (ErpConnectionFactory.IsConnectivity(ex))
        {
            throw new ErpUnavailableException(key.Split(':')[1], ex);
        }
    }

    private Task<SqlConnection> Open(Tenant tenant, CancellationToken ct) => connections.OpenAsync(tenant, ct);

    private CommandDefinition Cmd(string sql, string form, CancellationToken ct) =>
        new(sql, new { f = new DbString { Value = form, IsAnsi = true, Length = 250 } }, commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
