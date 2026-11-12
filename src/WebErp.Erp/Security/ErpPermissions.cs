using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Errors;
using WebErp.Core.Metadata;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;

namespace WebErp.Erp.Security;

/// <summary>
/// Quyền của người dùng trên một form, như <c>Functions.GetPermission</c> (tài liệu 12 §5.3): hợp quyền nhóm và quyền riêng
/// (bảng theo module: <c>{Module}_UserGroupPermisstion</c>, <c>{Module}_UserPermisstion</c>, <c>{Module}_Menu</c>), lấy dòng đầu
/// theo MenuID. Khác WinForms: không có dòng nào thì KHÔNG cấp toàn quyền (fail-closed), chỉ xem như <see cref="FormPermission.ViewOnly"/>.
/// Đọc mỗi lần mở màn (QĐ-07: quyền không nằm trong cookie). Bảng <c>SY_UserAuthority</c> (ủy quyền) MedData không có nên chưa xét.
/// </summary>
public sealed class ErpPermissions(ErpConnectionFactory connections)
{
    public async Task<FormPermission> GetAsync(Tenant tenant, string userName, string runtimeName, CancellationToken ct)
    {
        var m = tenant.ModuleId.ToUpperInvariant(); // đã kiểm ^[A-Za-z]{2,5}$ khi lưu cấu hình
        var sql = $"""
            SELECT TOP 1 MAX(CAST(IsRun AS int)) IsRun, MAX(CAST(IsAdd AS int)) IsAdd, MAX(CAST(IsUpdate AS int)) IsUpdate,
                   MAX(CAST(IsDelete AS int)) IsDelete, MAX(CAST(isManager AS int)) isManager, MAX(CAST(isAdmin AS int)) isAdmin,
                   MAX(CAST(isHideAmount AS int)) isHideAmount, MAX(CAST(isExportExcel AS int)) isExportExcel
            FROM (
              SELECT M.MenuID, P.IsRun, P.IsAdd, P.IsUpdate, P.IsDelete, P.isManager, P.isAdmin, P.isHideAmount, P.isExportExcel
              FROM dbo.SY_User U JOIN dbo.{m}_UserGroupPermisstion P ON P.UserGroupID=U.UserGroupID JOIN dbo.{m}_Menu M ON M.MenuID=P.MenuID
              WHERE U.UserName=@u AND M.isDisable=0 AND M.FormName + CASE WHEN COALESCE(M.Para,'')<>'' THEN '_'+M.Para ELSE '' END=@f
              UNION ALL
              SELECT M.MenuID, P.IsRun, P.IsAdd, P.IsUpdate, P.IsDelete, P.isManager, P.isAdmin, P.isHideAmount, P.isExportExcel
              FROM dbo.{m}_UserPermisstion P JOIN dbo.{m}_Menu M ON M.MenuID=P.MenuID
              WHERE P.UserName=@u AND M.isDisable=0 AND M.FormName + CASE WHEN COALESCE(M.Para,'')<>'' THEN '_'+M.Para ELSE '' END=@f
            ) X GROUP BY MenuID ORDER BY MenuID
            """;
        try
        {
            await using var c = await connections.OpenAsync(tenant, ct);
            var r = await c.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql,
                new
                {
                    u = new DbString { Value = userName, IsAnsi = true, Length = 100 },
                    f = new DbString { Value = runtimeName, IsAnsi = true, Length = 250 },
                }, commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct));
            if (r is null) return FormPermission.ViewOnly;
            var admin = r.IsAdmin == 1;
            return new FormPermission(true, r.IsRun == 1, r.IsAdd == 1, r.IsUpdate == 1, r.IsDelete == 1, admin || r.IsManager == 1, admin,
                r.IsHideAmount == 1, r.IsExportExcel == 1);
        }
        catch (SqlException ex) when (ErpConnectionFactory.IsConnectivity(ex))
        {
            throw new ErpUnavailableException(tenant.Key, ex);
        }
    }

    private sealed class Row
    {
        public int? IsRun { get; set; }
        public int? IsAdd { get; set; }
        public int? IsUpdate { get; set; }
        public int? IsDelete { get; set; }
        public int? IsManager { get; set; }
        public int? IsAdmin { get; set; }
        public int? IsHideAmount { get; set; }
        public int? IsExportExcel { get; set; }
    }
}
