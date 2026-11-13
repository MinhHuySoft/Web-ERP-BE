using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Errors;
using WebErp.Core.Metadata;
using WebErp.Core.Print;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;

namespace WebErp.Erp.Print;

/// <summary>
/// Kho mẫu web (M6 GĐ2, migration <c>0002_print_templates</c>) và quyền sửa mẫu như <c>ReportViewerFrm</c>:
/// <list type="bullet">
/// <item>Mẫu có hiệu lực: phiên bản đang dùng của <c>web.PrintTemplate</c> (JSON) nếu đã sửa trên web, không thì <c>.rpx</c>.</item>
/// <item>Quyền sửa: quyền Admin trên form luôn được; còn lại theo <c>EPER</c> của mẫu (<c>SY_FrmCfg</c>, FID = tên tệp mẫu):
/// 0 / trống = mọi người dùng, 1 = cần quyền Quản lý, 2 = chỉ Admin.</item>
/// <item>Lưu: kiểm mẫu lại ở BE, thêm một phiên bản bất biến, trỏ mẫu sang phiên bản đó. Chỉ ghi schema <c>web</c> (QĐ-02).</item>
/// </list>
/// </summary>
public sealed partial class ErpPrint
{
    /// <summary>Cỡ tối đa của một mẫu JSON (kể cả ảnh nhúng base64).</summary>
    public const int MaxTemplateChars = 4_000_000;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex ControlIdPattern();

    [GeneratedRegex("\"sys\"\\s*:\\s*\"([A-Za-z0-9_]{1,60})\"")]
    private static partial Regex JsonSys();

    private static readonly HashSet<string> BandTypes = ["reportHeader", "pageHeader", "groupHeader", "detail", "groupFooter", "pageFooter", "reportFooter"];
    private static readonly HashSet<string> ControlTypes = ["text", "line", "rect", "image", "checkbox", "barcode", "subreport", "crossLine", "chart", "pageBreak"];
    private static readonly HashSet<string> ImageMimes = ["image/png", "image/jpeg", "image/gif", "image/bmp"];

    /// <summary>Mẫu có hiệu lực: phiên bản web nếu có, không thì <c>.rpx</c> (SY_RpFTbl / thư mục).</summary>
    private async Task<PrintTemplateFile?> ResolveTemplateAsync(SqlConnection c, SqlTransaction tx, Tenant tenant, string report, string file, CancellationToken ct)
    {
        if (await WebStoreExistsAsync(c, tx, ct))
        {
            var web = await c.QueryFirstOrDefaultAsync<(string? Json, int VersionNo, DateTime CreatedAt)>(new CommandDefinition(
                """
                SELECT v.Json, v.VersionNo, v.CreatedAt FROM web.PrintTemplate t JOIN web.PrintTemplateVersion v ON v.VersionID = t.CurrentVersionID
                WHERE t.ReportName = @r AND t.TemplateKey = @k
                """, new { r = report, k = file }, tx, options.CommandTimeoutSeconds, cancellationToken: ct));
            if (!string.IsNullOrEmpty(web.Json))
                return new PrintTemplateFile(file + ".json", Encoding.UTF8.GetBytes(web.Json), web.CreatedAt, "web", "application/json",
                    "w" + web.VersionNo.ToString(CultureInfo.InvariantCulture));
        }
        var rpx = await LoadTemplateAsync(c, tx, tenant, file, ct);
        return rpx is null ? null : rpx with { Version = "r" + (rpx.FileDate ?? DateTime.MinValue).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) };
    }

    private async Task<bool> WebStoreExistsAsync(SqlConnection c, SqlTransaction? tx, CancellationToken ct) =>
        await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT CASE WHEN OBJECT_ID(N'web.PrintTemplateVersion', N'U') IS NULL THEN 0 ELSE 1 END",
            transaction: tx, commandTimeout: options.CommandTimeoutSeconds, cancellationToken: ct)) == 1;

    /// <summary>Tên tham số hệ thống mẫu dùng: control <c>Sys*</c> của <c>.rpx</c>, hoặc <c>"sys": "…"</c> của mẫu JSON.</summary>
    private static IEnumerable<string> SysNames(PrintTemplateFile file)
    {
        var text = Encoding.UTF8.GetString(file.Content);
        if (file.Format == "json")
        {
            foreach (Match m in JsonSys().Matches(text)) yield return m.Groups[1].Value.Split('_')[0];
            yield break;
        }
        foreach (Match m in SysControl().Matches(text))
            if (m.Groups[1].Value.StartsWith("Sys", StringComparison.OrdinalIgnoreCase)) yield return m.Groups[1].Value.Split('_')[0];
    }

    /// <summary>Quyền sửa mẫu (<c>ReportViewerFrm</c>: Admin luôn được; <c>EPER</c> 1 cần Quản lý, 2 chỉ Admin).</summary>
    private async Task<bool> CanDesignAsync(SqlConnection c, SqlTransaction tx, ScreenRequest request, string file, CancellationToken ct)
    {
        var perm = await permissions.GetAsync(request.Tenant, request.UserName, request.RuntimeName, ct);
        if (perm.Admin) return true;
        var raw = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP 1 KeyValue FROM dbo.SY_FrmCfg WHERE FID = @f AND KeyID = 'EPER'", new { f = file }, tx, options.CommandTimeoutSeconds, cancellationToken: ct));
        var level = int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return level switch { 0 => true, 1 => perm.Manager, _ => false };
    }

    public async Task<PrintDesignView> DesignAsync(ScreenRequest request, string? template, CancellationToken ct)
    {
        var form = await metadata.GetFormAsync(request.Tenant, request.FormName, request.RuntimeName, ct);
        if (form is null || !form.IsEdit || ReportName(form) is not { } report) return new PrintDesignView(ScreenStatus.NotSupported);
        return await RunAsync(request.Tenant, async (c, tx) =>
        {
            if (!await ProcedureExistsAsync(c, tx, report, ct)) return new PrintDesignView(ScreenStatus.NotSupported, report);
            var templates = await TemplatesAsync(c, tx, report, ct);
            var chosen = templates.FirstOrDefault(t => t.File.Equals(template ?? "", StringComparison.OrdinalIgnoreCase)) ?? templates[0];
            var file = await ResolveTemplateAsync(c, tx, request.Tenant, report, chosen.File, ct);
            if (file is null) return new PrintDesignView(ScreenStatus.NotFound, report, templates, chosen.File, Note: $"Không tìm thấy mẫu {chosen.File}.");
            return new PrintDesignView(ScreenStatus.Ok, report, templates, chosen.File, file.Format, file.Version, await CanDesignAsync(c, tx, request, chosen.File, ct));
        }, note => new PrintDesignView(ScreenStatus.ConfigError, report, Note: note), ct);
    }

    private async Task<IReadOnlyList<PrintTemplateOption>> TemplatesAsync(SqlConnection c, SqlTransaction tx, string report, CancellationToken ct) =>
        ParseTemplates(await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT TOP 1 CAST(DefaultValueVN AS nvarchar(4000)) FROM dbo.SY_RpDTbl WHERE RpName = @r AND PName = 'Template'",
            new { r = report }, tx, options.CommandTimeoutSeconds, cancellationToken: ct)), report);

    public async Task<PrintSaveResult> SaveTemplateAsync(ScreenRequest request, string template, string json, string? comment, CancellationToken ct)
    {
        var form = await metadata.GetFormAsync(request.Tenant, request.FormName, request.RuntimeName, ct);
        if (form is null || !form.IsEdit || ReportName(form) is not { } report) return Fail("Màn này không có báo cáo in.");
        var errors = ValidateTemplate(json);
        if (errors.Count > 0) return new PrintSaveResult(false, Errors: errors);

        // Đọc (giao dịch luôn rollback): mẫu thuộc báo cáo, quyền sửa, nguồn hiện tại.
        var check = await RunAsync<(string? Error, PrintTemplateFile? Source)>(request.Tenant, async (c, tx) =>
        {
            if (!await WebStoreExistsAsync(c, tx, ct)) return ("Kho mẫu web chưa được tạo trong DB công ty (cần chạy: tenant migrate).", null);
            var templates = await TemplatesAsync(c, tx, report, ct);
            var chosen = templates.FirstOrDefault(t => t.File.Equals(template, StringComparison.OrdinalIgnoreCase));
            if (chosen is null) return ("Mẫu này không thuộc báo cáo của màn.", null);
            if (!await CanDesignAsync(c, tx, request, chosen.File, ct)) return ("Bạn không có quyền sửa mẫu báo cáo này.", null);
            return (null, await ResolveTemplateAsync(c, tx, request.Tenant, report, chosen.File, ct));
        }, note => (note, null), ct);
        if (check.Error is not null) return Fail(check.Error);

        // Ghi (giao dịch thật, chỉ schema web): khóa dòng mẫu, phiên bản kế tiếp, trỏ mẫu sang phiên bản mới.
        var key = templateKey(template);
        var src = check.Source;
        var fromRpx = src is not null && src.Source != "web";
        try
        {
            await using var c = await connections.OpenAsync(request.Tenant, ct);
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            var no = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                DECLARE @id int = (SELECT TemplateID FROM web.PrintTemplate WITH (UPDLOCK, HOLDLOCK) WHERE ReportName = @r AND TemplateKey = @k);
                IF @id IS NULL
                BEGIN
                  INSERT web.PrintTemplate (ReportName, TemplateKey, CreatedBy) VALUES (@r, @k, @u);
                  SET @id = CAST(SCOPE_IDENTITY() AS int);
                END
                DECLARE @no int = ISNULL((SELECT MAX(VersionNo) FROM web.PrintTemplateVersion WHERE TemplateID = @id), 0) + 1;
                INSERT web.PrintTemplateVersion (TemplateID, VersionNo, Json, JsonSha256, Comment, SourceKind, SourceFileDate, SourceSha256, CreatedBy)
                VALUES (@id, @no, @json, @sha, @comment, @kind, @srcDate, @srcSha, @u);
                UPDATE web.PrintTemplate SET CurrentVersionID = CAST(SCOPE_IDENTITY() AS int), UpdatedAt = GETDATE(), UpdatedBy = @u WHERE TemplateID = @id;
                SELECT @no;
                """,
                new
                {
                    r = report, k = key, u = request.UserName, json, sha = Sha256(Encoding.UTF8.GetBytes(json)),
                    comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 500)],
                    kind = fromRpx ? "rpx" : "web", srcDate = fromRpx ? src!.FileDate : null, srcSha = fromRpx ? Sha256(src!.Content) : null,
                }, tx, options.CommandTimeoutSeconds, cancellationToken: ct));
            await tx.CommitAsync(ct);
            return new PrintSaveResult(true, no);
        }
        catch (SqlException ex) when (ErpConnectionFactory.IsConnectivity(ex))
        {
            throw new ErpUnavailableException(request.Tenant.Key, ex);
        }

        static string templateKey(string t) => t.Trim();
        static PrintSaveResult Fail(string e) => new(false, Errors: [e]);
    }

    private static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>
    /// Kiểm mẫu ở BE (không tin FE): JSON hợp lệ, đúng <c>$schema</c>, khổ giấy hợp lý, band / control đúng loại, mã control không trùng,
    /// biểu thức không quá dài, ảnh nhúng chỉ PNG/JPEG/GIF/BMP base64. Biểu thức chỉ được FE tính trong ngôn ngữ an toàn, BE không chạy.
    /// </summary>
    internal static List<string> ValidateTemplate(string json)
    {
        var errors = new List<string>();
        if (json.Length > MaxTemplateChars) return [$"Mẫu quá lớn (tối đa {MaxTemplateChars / 1_000_000} triệu ký tự)."];
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return ["Mẫu không phải JSON hợp lệ."]; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Str(root, "$schema") != "erp-print-template/1") return ["Mẫu không đúng định dạng erp-print-template/1."];
            if (!root.TryGetProperty("page", out var page) || !InRange(page, "width", 10, 2000) || !InRange(page, "height", 10, 2000))
                errors.Add("Khổ giấy không hợp lệ (rộng / cao từ 10 đến 2000 mm).");
            if (!root.TryGetProperty("bands", out var bands) || bands.ValueKind != JsonValueKind.Array || bands.GetArrayLength() is 0 or > 60)
                return [.. errors, "Mẫu phải có từ 1 đến 60 band."];
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var b in bands.EnumerateArray())
            {
                var type = Str(b, "type");
                if (type is null || !BandTypes.Contains(type)) { errors.Add($"Band có loại không hợp lệ: {type}."); continue; }
                if (!b.TryGetProperty("controls", out var ctls) || ctls.ValueKind != JsonValueKind.Array) continue;
                foreach (var c in ctls.EnumerateArray())
                {
                    if (++count > 5000) return [.. errors, "Mẫu có quá nhiều control (tối đa 5000)."];
                    var id = Str(c, "id") ?? "";
                    if (!ControlIdPattern().IsMatch(id)) errors.Add($"Mã control không hợp lệ: \"{id[..Math.Min(id.Length, 40)]}\".");
                    else if (!ids.Add(id)) errors.Add($"Mã control bị trùng: {id}.");
                    var ct = Str(c, "type");
                    if (ct is null || !ControlTypes.Contains(ct)) errors.Add($"Control {id} có loại không hợp lệ: {ct}.");
                    foreach (var e in Expressions(c))
                        if (e.Length > 2000) errors.Add($"Biểu thức của {id} dài quá 2000 ký tự.");
                }
            }
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Object)
                foreach (var a in assets.EnumerateObject())
                {
                    var mime = Str(a.Value, "mime");
                    var data = Str(a.Value, "data");
                    if (mime is null || !ImageMimes.Contains(mime)) errors.Add($"Ảnh {a.Name} không phải PNG/JPEG/GIF/BMP.");
                    else if (data is not null && !IsBase64(data)) errors.Add($"Ảnh {a.Name} không phải base64.");
                    if (a.Value.TryGetProperty("href", out _)) errors.Add($"Ảnh {a.Name} không được trỏ ra URL ngoài.");
                }
        }
        return errors.Distinct().Take(20).ToList();

        static IEnumerable<string> Expressions(JsonElement c)
        {
            if (c.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object && Str(v, "expr") is { } e) yield return e;
            if (Str(c, "visibleWhen") is { } w) yield return w;
            if (c.TryGetProperty("rules", out var rules) && rules.ValueKind == JsonValueKind.Array)
                foreach (var r in rules.EnumerateArray()) if (Str(r, "when") is { } x) yield return x;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool InRange(JsonElement e, string name, double min, double max) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetDouble() is var d && d >= min && d <= max;

    private static bool IsBase64(string s)
    {
        if (s.Length % 4 != 0) return false;
        foreach (var ch in s) if (!(char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=')) return false;
        return true;
    }
}
