using System.Diagnostics;
using Dapper;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;

namespace WebErp.Erp.Diagnostics;

/// <param name="RoundTripMs">Thời gian mở kết nối + một câu lệnh. Đo trước khi đưa khách vào dùng (độ trễ qua Internet).</param>
public sealed record ProbeResult(bool Ok, long RoundTripMs, string? Database, string? ServerVersion, bool HasUserTable, bool HasMenuFunction, string? Error);

/// <summary>Thử kết nối DB của một công ty. Chỉ đọc thông tin hệ thống, không đụng dữ liệu nghiệp vụ.</summary>
public sealed class ErpProbe(ErpConnectionFactory connections)
{
    public async Task<ProbeResult> ProbeAsync(Tenant tenant, CancellationToken ct)
    {
        if (!TenantNames.IsValidModule(tenant.ModuleId)) return new ProbeResult(false, 0, null, null, false, false, "Mã module không hợp lệ.");
        var sw = Stopwatch.StartNew();
        try
        {
            await using var c = await connections.OpenAsync(tenant, ct).ConfigureAwait(false);
            var fn = $"dbo.{tenant.ModuleId.ToUpperInvariant()}_GetUserMenuFnc";
            var r = await c.QuerySingleAsync<(string Db, string Ver, int HasUser, int HasFn)>(new CommandDefinition(
                "SELECT DB_NAME(),CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)),CASE WHEN OBJECT_ID(N'dbo.SY_User') IS NULL THEN 0 ELSE 1 END," +
                "CASE WHEN OBJECT_ID(@fn) IS NULL THEN 0 ELSE 1 END", new { fn }, cancellationToken: ct)).ConfigureAwait(false);
            return new ProbeResult(true, sw.ElapsedMilliseconds, r.Db, r.Ver, r.HasUser == 1, r.HasFn == 1, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ProbeResult(false, sw.ElapsedMilliseconds, null, null, false, false, (ex.InnerException ?? ex).Message);
        }
    }
}
