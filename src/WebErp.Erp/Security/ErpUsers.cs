using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Auth;
using WebErp.Core.Errors;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;

namespace WebErp.Erp.Security;

/// <summary>
/// Đọc <c>dbo.SY_User</c> (dùng chung, không đổi tiền tố theo module, tài liệu 12 §1.6). Cột đã kiểm trên schema MedData
/// (tài liệu 12 §5.1): <c>UserName</c> varchar(100) PK, <c>HoTen</c>, <c>UserGroupID</c>, <c>BranchID</c>, <c>Password</c>,
/// <c>Disable</c>. Chỉ đọc.
/// </summary>
public sealed class ErpUsers(ErpConnectionFactory connections) : IErpUsers
{
    private const string FindSql =
        "SELECT UserName,HoTen,UserGroupID,BranchID,[Password] FROM dbo.SY_User WHERE UserName=@u AND Disable=0";

    private const string EnabledSql = "SELECT COUNT(1) FROM dbo.SY_User WHERE UserName=@u AND Disable=0";

    public async Task<ErpLoginRecord?> FindForLoginAsync(Tenant tenant, string userName, CancellationToken ct)
    {
        if (!IsPlausibleUserName(userName)) return null;
        var row = await Run(tenant, c => c.QuerySingleOrDefaultAsync<UserRow>(Cmd(FindSql, userName, ct))).ConfigureAwait(false);
        return row is null ? null
            : new ErpLoginRecord(new ErpUser(row.UserName, row.HoTen ?? row.UserName, row.UserGroupID, row.BranchID), row.Password);
    }

    public async Task<bool> IsEnabledAsync(Tenant tenant, string userName, CancellationToken ct)
    {
        if (!IsPlausibleUserName(userName)) return false;
        return await Run(tenant, c => c.ExecuteScalarAsync<int>(Cmd(EnabledSql, userName, ct))).ConfigureAwait(false) > 0;
    }

    private CommandDefinition Cmd(string sql, string userName, CancellationToken ct) =>
        // AnsiString(100): khớp varchar(100), tránh SQL Server đổi kiểu cả cột khi so sánh.
        new(sql, new { u = new DbString { Value = userName, IsAnsi = true, Length = 100 } },
            commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct);

    private async Task<T> Run<T>(Tenant tenant, Func<SqlConnection, Task<T>> query)
    {
        await using var c = await connections.OpenAsync(tenant, CancellationToken.None).ConfigureAwait(false);
        try
        {
            return await query(c).ConfigureAwait(false);
        }
        catch (SqlException ex) when (ErpConnectionFactory.IsConnectivity(ex))
        {
            throw new ErpUnavailableException(tenant.Key, ex);
        }
    }

    private static bool IsPlausibleUserName(string userName) => userName.Length is > 0 and <= 100;

    private sealed class UserRow
    {
        public string UserName { get; set; } = "";
        public string? HoTen { get; set; }
        public string? UserGroupID { get; set; }
        public string? BranchID { get; set; }
        public string? Password { get; set; }
    }
}
