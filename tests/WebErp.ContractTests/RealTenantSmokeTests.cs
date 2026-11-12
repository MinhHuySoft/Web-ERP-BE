using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using WebErp.Config;
using WebErp.Core.Metadata;
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
        var screens = new ErpScreens(connections, new ErpMetadata(connections, TimeProvider.System), new ErpPermissions(connections), TimeProvider.System);
        var report = new List<string>();
        var failures = new List<string>();

        foreach (var spec in forms)
        {
            var parts = spec.Split('|');
            // WEBERP_REAL_QUERY: query string áp cho mọi danh sách, vd "FromDate=2020-01-01" để có dữ liệu.
            var query = (Environment.GetEnvironmentVariable("WEBERP_REAL_QUERY") ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x.Length > 1 ? Uri.UnescapeDataString(x[1]) : "");
            var req = new ScreenRequest(tenant, user, null, parts[0], parts.Length > 1 ? parts[1] : null, query);
            var list = await screens.OpenListAsync(req, CancellationToken.None);
            report.Add($"{spec}: {list.Status} {list.Note} rows={list.Rows?.Count} total={list.Total}");
            if (list.Status != ScreenStatus.Ok) continue;
            var urls = new ScreenUrls($"/erp/screens/{req.FormName}", req.Para, k => $"/erp/screens/{req.FormName}/{Uri.EscapeDataString(k)}",
                (k, s) => $"/erp/screens/{req.FormName}/{Uri.EscapeDataString(k)}/tabs/{s}", $"/erp/screens/{req.FormName}");
            var html = MetaScreens.List(list, list.Form!.Caption, null, urls);
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{req.RuntimeName}.html"), html);
            await AssertValid(html, $"{spec} danh sách");

            if (!list.CanOpenRecord || list.Keys is not { Count: > 0 }) continue;
            var key = list.Keys[0];
            var rec = await screens.OpenRecordAsync(req, key, CancellationToken.None);
            report.Add($"  record {key}: {rec.Status} {rec.Note} tabs={rec.Tabs?.Count} details={rec.Details?.Count}");
            if (rec.Status != ScreenStatus.Ok) continue;
            var recHtml = MetaScreens.Record(rec, list.Form.Caption, null, urls, "__RequestVerificationToken", "test");
            await File.WriteAllTextAsync(Path.Combine(outDir, $"{req.RuntimeName}.record.html"), recHtml);
            await AssertValid(recHtml, $"{spec} chứng từ");
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
