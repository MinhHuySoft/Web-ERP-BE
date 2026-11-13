using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Errors;
using WebErp.Core.Layout;
using WebErp.Core.Metadata;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Metadata;
using WebErp.Erp.Security;
using WebErp.Erp.Write;

namespace WebErp.Erp.Layout;

/// <summary>
/// Trình vẽ layout form (M4, chuẩn FE 15.3). Như <c>LayoutSettingFrm</c> của WinForms: đọc / ghi dòng bố cục của FID (tên form chạy,
/// có hậu tố Para) trong <c>dbo.SY_FrmCfg</c>, nên WinForms và web cùng thấy bố cục mới. An toàn hơn WinForms:
/// <list type="bullet">
/// <item>một giao dịch + khóa ứng dụng theo FID; dòng đọc với UPDLOCK; dấu phiên bản để từ chối khi bố cục đã bị đổi từ lúc mở;</item>
/// <item>chỉ ghi dòng bố cục (LYT / LYS / T?V), không xóa ghi lại mọi dòng của FID như WinForms;</item>
/// <item>mỗi lần lưu giữ ảnh trước / sau trong <c>web.FormLayoutVersion</c> (migration 0003) để xem và khôi phục; chưa có bảng thì không lưu.</item>
/// </list>
/// Quyền: Admin trên form (WinForms mở bằng chế độ quản trị). Form viết tay có bố cục trong mã / tệp mô tả thì không vẽ được.
/// </summary>
public sealed class ErpLayout(ErpConnectionFactory connections, ErpMetadata metadata, ErpPermissions permissions, ErpOptions options, TimeProvider time)
    : IErpLayout
{
    private readonly ErpIds _ids = new(time, ErpIds.VietnamZone);

    public async Task<LayoutDesignView> OpenAsync(ScreenRequest request, CancellationToken ct)
    {
        var form = await metadata.GetFormAsync(request.Tenant, request.FormName, request.RuntimeName, ct);
        if (form is null) return new LayoutDesignView(ScreenStatus.NotFound);
        if (Blocked(request) is { } why) return new LayoutDesignView(ScreenStatus.NotSupported, form.Caption, Note: why);
        var perm = await permissions.GetAsync(request.Tenant, request.UserName, request.RuntimeName, ct);
        var formats = await metadata.GetFormatsAsync(request.Tenant, ct);
        return await ReadAsync(request.Tenant, async (c, tx) =>
        {
            var rows = await RowsAsync(c, tx, request.RuntimeName, false, ct);
            var store = await StoreExistsAsync(c, tx, ct);
            var (items, _) = LayoutModel.Parse(rows);
            var fields = await FieldsAsync(c, tx, form, formats, ct);
            var versions = store ? await VersionsAsync(c, tx, request.RuntimeName, ct) : [];
            var canSave = perm.Admin && store;
            var json = JsonSerializer.Serialize(new
            {
                schema = LayoutModel.Schema,
                form = request.RuntimeName,
                title = form.Caption,
                revision = LayoutModel.Revision(rows),
                canSave,
                tabs = Tabs(form, rows),
                // text: chữ trên nút (LYT vị trí 7, LYS BT / LT, chữ mặc định của control) để khung vẽ hiện như màn chứng từ; FE không gửi lại.
                // props: thuộc tính riêng LYS của ô (mã → giá trị).
                items = items.Select(i => new { tab = i.Tab, group = i.Group, name = i.Name, cfg = i.Cfg, text = ButtonText(i, rows), props = i.Props }),
                // Mã LYS web cho sửa theo loại control ("0" = mọi loại); mã khác chỉ hiện để xem (có SQL / lệnh, sửa trên ERP).
                editableProps = LayoutModel.EditableProps.ToDictionary(x => x.Key.ToString(CultureInfo.InvariantCulture), x => x.Value),
                fields,
                versions,
                notes = new[]
                {
                    "Lưu ghi thẳng vào cấu hình form (SY_FrmCfg) như trình thiết kế layout của ERP: WinForms và web cùng dùng bố cục mới.",
                    "Máy đang mở ERP WinForms giữ bố cục cũ trong bộ đệm (tối đa 1 ngày): mở lại ERP để thấy bố cục mới.",
                },
                note = store ? null : "DB công ty chưa có bảng sao lưu bố cục (cần chạy: WebErp.Admin tenant migrate). Hiện chỉ xem.",
            }).Replace("\"schema\":", "\"$schema\":", StringComparison.Ordinal);
            return new LayoutDesignView(ScreenStatus.Ok, form.Caption, json, canSave);
        }, note => new LayoutDesignView(ScreenStatus.ConfigError, form.Caption, Note: note), ct);
    }

    public async Task<LayoutSaveResult> SaveAsync(ScreenRequest request, string json, string? comment, CancellationToken ct)
    {
        var input = LayoutModel.ReadInput(json);
        if (input.Errors.Count > 0) return new LayoutSaveResult(false, Errors: input.Errors.Take(20).ToList());
        if (!string.Equals(input.Form, request.RuntimeName, StringComparison.OrdinalIgnoreCase)) return Fail("Bố cục không thuộc form này.");
        return await WriteAsync(request, comment, "save", null, rows =>
        {
            if (!string.Equals(LayoutModel.Revision(rows), input.Revision, StringComparison.Ordinal))
                return (null, "Bố cục của form đã bị đổi (trên ERP WinForms hoặc bởi người khác) từ lúc bạn mở trình vẽ. Mở lại trình vẽ rồi sửa lại.");
            var plan = LayoutModel.Diff(rows, input.Items, input.Captions, _ids.NewUserAutoId, input.Pages);
            return plan.Errors is { Count: > 0 } errs ? (null, string.Join(" ", errs)) : (plan, null);
        }, ct);
    }

    public async Task<LayoutSaveResult> RestoreAsync(ScreenRequest request, int versionNo, string? comment, CancellationToken ct)
    {
        List<CfgRow>? snapshot = null;
        Func<CfgRow, bool>? scope = null;
        return await WriteAsync(request, comment, "restore", versionNo, rows =>
        {
            if (snapshot is null) return (null, "Không tìm thấy phiên bản cần khôi phục.");
            // Khôi phục: mọi dòng bố cục hiện có (trong phạm vi của ảnh) thay bằng ảnh "trước" của phiên bản, giữ nguyên UserAutoID gốc.
            var current = rows.Where(scope!).ToList();
            var keep = snapshot.Select(s => s.UserAutoID).ToHashSet(StringComparer.Ordinal);
            var curById = current.ToDictionary(r => r.UserAutoID, StringComparer.Ordinal);
            return (new LayoutModel.Plan(
                current.Where(r => !keep.Contains(r.UserAutoID)).ToList(),
                snapshot.Where(s => curById.TryGetValue(s.UserAutoID, out var cur) && !(cur.KeyID == s.KeyID && cur.SubID == s.SubID
                    && cur.KeyValue == s.KeyValue && cur.SubValue == s.SubValue)).ToList(),
                snapshot.Where(s => !curById.ContainsKey(s.UserAutoID)).ToList()), null);
        }, ct, async (c, tx) =>
        {
            var before = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT BeforeJson FROM web.FormLayoutVersion WHERE FID = @f AND VersionNo = @n", new { f = Ansi(request.RuntimeName, 250), n = versionNo },
                tx, options.CommandTimeoutSeconds, cancellationToken: ct));
            if (before is not null) (snapshot, scope) = LayoutModel.ReadSnapshot(before);
        });
    }

    /// <summary>
    /// Ghi trong một giao dịch: khóa FID, đọc dòng (UPDLOCK), lập kế hoạch, xóa / sửa / thêm, lưu ảnh trước / sau, commit, xóa bộ đệm metadata.
    /// </summary>
    private async Task<LayoutSaveResult> WriteAsync(ScreenRequest request, string? comment, string kind, int? restoredFrom,
        Func<List<CfgRow>, (LayoutModel.Plan? Plan, string? Error)> plan, CancellationToken ct, Func<SqlConnection, SqlTransaction, Task>? prepare = null)
    {
        var form = await metadata.GetFormAsync(request.Tenant, request.FormName, request.RuntimeName, ct);
        if (form is null) return Fail("Không tìm thấy form.");
        if (Blocked(request) is { } why) return Fail(why);
        var perm = await permissions.GetAsync(request.Tenant, request.UserName, request.RuntimeName, ct);
        if (!perm.Admin) return Fail("Chỉ người có quyền Admin trên form mới được vẽ layout.");
        var fid = request.RuntimeName;
        try
        {
            await using var c = await connections.OpenAsync(request.Tenant, ct);
            await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
            if (!await StoreExistsAsync(c, tx, ct)) return Fail("DB công ty chưa có bảng sao lưu bố cục (cần chạy: WebErp.Admin tenant migrate).");
            var lockResult = await c.QuerySingleAsync<int>(new CommandDefinition(
                "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 10000; SELECT @r;",
                new { res = "weberp-layout:" + fid.ToUpperInvariant() }, tx, options.CommandTimeoutSeconds, cancellationToken: ct));
            if (lockResult < 0) return Fail("Có người khác đang lưu bố cục form này. Thử lại sau ít giây.");
            if (prepare is not null) await prepare(c, tx);
            var rows = await RowsAsync(c, tx, fid, true, ct);
            var (p, error) = plan(rows);
            if (p is null) return new LayoutSaveResult(false, Errors: [error ?? "Không lưu được."], Conflict: error?.Contains("đã bị đổi", StringComparison.Ordinal) == true);
            var pfid = rows.Select(r => r.PFID).FirstOrDefault(x => !string.IsNullOrEmpty(x));
            var f = Ansi(fid, 250);
            await c.ExecuteAsync(new CommandDefinition("DELETE FROM dbo.SY_FrmCfg WHERE FID = @f AND UserAutoID = @id",
                p.Delete.Select(r => new { f, id = Ansi(r.UserAutoID, 50) }), tx, options.CommandTimeoutSeconds, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition(
                "UPDATE dbo.SY_FrmCfg SET KeyID = @k, SubID = @s, KeyValue = @v, SubValue = @o, VDate = GETDATE() WHERE FID = @f AND UserAutoID = @id",
                p.Update.Select(r => new { f, id = Ansi(r.UserAutoID, 50), k = Ansi(r.KeyID, 50), s = AnsiOrNull(r.SubID), v = r.KeyValue, o = r.SubValue }),
                tx, options.CommandTimeoutSeconds, cancellationToken: ct));
            await c.ExecuteAsync(new CommandDefinition(
                "INSERT dbo.SY_FrmCfg (UserAutoID, FID, KeyID, SubID, KeyValue, SubValue, VDate, PFID) VALUES (@id, @f, @k, @s, @v, @o, GETDATE(), @pf)",
                p.Insert.Select(r => new { f, id = Ansi(r.UserAutoID, 50), k = Ansi(r.KeyID, 50), s = AnsiOrNull(r.SubID), v = r.KeyValue, o = r.SubValue,
                    pf = r.PFID ?? pfid }), tx, options.CommandTimeoutSeconds, cancellationToken: ct));
            var after = await RowsAsync(c, tx, fid, false, ct);
            var beforeJson = LayoutModel.Snapshot(rows);
            var afterJson = LayoutModel.Snapshot(after);
            var no = await c.ExecuteScalarAsync<int>(new CommandDefinition(
                """
                DECLARE @no int = ISNULL((SELECT MAX(VersionNo) FROM web.FormLayoutVersion WITH (UPDLOCK, HOLDLOCK) WHERE FID = @f), 0) + 1;
                INSERT web.FormLayoutVersion (FID, VersionNo, BeforeJson, BeforeSha256, AfterJson, AfterSha256, Kind, RestoredFrom, Comment, CreatedBy)
                VALUES (@f, @no, @b, @bs, @a, @as, @kind, @from, @comment, @u);
                SELECT @no;
                """,
                new
                {
                    f, b = beforeJson, bs = Sha(beforeJson), a = afterJson, @as = Sha(afterJson), kind, from = restoredFrom,
                    comment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim()[..Math.Min(comment.Trim().Length, 500)], u = request.UserName,
                }, tx, options.CommandTimeoutSeconds, cancellationToken: ct));
            await tx.CommitAsync(ct);
            metadata.Invalidate(request.Tenant, fid);
            return new LayoutSaveResult(true, no, Changed: p.Count);
        }
        catch (SqlException ex) when (ErpConnectionFactory.IsConnectivity(ex))
        {
            throw new ErpUnavailableException(request.Tenant.Key, ex);
        }
    }

    private static string? ButtonText(LayoutItem i, List<CfgRow> rows)
    {
        var type = LayoutModel.ControlType(i.Cfg);
        if (type is not (22 or 24 or 26)) return null;
        var props = rows.Where(r => r.KeyID.StartsWith("LYS", StringComparison.OrdinalIgnoreCase) && r.SubValue is "BT" or "LT"
                                    && i.Name.Equals(r.SubID, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.KeyValue))
            .GroupBy(r => $"{r.KeyID[3..]}|{r.SubID}|{r.SubValue}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().KeyValue!.Trim(), StringComparer.OrdinalIgnoreCase);
        var caption = (i.Cfg.Split(';') is { Length: > 7 } p ? p[7] : "").Replace(':', ';').Trim();
        return MetadataCompiler.ButtonText(type, i.Name, caption, i.Zone, props);
    }

    /// <summary>
    /// Nguồn chép bố cục (như "Copy Layout" của WinForms): không có <paramref name="fid"/> ⇒ danh sách FID có bố cục; có ⇒ bố cục
    /// (ô + thuộc tính LYS, tiêu đề tab) của FID đó, chỉ đọc. Chỉ người được vẽ form đang mở (Admin) mới xem.
    /// </summary>
    public async Task<string?> SourceAsync(ScreenRequest request, string? fid, CancellationToken ct)
    {
        var perm = await permissions.GetAsync(request.Tenant, request.UserName, request.RuntimeName, ct);
        if (!perm.Admin) return null;
        return await ReadAsync<string?>(request.Tenant, async (c, tx) =>
        {
            if (string.IsNullOrEmpty(fid))
            {
                var list = await c.QueryAsync<(string Fid, int N, string? Caption)>(new CommandDefinition(
                    """
                    SELECT c.FID, COUNT(*), MAX(l.CaptionVN) FROM dbo.SY_FrmCfg c LEFT JOIN dbo.SY_FrmLstTbl l ON l.FormID = c.FID
                    WHERE c.KeyID LIKE 'LYT%' AND c.FID IS NOT NULL GROUP BY c.FID ORDER BY c.FID
                    """, transaction: tx, commandTimeout: options.CommandTimeoutSeconds, cancellationToken: ct));
                return JsonSerializer.Serialize(new { forms = list.Select(x => new { fid = x.Fid, items = x.N, caption = x.Caption?.Trim() }) });
            }
            var rows = await RowsAsync(c, tx, fid, false, ct);
            var (items, _) = LayoutModel.Parse(rows);
            string? Cap(string key) => rows.FirstOrDefault(r => r.KeyID.Equals(key, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(r.SubID))?.KeyValue?.Trim();
            return JsonSerializer.Serialize(new
            {
                form = fid,
                captions = LayoutModel.CaptionKeys.ToDictionary(k => k, Cap),
                items = items.Select(i => new { tab = i.Tab, group = i.Group, name = i.Name, cfg = i.Cfg, text = ButtonText(i, rows), props = i.Props }),
            });
        }, _ => null, ct);
    }

    /// <summary>Form viết tay có bố cục trong tệp mô tả (không đọc SY_FrmCfg): web không vẽ được.</summary>
    private static string? Blocked(ScreenRequest request) =>
        FormOverlays.Get(request.FormName, request.Para) is { UseDbConfig: false }
            ? "Form này viết tay, bố cục nằm trong mã chương trình (không đọc cấu hình layout), nên không vẽ được trên web."
            : HandCodedForms.Names.Contains(request.FormName) && !FormOverlays.Has(request.FormName)
                ? "Form này viết tay, web chưa hỗ trợ." : null;

    private async Task<List<CfgRow>> RowsAsync(SqlConnection c, SqlTransaction tx, string fid, bool forUpdate, CancellationToken ct) =>
        (await c.QueryAsync<CfgRow>(new CommandDefinition(
            $"SELECT UserAutoID, KeyID, SubID, KeyValue, SubValue, VDate, PFID FROM dbo.SY_FrmCfg {(forUpdate ? "WITH (UPDLOCK, HOLDLOCK) " : "")}WHERE FID = @f",
            new { f = Ansi(fid, 250) }, tx, options.CommandTimeoutSeconds, cancellationToken: ct))).ToList();

    private async Task<bool> StoreExistsAsync(SqlConnection c, SqlTransaction tx, CancellationToken ct) =>
        await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT CASE WHEN OBJECT_ID(N'web.FormLayoutVersion', N'U') IS NULL THEN 0 ELSE 1 END",
            transaction: tx, commandTimeout: options.CommandTimeoutSeconds, cancellationToken: ct)) == 1;

    private async Task<object[]> VersionsAsync(SqlConnection c, SqlTransaction tx, string fid, CancellationToken ct) =>
        (await c.QueryAsync<(int VersionNo, DateTime CreatedAt, string CreatedBy, string Kind, int? RestoredFrom, string? Comment)>(new CommandDefinition(
            "SELECT TOP 30 VersionNo, CreatedAt, CreatedBy, Kind, RestoredFrom, Comment FROM web.FormLayoutVersion WHERE FID = @f ORDER BY VersionNo DESC",
            new { f = Ansi(fid, 250) }, tx, options.CommandTimeoutSeconds, cancellationToken: ct)))
        .Select(v => (object)new
        {
            no = v.VersionNo, at = v.CreatedAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture), by = v.CreatedBy, kind = v.Kind,
            restoredFrom = v.RestoredFrom, comment = v.Comment,
        }).ToArray();

    /// <summary>Tab của trình vẽ: master (tiêu đề T?V sửa được) và trang chi tiết (tiêu đề theo lưới cùng trang, chỉ xem).</summary>
    private static object[] Tabs(FormMeta form, List<CfgRow> rows)
    {
        string? Cap(string key) => rows.FirstOrDefault(r => r.KeyID.Equals(key, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(r.SubID))?.KeyValue?.Trim();
        var list = new List<object>();
        for (var i = 0; i < 6; i++)
        {
            var id = LayoutModel.Tabs[i];
            list.Add(new { id, kind = "master", captionKey = LayoutModel.CaptionKeys[i], caption = Cap(LayoutModel.CaptionKeys[i]) ?? "",
                defaultCaption = i == 0 ? "Thông tin chung" : $"Thông tin {i + 1}" });
        }
        var (dcps, vis) = LayoutModel.Pages(rows);
        for (var n = 1; n <= 9; n++)
        {
            var slot = form.Slots.GetValueOrDefault($"T{n}");
            var own = form.Setting($"L{n + 4}V");
            list.Add(new { id = (n + 4).ToString(CultureInfo.InvariantCulture), kind = "detail", page = n,
                caption = own ?? slot?.Caption ?? $"Chi tiết {n}", defaultCaption = $"Chi tiết {n}",
                hint = own is not null ? $"Trang riêng \"{own}\" (form viết tay) đứng trước lưới T{n}."
                    : $"Trang chi tiết {n}: ô của chứng từ (LYT{n + 4}) đứng trên lưới T{n}{(slot?.Caption is { } dc ? $" \"{dc}\"" : "")} cùng trang.",
                // Tiêu đề trang (T{n} / DCP) và quyền xem (TPV{n}, trang 1–5) sửa được khi form có lưới T{n} và không phải trang riêng của form viết tay.
                grid = slot is not null,
                dcp = dcps.GetValueOrDefault(n), dcpEditable = slot is not null && own is null,
                visible = n <= 5 ? vis.GetValueOrDefault(n) ?? "0" : null, visibleEditable = slot is not null && n <= 5 });
        }
        return [.. list];
    }

    /// <summary>
    /// Cột của bảng / view chính (như "Reload Field" của WinForms đọc cột của nguồn dữ liệu chính): tên, kiểu, tiêu đề. Bỏ cột bí mật.
    /// </summary>
    private async Task<object[]> FieldsAsync(SqlConnection c, SqlTransaction tx, FormMeta form, FieldFormats formats, CancellationToken ct)
    {
        if (form.Master?.Source is not { Length: > 0 } source) return [];
        var cols = await c.QueryAsync<(string Name, string Type)>(new CommandDefinition(
            "SELECT c.name, t.name FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(@t) ORDER BY c.column_id",
            new { t = new DbString { Value = source, IsAnsi = false, Length = 300 } }, tx, options.CommandTimeoutSeconds, cancellationToken: ct));
        return cols.Where(x => !Screens.ErpScreens.IsSecret(x.Name) && x.Type is not ("varbinary" or "image" or "binary" or "timestamp"))
            .Select(x => (object)new
            {
                name = x.Name,
                type = x.Type switch
                {
                    "bit" => "bool",
                    "date" or "datetime" or "smalldatetime" or "datetime2" or "datetimeoffset" => "date",
                    "int" or "bigint" or "smallint" or "tinyint" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "number",
                    _ => "text",
                },
                caption = form.Captions.GetValueOrDefault($"C:{x.Name}") ?? form.MasterDropdown(x.Name)?.Caption
                          ?? formats.Caption(x.Name, form.RuntimeName, form.FormName),
            }).ToArray();
    }

    private async Task<T> ReadAsync<T>(Tenant tenant, Func<SqlConnection, SqlTransaction, Task<T>> work, Func<string, T> error, CancellationToken ct)
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
            return error($"Không đọc được cấu hình form (SQL {ex.Number}).");
        }
        finally
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { } catch (SqlException) { }
        }
    }

    private static DbString Ansi(string v, int len) => new() { Value = v, IsAnsi = true, Length = len };

    private static DbString? AnsiOrNull(string? v) => v is null ? null : new DbString { Value = v, IsAnsi = true, Length = 50 };

    private static string Sha(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static LayoutSaveResult Fail(string e) => new(false, Errors: [e]);
}
