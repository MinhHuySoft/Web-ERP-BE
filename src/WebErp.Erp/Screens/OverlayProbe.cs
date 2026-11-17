using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Metadata;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Metadata;
using WebErp.Erp.Query;

namespace WebErp.Erp.Screens;

/// <summary>Kết quả kiểm một slot (T0 bảng chính, T1… tab) của tệp mô tả trên DB một công ty.</summary>
/// <param name="Columns">Tên cột câu truy vấn trả về (chạy <c>TOP (0)</c>: không đọc dòng dữ liệu nào).</param>
/// <param name="Dropped">Cột hiển thị bị bỏ vì DB không có (web vẫn mở được).</param>
/// <param name="Error">Lỗi làm slot không chạy được (thiếu bảng / cột lọc, SQL ghi, SQL sai); null nếu chạy được.</param>
public sealed record OverlaySlotCheck(string Slot, string Source, IReadOnlyList<string> Columns, IReadOnlyList<string> Dropped, string? Error);

/// <summary>Kết quả nút "Kiểm" của trang quản trị.</summary>
/// <param name="Error">Lỗi chung (JSON sai, form không dựng được); khi có thì <see cref="Slots"/> rỗng.</param>
public sealed record OverlayCheck(string? Form, string? Caption, string? FormType, bool UsesDbConfig, IReadOnlyList<OverlaySlotCheck> Slots, string? Error)
{
    public bool Ok => Error is null && Slots.All(s => s.Error is null);
}

/// <summary>Một form trong menu công ty mà web chưa dựng được.</summary>
/// <param name="Reason"><c>handcoded</c>: form viết tay chưa có mô tả; <c>noconfig</c>: không có <c>SY_FrmLstTbl</c> / <c>SY_FrmCfg</c> / mô tả.</param>
public sealed record MenuGap(string Form, string? Caption, string Reason);

/// <summary>
/// Kiểm tệp mô tả form CHƯA lưu trên DB của một công ty (trang quản trị), và liệt kê form trong menu công ty web chưa dựng được.
/// Chỉ đọc: câu dựng từ mô tả chạy <c>TOP (0)</c> (chỉ lấy tên cột) trong giao dịch luôn rollback, qua cùng lớp kiểm chỉ đọc với Host.
/// </summary>
public sealed class OverlayProbe(ErpConnectionFactory connections, ErpMetadata metadata)
{
    public async Task<OverlayCheck> CheckAsync(Tenant tenant, string json, string? para, CancellationToken ct)
    {
        IReadOnlyList<FormOverlays.Overlay> parsed;
        try
        {
            parsed = FormOverlays.ParseText(json, "Mô tả");
        }
        catch (InvalidDataException ex)
        {
            return new OverlayCheck(null, null, null, false, [], ex.Message);
        }
        var overlay = parsed[0];
        var resolved = overlay.Resolve(para);
        var runtimeName = string.IsNullOrEmpty(para) ? overlay.Form : $"{overlay.Form}_{para}";
        await using var c = await connections.OpenAsync(tenant, ct);
        var form = await metadata.BuildFormAsync(c, tenant, overlay.Form, runtimeName, resolved, handCoded: true, ct);
        if (form is null) return new OverlayCheck(overlay.Form, overlay.Caption, overlay.FormType, overlay.UseDbConfig, [], "Không dựng được form từ mô tả.");

        var checks = new List<OverlaySlotCheck>();
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            foreach (var slot in form.Slots.Values.OrderBy(s => s.Id, StringComparer.OrdinalIgnoreCase))
                checks.Add(await CheckSlotAsync(tenant, slot, c, tx, ct));
        }
        finally
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { } catch (SqlException) { }
        }
        return new OverlayCheck(form.FormName, form.Caption, form.FormType, overlay.UseDbConfig, checks,
            checks.Count == 0 && !form.IsDialog ? "Mô tả không có slot bảng nào (T0, T1…)." : null);
    }

    private async Task<OverlaySlotCheck> CheckSlotAsync(Tenant tenant, SlotMeta slot, SqlConnection c, SqlTransaction tx, CancellationToken ct)
    {
        var r = await OverlaySchema.ResolveAsync(slot, t => metadata.GetColumnsAsync(tenant, t, ct));
        if (r.Missing is { } missing) return new OverlaySlotCheck(slot.Id, slot.Source, [], [], missing);
        try
        {
            // Mọi token ({User}, {BranchID}…) thành tham số NULL: chỉ cần câu lệnh biên dịch được, TOP (0) không trả dòng nào.
            var q = new SlotQuery(r.Slot!, _ => (true, null));
            // Điều kiện lọc của mô tả cũng phải biên dịch được (màn thật luôn áp các điều kiện này).
            foreach (var (text, key) in new[] { (r.Slot!.Filter, "FTR"), (r.Slot.FilterEx, "FTX"), (r.Slot.FilterWeb, "FTW") })
                if (!string.IsNullOrWhiteSpace(text)) q.Where.Add(q.Fragment(text, $"{slot.Id}.{key}"));
            var sql = q.ListSql(0);
            await using var reader = await c.ExecuteReaderAsync(new CommandDefinition(sql, q.Parameters, tx, connections.Options.CommandTimeoutSeconds, cancellationToken: ct));
            var cols = new List<string>();
            for (var i = 0; i < reader.FieldCount; i++) cols.Add(reader.GetName(i));
            return new OverlaySlotCheck(slot.Id, slot.Source, cols, r.Dropped, null);
        }
        catch (UnsafeMetadataSqlException ex)
        {
            return new OverlaySlotCheck(slot.Id, slot.Source, [], r.Dropped, ex.Message);
        }
        catch (SqlException ex) when (!ErpConnectionFactory.IsConnectivity(ex))
        {
            return new OverlaySlotCheck(slot.Id, slot.Source, [], r.Dropped, $"SQL lỗi: {SqlErrorText.Describe(ex)}");
        }
    }

    /// <summary>
    /// Form trong menu công ty (<c>{Mod}_Menu.FormName</c>) mà web chưa dựng được: viết tay chưa có mô tả, hoặc không có cấu hình nào.
    /// Chỉ đọc bảng menu và bảng cấu hình form, không đọc dữ liệu nghiệp vụ.
    /// </summary>
    public async Task<IReadOnlyList<MenuGap>> MenuGapsAsync(Tenant tenant, CancellationToken ct)
    {
        if (!TenantNames.IsValidModule(tenant.ModuleId)) return [];
        var set = await metadata.GetOverlaysAsync(tenant, ct);
        var menu = $"dbo.{tenant.ModuleId.ToUpperInvariant()}_Menu";
        await using var c = await connections.OpenAsync(tenant, ct);
        if (await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT CASE WHEN OBJECT_ID(@t) IS NULL THEN 0 ELSE 1 END", new { t = menu }, cancellationToken: ct)) == 0)
            return [];
        var rows = await c.QueryAsync<(string Form, string? Caption, int HasLst, int HasCfg)>(new CommandDefinition(
            // Gom menu theo form trước (SQL Server không cho hàm gộp bọc truy vấn con), rồi mới kiểm có cấu hình form không.
            "SELECT G.FormName, G.VN, " +
            "CASE WHEN EXISTS (SELECT 1 FROM dbo.SY_FrmLstTbl L WHERE L.FormID = G.FormName) THEN 1 ELSE 0 END, " +
            "CASE WHEN EXISTS (SELECT 1 FROM dbo.SY_FrmCfg C WHERE C.FID = G.FormName) THEN 1 ELSE 0 END " +
            $"FROM (SELECT M.FormName, MAX(M.VN) AS VN FROM {menu} M WHERE COALESCE(M.FormName, '') <> '' GROUP BY M.FormName) G ORDER BY G.FormName",
            commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct));
        var gaps = new List<MenuGap>();
        foreach (var (form, caption, hasLst, hasCfg) in rows)
        {
            var name = form.Trim();
            if (name.Length == 0 || set.Has(name)) continue;
            if (set.IsHandCoded(name)) gaps.Add(new MenuGap(name, caption?.Trim(), "handcoded"));
            else if (hasLst == 0 && hasCfg == 0) gaps.Add(new MenuGap(name, caption?.Trim(), "noconfig"));
        }
        return gaps;
    }
}
