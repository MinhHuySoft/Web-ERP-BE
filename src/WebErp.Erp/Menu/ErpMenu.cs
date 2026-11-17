using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebErp.Core.Errors;
using WebErp.Core.Menu;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;

namespace WebErp.Erp.Menu;

/// <summary>
/// Menu được phép thấy, đọc qua hàm <c>&lt;Module&gt;_GetUserMenuFnc(@Username)</c> giống WinForms (tài liệu 12 §4.2, §5.2).
/// <para>
/// Hàm trả các cột của <c>&lt;Module&gt;_Menu</c>. Danh sách cột trả về <b>chưa xác minh</b> trên mọi khách, nên đọc mềm theo
/// tên. Bắt buộc có <c>MenuID</c>. Không có <c>VN</c>/<c>FormName</c>/<c>Parent</c> thì để trống.
/// </para>
/// <para>
/// Khác WinForms: hàm lỗi thì báo lỗi, KHÔNG đọc cả bảng menu không lọc quyền (tránh lộ menu không được phép).
/// </para>
/// <para>
/// Timeout riêng <see cref="ErpOptions.MenuCommandTimeoutSeconds"/>. Mỗi lần đọc ghi log thời gian (mở kết nối, chạy hàm) theo công
/// ty; quá chậm thì cảnh báo. Hết thời gian chờ → <see cref="ErpTimeoutException"/> ("phản hồi chậm"), không phải "mất kết nối".
/// </para>
/// </summary>
public sealed class ErpMenu(ErpConnectionFactory connections, ILogger<ErpMenu>? log = null) : IErpMenu
{
    private readonly ILogger _log = log ?? NullLogger<ErpMenu>.Instance;

    public async Task<IReadOnlyList<MenuEntry>> GetUserMenuAsync(Tenant tenant, string userName, string language, CancellationToken ct)
    {
        if (!TenantNames.IsValidModule(tenant.ModuleId)) throw new InvalidOperationException($"Mã module '{tenant.ModuleId}' không hợp lệ.");
        // Tên hàm ghép từ mã module đã kiểm (chỉ chữ cái), không từ dữ liệu người dùng.
        var sql = $"SELECT * FROM dbo.{tenant.ModuleId.ToUpperInvariant()}_GetUserMenuFnc(@u) ORDER BY MenuID";
        var timeout = connections.Options.MenuCommandTimeoutSeconds;
        var watch = Stopwatch.StartNew();
        await using var c = await connections.OpenAsync(tenant, ct).ConfigureAwait(false);
        var openMs = watch.ElapsedMilliseconds;
        try
        {
            var rows = await c.QueryAsync(new CommandDefinition(sql,
                new { u = new DbString { Value = userName, IsAnsi = true, Length = 100 } },
                commandTimeout: timeout, cancellationToken: ct)).ConfigureAwait(false);
            var list = rows.Select(r => Map((IDictionary<string, object?>)r, language)).OfType<MenuEntry>().ToList();
            var totalMs = watch.ElapsedMilliseconds;
            _log.Log(totalMs >= connections.Options.MenuSlowLogMs ? LogLevel.Warning : LogLevel.Information,
                "Menu công ty {Tenant}: {Rows} dòng, {TotalMs} ms (mở kết nối {OpenMs} ms, hàm {Module}_GetUserMenuFnc {QueryMs} ms).",
                tenant.Key, list.Count, totalMs, openMs, tenant.ModuleId.ToUpperInvariant(), totalMs - openMs);
            return list;
        }
        catch (SqlException ex) when (ErpConnectionFactory.IsConnectivity(ex))
        {
            _log.LogWarning("Menu công ty {Tenant} lỗi sau {TotalMs} ms (mở kết nối {OpenMs} ms, timeout {Timeout} giây): SQL {Number}.",
                tenant.Key, watch.ElapsedMilliseconds, openMs, timeout, ex.Number);
            throw ErpConnectionFactory.Translate(tenant.Key, ex, "đọc menu", timeout);
        }
    }

    internal static MenuEntry? Map(IDictionary<string, object?> raw, string language)
    {
        var row = new Dictionary<string, object?>(raw, StringComparer.OrdinalIgnoreCase);
        var id = Str(row, "MenuID");
        if (string.IsNullOrWhiteSpace(id)) return null;

        var vn = Str(row, "VN") ?? id;
        var caption = language switch
        {
            "en" => Str(row, "EN") ?? vn,
            "zh" => Str(row, "CH") ?? vn + "(*)", // WinForms: thiếu CH thì dùng VN + "(*)" (tài liệu 12 §4.2)
            _ => vn,
        };
        return new MenuEntry(id.Trim(), Str(row, "Parent")?.Trim(), caption.Trim(), Str(row, "FormName")?.Trim(), Str(row, "Para")?.Trim(),
            Bit(row, "isBeginGroup"), Bit(row, "isBold"));
    }

    private static string? Str(Dictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is not null and not DBNull && v.ToString() is { Length: > 0 } s ? s : null;

    private static bool Bit(Dictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v switch { bool b => b, byte n => n != 0, int n => n != 0, long n => n != 0, _ => false };
}
