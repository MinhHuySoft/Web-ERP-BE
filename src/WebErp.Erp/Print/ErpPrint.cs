using System.Data;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Errors;
using WebErp.Core.Messages;
using WebErp.Core.Metadata;
using WebErp.Core.Print;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;
using WebErp.Erp.Security;

namespace WebErp.Erp.Print;

/// <summary>
/// In theo mẫu như <c>ReportViewerFrm</c> (M6, docs FE <c>06-mau-in.md</c> §7):
/// <list type="bullet">
/// <item>Báo cáo của form chứng từ: <c>BPRN</c> trong <c>SY_FrmCfg</c>, mặc định tên form bỏ <c>Frm</c> + <c>Report</c>; phải có SP
/// <c>&lt;Report&gt;Stp</c>.</item>
/// <item>Mẫu: <c>SY_RpDTbl</c> (<c>RpName</c>, <c>PName='Template'</c>, "tệp;Tên;…"); nội dung là bản mới nhất của <c>SY_RpFTbl</c>
/// (<c>FileName = tệp + '.rpx'</c>), không có thì thư mục Reports của công ty (<see cref="ErpOptions.ReportsRoot"/>).</item>
/// <item>Dữ liệu: <c>&lt;Report&gt;Stp</c> với <c>@Template</c> và tham số chứng từ (<c>@&lt;PK&gt;</c> / <c>@DocumentID</c>, nhiều chứng từ
/// nối bằng <c>;</c>) trong giao dịch LUÔN rollback, như mọi đường đọc của web.</item>
/// <item>Tham số hệ thống (<c>GetSysParam</c>): <c>{Module}_Setup</c> (tên <c>CodeID</c> và <c>Sys</c>+<c>CodeID</c>), chỉ những tên mẫu
/// dùng (control <c>Sys*</c>), không bao giờ gửi tên có dạng bí mật.</item>
/// </list>
/// </summary>
public sealed partial class ErpPrint(ErpConnectionFactory connections, ErpMetadata metadata, IErpScreens screens, ErpPermissions permissions,
    ErpOptions options) : IErpPrint
{
    /// <summary>Số chứng từ tối đa một lần in (mỗi chứng từ được kiểm quyền xem).</summary>
    public const int MaxDocuments = 50;

    /// <summary>Số dòng tối đa mỗi result set gửi cho FE.</summary>
    public const int MaxRows = 20000;

    [GeneratedRegex("^[A-Za-z0-9_]{1,100}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("^[A-Za-z0-9_()\\-. ]{1,120}$")]
    private static partial Regex FilePattern();

    [GeneratedRegex("Name=\"((?:Sys|Lng)[A-Za-z0-9_]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SysControl();

    private static readonly string[] AlwaysSys = ["ReportLocation", "isReportPrintDate", "ServerDate", "UserFullName", "UserName"];

    public async Task<string?> ReportOfAsync(ScreenRequest request, CancellationToken ct)
    {
        var form = await metadata.GetFormAsync(request.Tenant, request.FormName, request.RuntimeName, ct);
        if (form is null || !form.IsEdit) return null;
        var report = ReportName(form);
        if (report is null) return null;
        await using var c = await connections.OpenAsync(request.Tenant, ct);
        return await ProcedureExistsAsync(c, null, report, ct) ? report : null;
    }

    /// <summary>Tên báo cáo của form: <c>BPRN</c>, mặc định <c>X_TenFrm → X_TenReport</c>.</summary>
    internal static string? ReportName(FormMeta form)
    {
        var name = form.Setting("BPRN")?.Trim();
        if (string.IsNullOrEmpty(name))
            name = (form.FormName.EndsWith("Frm", StringComparison.OrdinalIgnoreCase) ? form.FormName[..^3] : form.FormName) + "Report";
        return NamePattern().IsMatch(name) ? name : null;
    }

    /// <summary>"tệp;Tên;tệp;Tên…" → danh sách mẫu (bỏ tệp tên lạ); rỗng thì một mẫu cùng tên báo cáo.</summary>
    internal static IReadOnlyList<PrintTemplateOption> ParseTemplates(string? list, string report)
    {
        var parts = (list ?? "").Split(';');
        var opts = new List<PrintTemplateOption>();
        for (var i = 0; i < parts.Length; i += 2)
        {
            var file = parts[i].Trim();
            if (file.EndsWith(".rpx", StringComparison.OrdinalIgnoreCase)) file = file[..^4];
            if (file.Length == 0 || !FilePattern().IsMatch(file) || opts.Any(o => o.File.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            var title = i + 1 < parts.Length ? parts[i + 1].Trim() : "";
            opts.Add(new PrintTemplateOption(file, title.Length > 0 ? title : file));
        }
        return opts.Count > 0 ? opts : [new PrintTemplateOption(report, report)];
    }

    public async Task<PrintView> PrepareAsync(ScreenRequest request, IReadOnlyList<string> documentIds, string? template,
        IReadOnlyDictionary<string, string> userInputs, CancellationToken ct)
    {
        var form = await metadata.GetFormAsync(request.Tenant, request.FormName, request.RuntimeName, ct);
        if (form is null || !form.IsEdit) return new PrintView(ScreenStatus.NotSupported);
        var report = ReportName(form);
        if (report is null) return new PrintView(ScreenStatus.NotSupported);
        var ids = documentIds.Select(d => d.Trim()).Where(d => d.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count == 0 || ids.Count > MaxDocuments) return new PrintView(ScreenStatus.NotFound, report, Note: $"Chọn từ 1 đến {MaxDocuments} chứng từ để in.");
        // Chỉ in chứng từ người dùng xem được (lọc chi nhánh / người lập / quyền như màn chứng từ).
        foreach (var id in ids)
        {
            var r = await screens.OpenRecordAsync(request with { Query = new Dictionary<string, string>() }, id, ct);
            if (r.Status != ScreenStatus.Ok) return new PrintView(r.Status == ScreenStatus.ConfigError ? ScreenStatus.ConfigError : ScreenStatus.NotFound, report, Note: r.Note);
        }
        var tokens = await metadata.GetUserTokensAsync(request.Tenant, request.UserName, ct);
        return await RunAsync(request.Tenant, async (c, tx) =>
        {
            if (!await ProcedureExistsAsync(c, tx, report, ct)) return new PrintView(ScreenStatus.NotSupported, report);
            var templates = ParseTemplates(await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT TOP 1 CAST(DefaultValueVN AS nvarchar(4000)) FROM dbo.SY_RpDTbl WHERE RpName = @r AND PName = 'Template'",
                new { r = report }, tx, options.CommandTimeoutSeconds, cancellationToken: ct)), report);
            var chosen = templates.FirstOrDefault(t => t.File.Equals(template ?? "", StringComparison.OrdinalIgnoreCase)) ?? templates[0];
            var inputs = (await c.QueryAsync<(string Name, string? Caption)>(new CommandDefinition(
                    "SELECT PName, Caption FROM dbo.SY_RpDTbl WHERE RpName = @r AND PName LIKE 'UInput[1-9]' ORDER BY PName",
                    new { r = report }, tx, options.CommandTimeoutSeconds, cancellationToken: ct)))
                .Select(u => new PrintUserInput(u.Name, string.IsNullOrWhiteSpace(u.Caption) ? u.Name : u.Caption!.Trim(),
                    userInputs.TryGetValue(u.Name, out var v) ? v[..Math.Min(v.Length, 200)] : null)).ToList();

            // Tham số SP: @Template, tham số chứng từ, còn lại theo phiên / tham số người dùng / NULL.
            var spParams = (await c.QueryAsync<(string Name, string Type, short MaxLength)>(new CommandDefinition(
                "SELECT p.name, TYPE_NAME(p.user_type_id), p.max_length FROM sys.parameters p WHERE p.object_id = OBJECT_ID(@sp) ORDER BY p.parameter_id",
                new { sp = $"dbo.{report}Stp" }, tx, options.CommandTimeoutSeconds, cancellationToken: ct))).ToList();
            var pk = form.Slots.TryGetValue("T0", out var t0) ? t0.PrimaryKey : "DocumentID";
            var docParam = spParams.FirstOrDefault(p => p.Name.Equals("@" + pk, StringComparison.OrdinalIgnoreCase)).Name
                ?? spParams.FirstOrDefault(p => p.Name.Equals("@DocumentID", StringComparison.OrdinalIgnoreCase)).Name
                ?? spParams.FirstOrDefault(p => !p.Name.Equals("@Template", StringComparison.OrdinalIgnoreCase)).Name;
            var args = new DynamicParameters();
            foreach (var p in spParams)
            {
                object? value = null;
                var bare = p.Name.TrimStart('@');
                if (bare.Equals("Template", StringComparison.OrdinalIgnoreCase)) value = chosen.File;
                else if (p.Name == docParam) value = string.Join(';', ids);
                else if (bare.Equals("UserName", StringComparison.OrdinalIgnoreCase) || bare.Equals("User", StringComparison.OrdinalIgnoreCase)) value = request.UserName;
                else if (bare.Equals("BranchID", StringComparison.OrdinalIgnoreCase)) value = request.BranchId;
                else if (bare.Equals("LangID", StringComparison.OrdinalIgnoreCase) || bare.Equals("Lang", StringComparison.OrdinalIgnoreCase)) value = "VN";
                else if (bare.Equals("ModuleID", StringComparison.OrdinalIgnoreCase)) value = request.Tenant.ModuleId;
                args.Add(p.Name, value);
            }
            var sets = new List<PrintDataSet>();
            var messages = new List<ErpMessage>();
            await using (var reader = await c.ExecuteReaderAsync(new CommandDefinition($"dbo.[{report}Stp]", args, tx, options.CommandTimeoutSeconds,
                             CommandType.StoredProcedure, cancellationToken: ct)))
            {
                do
                {
                    if (reader.FieldCount == 0) continue;
                    var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
                    var types = Enumerable.Range(0, reader.FieldCount).Select(reader.GetFieldType).ToList();
                    var keep = names.Select((n, i) => (n, i)).Where(x => x.n.Length > 0 && !ErpScreens.IsSecret(x.n) && types[x.i] != typeof(byte[])).ToList();
                    var rows = new List<object?[]>();
                    var truncated = false;
                    while (await reader.ReadAsync(ct))
                    {
                        if (rows.Count >= MaxRows) { truncated = true; continue; }
                        rows.Add(keep.Select(k => reader.IsDBNull(k.i) ? null : reader.GetValue(k.i)).ToArray());
                    }
                    if (IsMessageSet(names) && rows.Count <= 5)
                    {
                        var mi = keep.FindIndex(k => k.n.Equals("Msg", StringComparison.OrdinalIgnoreCase));
                        var ti = keep.FindIndex(k => k.n.Equals("Type", StringComparison.OrdinalIgnoreCase));
                        foreach (var r in rows)
                        {
                            var text = Convert.ToString(r[mi], CultureInfo.InvariantCulture);
                            if (string.IsNullOrWhiteSpace(text)) continue;
                            var t = ti >= 0 && r[ti] is not null ? Convert.ToInt32(r[ti], CultureInfo.InvariantCulture) : 0;
                            messages.Add(new ErpMessage(t == 1 ? MessageType.Error : t == 2 ? MessageType.Warning : MessageType.Info, text, LegacyType: t));
                        }
                        continue;
                    }
                    sets.Add(new PrintDataSet(keep.Select(k => new ViewColumn(k.n, k.n, ColumnType(types[k.i]), null, false, false)).ToList(), rows, truncated));
                }
                while (await reader.NextResultAsync(ct));
            }

            // Tham số hệ thống mẫu cần (control Sys* trong .rpx) + vài tên luôn gửi; không bao giờ gửi tên dạng bí mật.
            var file = await ResolveTemplateAsync(c, tx, request.Tenant, report, chosen.File, ct);
            var wanted = new HashSet<string>(AlwaysSys, StringComparer.OrdinalIgnoreCase);
            if (file is not null) wanted.UnionWith(SysNames(file));
            var lookup = tokens.GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
            var serverDate = await c.ExecuteScalarAsync<DateTime>(new CommandDefinition("SELECT GETDATE()", transaction: tx, cancellationToken: ct));
            var ps = new List<PrintParam> { new("Template", "text", chosen.File), new(docParam?.TrimStart('@') ?? "DocumentID", "text", string.Join(';', ids)) };
            foreach (var u in inputs) ps.Add(new PrintParam(u.Name, "text", u.Value ?? "", u.Caption));
            foreach (var name in wanted.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (ErpScreens.IsSecret(name)) continue;
                if (name.Equals("ServerDate", StringComparison.OrdinalIgnoreCase)) { ps.Add(new PrintParam("Sys.ServerDate", "datetime", serverDate)); continue; }
                if (name.Equals("isReportPrintDate", StringComparison.OrdinalIgnoreCase))
                {
                    // SystemPara.isReportPrintDate mặc định true; tham số isReportPrintDate = 0 thì không in giờ in.
                    var on = !lookup.TryGetValue(name, out var flag) || !(Convert.ToString(flag, CultureInfo.InvariantCulture)?.Trim() is "0" or "False" or "false");
                    ps.Add(new PrintParam("Sys.ReportPrintDate", "bool", on));
                    continue;
                }
                var key = name.Equals("UserFullName", StringComparison.OrdinalIgnoreCase) && !lookup.ContainsKey(name) ? "FullName" : name;
                if (lookup.TryGetValue(key, out var v) || (name.StartsWith("Sys", StringComparison.OrdinalIgnoreCase) && lookup.TryGetValue(name[3..], out v)))
                    ps.Add(new PrintParam("Sys." + name, "text", v is null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture)));
                else if (name.Equals("UserName", StringComparison.OrdinalIgnoreCase)) ps.Add(new PrintParam("Sys.UserName", "text", request.UserName));
            }
            if (sets.Count == 0) messages.Add(ErpMessage.Warning($"{report}Stp không trả dữ liệu để in."));
            if (sets.Any(s => s.Truncated)) messages.Add(ErpMessage.Warning($"Dữ liệu in quá {MaxRows.ToString("N0", CultureInfo.GetCultureInfo("vi-VN"))} dòng, chỉ in phần đầu."));
            if (file is null) messages.Add(ErpMessage.Error($"Không tìm thấy mẫu {chosen.File}.rpx trong SY_RpFTbl hoặc thư mục Reports của công ty."));
            else if (file.Source == "web") messages.Add(ErpMessage.Info($"Đang dùng mẫu sửa trên web (phiên bản {file.Version[1..]}); WinForms vẫn in theo mẫu .rpx của nó."));
            return new PrintView(ScreenStatus.Ok, report, templates, chosen.File, file?.Format ?? "rpx", ids, ps, inputs, sets, messages,
                TemplateVersion: file?.Version ?? "", CanDesign: file is not null && await CanDesignAsync(c, tx, request, chosen.File, ct));
        }, note => new PrintView(ScreenStatus.ConfigError, report, Note: note), ct);
    }

    public async Task<PrintTemplateFile?> TemplateAsync(ScreenRequest request, string template, CancellationToken ct)
    {
        var form = await metadata.GetFormAsync(request.Tenant, request.FormName, request.RuntimeName, ct);
        if (form is null || !form.IsEdit || ReportName(form) is not { } report || !FilePattern().IsMatch(template)) return null;
        return await RunAsync<PrintTemplateFile?>(request.Tenant, async (c, tx) =>
        {
            var templates = await TemplatesAsync(c, tx, report, ct);
            var chosen = templates.FirstOrDefault(t => t.File.Equals(template, StringComparison.OrdinalIgnoreCase));
            return chosen is null ? null : await ResolveTemplateAsync(c, tx, request.Tenant, report, chosen.File, ct);
        }, _ => null, ct);
    }

    /// <summary>Bản mới nhất trong <c>SY_RpFTbl</c>, không có thì tệp trong thư mục Reports của công ty.</summary>
    private async Task<PrintTemplateFile?> LoadTemplateAsync(SqlConnection c, SqlTransaction tx, Tenant tenant, string file, CancellationToken ct)
    {
        var row = await c.QueryFirstOrDefaultAsync<(byte[]? Content, DateTime? FileDate)>(new CommandDefinition(
            "SELECT TOP 1 CAST(FileContent AS varbinary(max)), FileDate FROM dbo.SY_RpFTbl WHERE FileName = @f ORDER BY FileDate DESC",
            new { f = file + ".rpx" }, tx, options.CommandTimeoutSeconds, cancellationToken: ct));
        if (row.Content is { Length: > 0 }) return new PrintTemplateFile(file + ".rpx", row.Content, row.FileDate, "db");
        if (string.IsNullOrWhiteSpace(options.ReportsRoot)) return null;
        var dir = Path.GetFullPath(Path.Combine(options.ReportsRoot, tenant.Key));
        var path = Path.GetFullPath(Path.Combine(dir, file + ".rpx"));
        if (!path.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        return new PrintTemplateFile(file + ".rpx", await File.ReadAllBytesAsync(path, ct), File.GetLastWriteTime(path), "folder");
    }

    private async Task<bool> ProcedureExistsAsync(SqlConnection c, SqlTransaction? tx, string report, CancellationToken ct) =>
        await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT CASE WHEN OBJECT_ID(@sp, 'P') IS NULL THEN 0 ELSE 1 END",
            new { sp = $"dbo.{report}Stp" }, tx, options.CommandTimeoutSeconds, cancellationToken: ct)) == 1;

    /// <summary>Bảng thông điệp của SP: có cột <c>Msg</c> (và thường <c>Type</c>), không cột nào khác ngoài vài cột phụ.</summary>
    private static bool IsMessageSet(IReadOnlyList<string> names) =>
        names.Any(n => n.Equals("Msg", StringComparison.OrdinalIgnoreCase)) && names.Count <= 3;

    private static string ColumnType(Type t) =>
        t == typeof(DateTime) || t == typeof(DateTimeOffset) ? "datetime"
        : t == typeof(bool) ? "bool"
        : t == typeof(decimal) || t == typeof(double) || t == typeof(float) || t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ? "number"
        : "text";

    /// <summary>Giao dịch LUÔN rollback (như <c>ErpScreens</c>): SP báo cáo có lỡ ghi gì cũng không lưu.</summary>
    private async Task<T> RunAsync<T>(Tenant tenant, Func<SqlConnection, SqlTransaction, Task<T>> work, Func<string, T> error, CancellationToken ct)
    {
        await using var c = await connections.OpenAsync(tenant, ct);
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            return await work(c, tx);
        }
        catch (SqlException ex) when (ErpConnectionFactory.IsConnectivity(ex))
        {
            throw new ErpUnavailableException(tenant.Key, ex);
        }
        catch (SqlException ex)
        {
            return error($"SP báo cáo lỗi (SQL {ex.Number}): {ex.Message}");
        }
        finally
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { } catch (SqlException) { }
        }
    }
}
