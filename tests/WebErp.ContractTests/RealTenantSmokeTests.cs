using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using WebErp.Config;
using WebErp.Core.Metadata;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;
using WebErp.Erp.Security;
using WebErp.Html.Screens;

namespace WebErp.ContractTests;

/// <summary>
/// CHỈ CHẠY TRÊN MÁY DEV, khi đặt biến môi trường (CI không đặt nên bỏ qua):
/// <c>WEBERP_REAL_ROOT</c> (thư mục cấu hình dev, vd <c>D:\Data\Web-ERP-BE\.weberp</c>), <c>WEBERP_REAL_TENANT</c>, <c>WEBERP_REAL_USER</c>,
/// <c>WEBERP_REAL_FORMS</c> (danh sách form, cách nhau dấu phẩy), <c>WEBERP_REAL_OUT</c> (thư mục ghi HTML để xem).
/// Dựng màn từ DB ERP THẬT (chỉ đọc) rồi cho validator của FE kiểm: 0 lỗi, 0 cảnh báo. Không ghi gì vào DB.
/// </summary>
public sealed class RealTenantSmokeTests(FrontendValidator validator) : IClassFixture<FrontendValidator>
{
    /// <summary>
    /// Mô tả bảng/view thật (cột, kiểu, số dòng) và dòng SY_FrmCfg của các form, để soạn tệp mô tả form viết tay.
    /// Biến <c>WEBERP_REAL_DESCRIBE</c>: danh sách tên bảng/view hoặc <c>form:TênForm</c>, cách nhau dấu phẩy. Chỉ đọc catalog.
    /// </summary>
    /// <summary>
    /// CHỈ MÁY DEV. Biến <c>WEBERP_REAL_SQLFILE</c>: tệp các câu SELECT, mỗi câu mở đầu bằng dòng <c>-- tên</c>. Mỗi câu qua
    /// <c>SqlTemplate.EnsureReadOnly</c> và chạy trong giao dịch LUÔN rollback; ghi tối đa 300 dòng / câu vào <c>sql.txt</c>.
    /// Dùng để kiểm cấu trúc DB thật trước khi thiết kế phần ghi (M4). Không đưa kết quả vào repo.
    /// </summary>
    [Fact]
    public async Task Run_read_only_checks()
    {
        var root = Environment.GetEnvironmentVariable("WEBERP_REAL_ROOT");
        var file = Environment.GetEnvironmentVariable("WEBERP_REAL_SQLFILE");
        Assert.SkipWhen(string.IsNullOrEmpty(root) || string.IsNullOrEmpty(file), "Chỉ chạy trên máy dev (WEBERP_REAL_ROOT, WEBERP_REAL_SQLFILE).");
        var outDir = Environment.GetEnvironmentVariable("WEBERP_REAL_OUT") ?? Path.GetTempPath();
        var paths = new WebErpPaths(root!);
        var services = new ServiceCollection();
        services.AddWebErpDataProtection(paths, null);
        using var sp = services.BuildServiceProvider();
        var store = new SqliteTenantStore(new ConfigDatabase(paths), new SecretProtector(sp.GetRequiredService<IDataProtectionProvider>()), TimeProvider.System);
        var tenant = (await store.GetAsync(Environment.GetEnvironmentVariable("WEBERP_REAL_TENANT")!, CancellationToken.None))!;
        await using var c = await new ErpConnectionFactory(store, new ErpOptions()).OpenAsync(tenant, CancellationToken.None);
        var lines = new List<string>();
        var blocks = System.Text.RegularExpressions.Regex.Split(await File.ReadAllTextAsync(file!), @"^--\s*", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Where(b => b.Trim().Length > 0);
        foreach (var block in blocks)
        {
            var nl = block.IndexOf('\n');
            var name = (nl < 0 ? block : block[..nl]).Trim();
            var sql = nl < 0 ? "" : block[(nl + 1)..].Trim().TrimEnd(';');
            lines.Add($"== {name}");
            try
            {
                WebErp.Erp.Query.SqlTemplate.EnsureReadOnly(sql, name);
                await using var tx = (Microsoft.Data.SqlClient.SqlTransaction)await c.BeginTransactionAsync();
                try
                {
                    await using var r = await c.ExecuteReaderAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: 120));
                    var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToArray();
                    lines.Add("   " + string.Join(" | ", cols));
                    var n = 0;
                    while (await r.ReadAsync() && n++ < 300)
                    {
                        lines.Add("   " + string.Join(" | ", Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? "NULL"
                            : (r.GetValue(i) is byte[] b ? $"0x{Convert.ToHexString(b.Take(16).ToArray())}" : Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!)
                                .ReplaceLineEndings(" ") is var s && s.Length > 300 ? s[..300] + "…" : s)));
                    }
                }
                finally { await tx.RollbackAsync(); }
            }
            catch (Exception ex) { lines.Add("   LỖI: " + ex.Message); }
        }
        await File.WriteAllLinesAsync(Path.Combine(outDir, "sql.txt"), lines);
    }

    [Fact]
    public async Task Describe_real_tables_for_overlays()
    {
        var root = Environment.GetEnvironmentVariable("WEBERP_REAL_ROOT");
        var names = Environment.GetEnvironmentVariable("WEBERP_REAL_DESCRIBE");
        Assert.SkipWhen(string.IsNullOrEmpty(root) || string.IsNullOrEmpty(names), "Chỉ chạy trên máy dev (WEBERP_REAL_ROOT, WEBERP_REAL_DESCRIBE).");
        var outDir = Environment.GetEnvironmentVariable("WEBERP_REAL_OUT") ?? Path.GetTempPath();
        Directory.CreateDirectory(outDir);
        var paths = new WebErpPaths(root!);
        var services = new ServiceCollection();
        services.AddWebErpDataProtection(paths, null);
        using var sp = services.BuildServiceProvider();
        var store = new SqliteTenantStore(new ConfigDatabase(paths), new SecretProtector(sp.GetRequiredService<IDataProtectionProvider>()), TimeProvider.System);
        var tenant = (await store.GetAsync(Environment.GetEnvironmentVariable("WEBERP_REAL_TENANT")!, CancellationToken.None))!;
        await using var c = await new ErpConnectionFactory(store, new ErpOptions()).OpenAsync(tenant, CancellationToken.None);
        var lines = new List<string>();
        foreach (var n in names!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (n.StartsWith("fmt:", StringComparison.Ordinal))
            {
                var fields = n[4..].Split(';');
                var rows = await c.QueryAsync<(string, string?, string?, string?)>(
                    "SELECT FieldName, FormName, FormatID, CaptionVN FROM dbo.SY_FmtFldTbl WHERE FieldName IN @fields ORDER BY FieldName, FormName", new { fields });
                lines.Add($"== {n}:");
                lines.AddRange(rows.Select(r => $"   {r.Item1} [{r.Item2}] {r.Item3} = {r.Item4}"));
                continue;
            }
            if (n.StartsWith("cfgall:", StringComparison.Ordinal))
            {
                var rows = await c.QueryAsync<(string, string, string, string, string)>(
                    "SELECT FID, KeyID, COALESCE(SubID,''), LEFT(REPLACE(REPLACE(KeyValue, CHAR(13), ' '), CHAR(10), ' '), 160), COALESCE(LEFT(SubValue, 40),'') FROM dbo.SY_FrmCfg " +
                    "WHERE FID = @f AND COALESCE(KeyValue,'')<>'' AND KeyID NOT LIKE 'LY%' ORDER BY KeyID, SubID", new { f = n[7..] });
                lines.Add($"== {n}:");
                lines.AddRange(rows.Select(r => $"   {r.Item2}.{r.Item3} = {r.Item4} | {r.Item5}"));
                continue;
            }
            if (n.StartsWith("ctr:", StringComparison.Ordinal))
            {
                var ctrCols = string.Join(", ", await c.QueryAsync<string>("SELECT name FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SY_FrmCtrTbl') ORDER BY column_id"));
                lines.Add($"== SY_FrmCtrTbl cột: {ctrCols}");
                var rows = await c.QueryAsync("SELECT TOP 40 * FROM dbo.SY_FrmCtrTbl WHERE FormID = @f", new { f = n[4..] });
                foreach (IDictionary<string, object?> r in rows) lines.Add("   " + string.Join(" | ", r.Where(x => x.Value is not null and not DBNull).Select(x => $"{x.Key}={x.Value}")));
                continue;
            }
            if (n == "menu:")
            {
                if (!TenantNames.IsValidModule(tenant.ModuleId)) throw new InvalidOperationException("module");
                var rows = await c.QueryAsync($"SELECT * FROM dbo.{tenant.ModuleId.ToUpperInvariant()}_Menu WHERE COALESCE(FormName,'')<>'' ORDER BY MenuID");
                lines.Add("== menu:");
                foreach (IDictionary<string, object?> r in rows)
                    lines.Add($"   {r["MenuID"]} | {r["FormName"]} | {(r.TryGetValue("Para", out var pa) ? pa : null)} | {(r.TryGetValue("VN", out var vn) ? vn : null)}");
                continue;
            }
            if (n.StartsWith("flt:", StringComparison.Ordinal))
            {
                var rows = await c.QueryAsync("SELECT * FROM dbo.SY_FrmFltTbl WHERE FormID = @f ORDER BY KeyID", new { f = n[4..] });
                lines.Add($"== {n}:");
                foreach (IDictionary<string, object?> r in rows)
                    lines.Add("   " + string.Join(" | ", r.Where(x => x.Value is not null and not DBNull && x.Value.ToString() is { Length: > 0 } && x.Key != "UserAutoID")
                        .Select(x => $"{x.Key}={x.Value!.ToString()!.ReplaceLineEndings(" ")}")));
                continue;
            }
            if (n.StartsWith("group:", StringComparison.Ordinal))
            {
                // group:Bảng:Cột — đếm số dòng theo giá trị của một cột cờ (tên kiểm bằng sys.columns, không chép dữ liệu).
                var p = n[6..].Split(':');
                var ok = await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(@t) AND name = @c", new { t = p[0], c = p[1] });
                lines.Add($"== {n}:");
                if (ok == 1)
                    foreach (var (v, k) in await c.QueryAsync<(string?, int)>($"SELECT CAST([{p[1]}] AS nvarchar(50)), COUNT(*) FROM dbo.[{p[0]}] GROUP BY [{p[1]}]"))
                        lines.Add($"   {v ?? "NULL"} = {k}");
                continue;
            }
            if (n.StartsWith("perm:", StringComparison.Ordinal))
            {
                // perm:User:Form — dòng quyền nhóm / riêng của người dùng trên form (chỉ cờ quyền, không dữ liệu khác).
                var p = n[5..].Split(':');
                var m = tenant.ModuleId.ToUpperInvariant();
                var rows = await c.QueryAsync(
                    $"SELECT 'G' Src, U.UserGroupID, M.MenuID, M.isNotCheckPermission, P.IsRun, P.IsAdd, P.IsUpdate, P.IsDelete, P.isManager, P.isAdmin " +
                    $"FROM dbo.SY_User U JOIN dbo.{m}_UserGroupPermisstion P ON P.UserGroupID = U.UserGroupID JOIN dbo.{m}_Menu M ON M.MenuID = P.MenuID " +
                    $"WHERE U.UserName = @u AND M.FormName = @f UNION ALL " +
                    $"SELECT 'U', NULL, M.MenuID, M.isNotCheckPermission, P.IsRun, P.IsAdd, P.IsUpdate, P.IsDelete, P.isManager, P.isAdmin " +
                    $"FROM dbo.{m}_UserPermisstion P JOIN dbo.{m}_Menu M ON M.MenuID = P.MenuID WHERE P.UserName = @u AND M.FormName = @f", new { u = p[0], f = p[1] });
                lines.Add($"== {n}:");
                foreach (IDictionary<string, object?> r in rows) lines.Add("   " + string.Join(" | ", r.Select(x => $"{x.Key}={x.Value}")));
                continue;
            }
            if (n.StartsWith("drdwfull:", StringComparison.Ordinal))
            {
                var rows = await c.QueryAsync("SELECT * FROM dbo.SY_FrmDrdwTbl WHERE FormID = @f ORDER BY GridName, ColumnID", new { f = n[9..] });
                lines.Add($"== {n}:");
                foreach (IDictionary<string, object?> r in rows)
                    lines.Add("   " + string.Join(" | ", r.Where(x => x.Value is not null and not DBNull && x.Value.ToString() is { Length: > 0 } && x.Key != "UserAutoID")
                        .Select(x => $"{x.Key}={x.Value!.ToString()!.ReplaceLineEndings(" ")}")));
                continue;
            }
            if (n.StartsWith("drdw:", StringComparison.Ordinal))
            {
                var rows = await c.QueryAsync<(string, string, string, string, string)>(
                    "SELECT COALESCE(GridName,''), COALESCE(ColumnID,''), COALESCE(Caption,''), COALESCE(ValueColumn,''), COALESCE(DisplayColumn,'') FROM dbo.SY_FrmDrdwTbl " +
                    "WHERE FormID = @f ORDER BY GridName, ColumnID", new { f = n[5..] });
                lines.Add($"== {n}:");
                lines.AddRange(rows.Select(r => $"   [{r.Item1}] {r.Item2} caption='{r.Item3}' value={r.Item4} display={r.Item5}"));
                continue;
            }
            if (n.StartsWith("cfgfield:", StringComparison.Ordinal))
            {
                var parts = n[9..].Split(':');
                var rows = await c.QueryAsync<(string, string, string, string)>(
                    "SELECT KeyID, COALESCE(SubID,''), LEFT(COALESCE(KeyValue,''), 200), COALESCE(LEFT(SubValue, 60),'') FROM dbo.SY_FrmCfg " +
                    "WHERE FID = @f AND (SubID = @s OR SubID = 'Fld' + @s OR KeyValue LIKE '%' + @s + '%') ORDER BY KeyID, SubID", new { f = parts[0], s = parts[1] });
                lines.Add($"== {n}:");
                lines.AddRange(rows.Select(r => $"   {r.Item1}.{r.Item2} = {r.Item3} | {r.Item4}"));
                continue;
            }
            if (n.StartsWith("form:", StringComparison.Ordinal))
            {
                var rows = await c.QueryAsync<(string, string, string, string)>(
                    "SELECT FID, KeyID, COALESCE(SubID,''), LEFT(REPLACE(REPLACE(KeyValue, CHAR(13), ' '), CHAR(10), ' '), 200) FROM dbo.SY_FrmCfg " +
                    "WHERE FID IN (@f, @f + '_Bank', @f + '_Import', @f + '_IMPORT') AND COALESCE(KeyValue,'')<>'' AND KeyID NOT LIKE 'LY%' " +
                    "AND KeyID NOT IN ('EBC','EBS','EBE','EBD','ESS','ESL','ESC','DW2','DW3','FLG') ORDER BY FID, KeyID, SubID", new { f = n[5..] });
                lines.Add($"== {n}:");
                lines.AddRange(rows.Select(r => $"   {r.Item1} {r.Item2}.{r.Item3} = {r.Item4}"));
                continue;
            }
            var cols = await c.QueryAsync<(string, string, bool)>(
                "SELECT c.name, TYPE_NAME(c.user_type_id), c.is_nullable FROM sys.columns c WHERE c.object_id = OBJECT_ID(@t) ORDER BY c.column_id", new { t = n });
            var kind = await c.ExecuteScalarAsync<string>("SELECT type_desc FROM sys.objects WHERE object_id = OBJECT_ID(@t)", new { t = n });
            var count = await c.ExecuteScalarAsync<long?>("SELECT SUM(p.rows) FROM sys.partitions p WHERE p.object_id = OBJECT_ID(@t) AND p.index_id IN (0,1)", new { t = n });
            lines.Add($"== {n} [{kind ?? "KHÔNG CÓ"}] rows={count?.ToString() ?? "?"}: " + string.Join(", ", cols.Select(x => $"{x.Item1}:{x.Item2}")));
        }
        await File.WriteAllLinesAsync(Path.Combine(outDir, "describe.txt"), lines);
    }

    [Fact]
    public async Task Real_metadata_screens_pass_frontend_validator()
    {
        var root = Environment.GetEnvironmentVariable("WEBERP_REAL_ROOT");
        Assert.SkipWhen(string.IsNullOrEmpty(root), "Chỉ chạy trên máy dev (WEBERP_REAL_ROOT).");
        var tenantKey = Environment.GetEnvironmentVariable("WEBERP_REAL_TENANT")!;
        var user = Environment.GetEnvironmentVariable("WEBERP_REAL_USER")!;
        var forms = Environment.GetEnvironmentVariable("WEBERP_REAL_FORMS")!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var outDir = Environment.GetEnvironmentVariable("WEBERP_REAL_OUT") ?? Path.GetTempPath();
        Directory.CreateDirectory(outDir);

        var paths = new WebErpPaths(root!);
        var services = new ServiceCollection();
        services.AddWebErpDataProtection(paths, null);
        using var sp = services.BuildServiceProvider();
        var store = new SqliteTenantStore(new ConfigDatabase(paths), new SecretProtector(sp.GetRequiredService<IDataProtectionProvider>()), TimeProvider.System);
        var tenant = (await store.GetAsync(tenantKey, CancellationToken.None))!;
        var connections = new ErpConnectionFactory(store, new ErpOptions());
        var meta = new ErpMetadata(connections, TimeProvider.System);
        var screens = new ErpScreens(connections, meta, new ErpPermissions(connections), TimeProvider.System);
        var print = new WebErp.Erp.Print.ErpPrint(connections, meta, screens, new ErpPermissions(connections), new ErpOptions());
        var report = new List<string>();
        var failures = new List<string>();

        foreach (var spec in forms)
        {
            var parts = spec.Split('|');
            // WEBERP_REAL_QUERY: query string áp cho mọi danh sách, vd "FromDate=2020-01-01" để có dữ liệu.
            var query = (Environment.GetEnvironmentVariable("WEBERP_REAL_QUERY") ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x.Length > 1 ? Uri.UnescapeDataString(x[1]) : "");
            // WEBERP_REAL_BRANCH: chi nhánh của phiên (vd "MB") để thử lọc chi nhánh; mặc định không có.
            var req = new ScreenRequest(tenant, user, Environment.GetEnvironmentVariable("WEBERP_REAL_BRANCH") is { Length: > 0 } br ? br : null,
                parts[0], parts.Length > 1 ? parts[1] : null, query);
            var list = await screens.OpenListAsync(req, CancellationToken.None);
            report.Add($"{spec}: {list.Status} {list.Note} rows={list.Rows?.Count} total={list.Total}");
            if (list.Status != ScreenStatus.Ok) continue;
            var urls = new ScreenUrls($"/erp/screens/{req.FormName}", req.Para, k => $"/erp/screens/{req.FormName}/{Uri.EscapeDataString(k)}",
                (k, s) => $"/erp/screens/{req.FormName}/{Uri.EscapeDataString(k)}/tabs/{s}", $"/erp/screens/{req.FormName}",
                Page: n => $"/erp/screens/{req.FormName}?page={n}",
                Lookup: (f, p) => $"/erp/screens/{req.FormName}/lookups/{f}?q={{q}}" + string.Concat(p.Select((x, i) => $"&p{i}={Uri.EscapeDataString(x)}")));
            var html = MetaScreens.List(list, list.Form!.Caption, null, urls);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{req.RuntimeName}.html"), html);
            await AssertValid(html, $"{spec} danh sách");

            // Tab độc lập (IND) dưới lưới danh sách.
            foreach (var t in list.Tabs ?? [])
            {
                var det = await screens.OpenDetailAsync(req, "-", t.Id, CancellationToken.None);
                report.Add($"  tab {t.Id} {t.Title}: {det.Status} {det.Note} rows={det.Rows?.Count}");
                if (det.Status == ScreenStatus.Ok) await AssertValid(MetaScreens.Detail(det, t.Title), $"{spec} tab {t.Id}");
            }
            if (!list.CanOpenRecord || list.Keys is not { Count: > 0 }) continue;
            var key = list.Keys[0];
            var rec = await screens.OpenRecordAsync(req, key, CancellationToken.None);
            report.Add($"  record {key}: {rec.Status} {rec.Note} tabs={rec.Tabs?.Count} details={rec.Details?.Count}");
            if (rec.Status != ScreenStatus.Ok) continue;
            var recHtml = MetaScreens.Record(rec, list.Form.Caption, null, urls, "__RequestVerificationToken", "test");
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{req.RuntimeName}.record.html"), recHtml);
            await AssertValid(recHtml, $"{spec} chứng từ");
            // In theo mẫu (M6): báo cáo của form, SP <Report>Stp với chứng từ này, tệp mẫu.
            if (await print.ReportOfAsync(req, CancellationToken.None) is { } rpt)
            {
                var pv = await print.PrepareAsync(req, [key], null, new Dictionary<string, string>(), CancellationToken.None);
                report.Add($"  print {rpt}: {pv.Status} {pv.Note} template={pv.Template} sets={pv.Sets?.Count} rows={string.Join("/", (pv.Sets ?? []).Select(s => s.Rows.Count))} "
                    + $"params={string.Join(",", (pv.Params ?? []).Select(p => p.Name))} msgs={string.Join(" | ", (pv.Messages ?? []).Select(m => m.Text))}");
                if (pv.Status == ScreenStatus.Ok)
                {
                    var prnHtml = PrintScreens.Print(pv, req.FormName, list.Form.Caption, null,
                        new PrintUrls($"/erp/screens/{req.FormName}/print", req.Para, t => $"/real/{t}.rpx"));
                    await File.WriteAllTextAsync(Path.Combine(outDir, $"{req.RuntimeName}.print.html"), prnHtml);
                    await AssertValid(prnHtml, $"{spec} in");
                    if (await print.TemplateAsync(req, pv.Template, CancellationToken.None) is { } tf)
                    {
                        await File.WriteAllBytesAsync(Path.Combine(outDir, tf.FileName), tf.Content);
                        report.Add($"    mẫu {tf.FileName}: {tf.Source} {tf.FileDate:yyyy-MM-dd} {tf.Content.Length} B");
                    }
                }
            }
            // Tra cứu F4 của vài ô có danh sách chọn: rỗng và có chữ gõ.
            foreach (var lf in (rec.Tabs ?? []).SelectMany(t => t.Fields).Where(x => x.Control == "lookup").Take(6))
            {
                foreach (var text in new[] { "", "a" })
                {
                    var lk = await screens.LookupAsync(req, lf.Field, text, lf.LookupParas ?? [], CancellationToken.None);
                    report.Add($"    F4 {lf.Field} '{text}': {lk.Status} {lk.Note} rows={lk.Rows?.Count} total={lk.Total}");
                    if (lk.Status == ScreenStatus.Ok) await AssertValid(MetaScreens.Lookup(lk, lf.Caption), $"{spec} F4 {lf.Field}");
                }
            }
            foreach (var d in rec.Details ?? [])
            {
                var det = await screens.OpenDetailAsync(req, key, d.Id, CancellationToken.None);
                report.Add($"    {d.Id} {d.Title}: {det.Status} {det.Note} rows={det.Rows?.Count}");
                if (det.Status != ScreenStatus.Ok) continue;
                var detHtml = MetaScreens.Detail(det, d.Title);
                await File.WriteAllTextAsync(Path.Combine(outDir, $"{req.RuntimeName}.{d.Id}.html"), detHtml);
                await AssertValid(detHtml, $"{spec} {d.Id}");
            }
        }
        await File.WriteAllLinesAsync(Path.Combine(outDir, "report.txt"), report.Concat(["", "== Vi phạm ERP-HTML"]).Concat(failures));
        Assert.True(failures.Count == 0, string.Join("\n", failures));

        async Task AssertValid(string html, string what)
        {
            var issues = await validator.ValidateAsync(html);
            failures.AddRange(issues.Select(i => $"{what}: [{i.Level}][{i.Rule}] {i.Msg} — {i.Where}"));
        }
    }
}
