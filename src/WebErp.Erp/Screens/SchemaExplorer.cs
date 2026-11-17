using Dapper;
using Microsoft.Data.SqlClient;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Metadata;
using WebErp.Erp.Query;

namespace WebErp.Erp.Screens;

/// <summary>Một cột của bảng / view trong DB công ty. <see cref="Caption"/>: tiêu đề tiếng Việt của ERP (<c>SY_FmtFldTbl</c>) nếu có.</summary>
public sealed record DbColumn(string Name, string Type, bool IsPrimaryKey, string? Caption);

/// <summary>Kết quả xem thử một bảng của mô tả: tên cột và vài dòng dữ liệu thật (đã bỏ cột bí mật).</summary>
public sealed record QuickPreviewTable(string Slot, string Table, IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, string? Error);

/// <summary>
/// Đọc cấu trúc DB công ty cho trang "Tạo nhanh" (tên bảng, cột, khóa chính, khóa ngoại, tiêu đề cột) và xem thử dữ liệu. Chỉ đọc:
/// truy vấn danh mục hệ thống (<c>sys.*</c>); xem thử chạy câu dựng từ mô tả qua cùng lớp kiểm chỉ đọc với Host, trong giao dịch luôn
/// rollback. Cột bí mật (mật khẩu, token…) không bao giờ được liệt kê hay đọc.
/// </summary>
public sealed class SchemaExplorer(ErpConnectionFactory connections, ErpMetadata metadata)
{
    public const int PreviewRows = 5;

    /// <summary>Chờ tiêu đề cột tối đa chừng này; quá thì dùng tên cột, tiêu đề tiếp tục nạp nền cho lần sau.</summary>
    public static readonly TimeSpan CaptionWait = TimeSpan.FromSeconds(5);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<FieldFormats>> _loading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tiêu đề cột của công ty còn đang nạp nền (máy chủ SQL chậm): trang nên báo "bấm Nạp lại cột sau ít giây".</summary>
    public bool CaptionsPending(Tenant tenant) => _loading.TryGetValue(tenant.Key, out var t) && !t.IsCompleted;

    /// <summary>
    /// Tiêu đề cột (<c>SY_FmtFldTbl</c>, đọc cả bảng, có bộ đệm 5 phút ở <see cref="ErpMetadata"/>). Lần đầu trên máy chủ bận có thể quá
    /// 30 giây: chỉ chờ <see cref="CaptionWait"/>, không chặn trang; một lần nạp nền cho mỗi công ty, xong thì vào bộ đệm. Lỗi thì không có tiêu đề.
    /// </summary>
    private async Task<FieldFormats> FormatsAsync(Tenant tenant, CancellationToken ct)
    {
        var task = _loading.GetOrAdd(tenant.Key, key =>
        {
            var t = metadata.GetFormatsAsync(tenant, CancellationToken.None);
            t.ContinueWith(done => done.Exception, TaskContinuationOptions.OnlyOnFaulted); // quan sát lỗi nạp nền
            return t;
        });
        var finished = await Task.WhenAny(task, Task.Delay(CaptionWait, ct)) == task;
        // Xong (được hay lỗi) thì bỏ khỏi danh sách đang nạp: lần sau đọc bộ đệm của ErpMetadata, hoặc thử nạp lại nếu lần này lỗi.
        if (task.IsCompleted) _loading.TryRemove(new KeyValuePair<string, Task<FieldFormats>>(tenant.Key, task));
        return finished && task.IsCompletedSuccessfully ? task.Result : FieldFormats.Empty;
    }

    /// <summary>Bảng và view của người dùng (bỏ bảng hệ thống), theo tên.</summary>
    public async Task<IReadOnlyList<string>> ObjectsAsync(Tenant tenant, CancellationToken ct)
    {
        await using var c = await connections.OpenAsync(tenant, ct);
        return (await c.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM sys.objects WHERE type IN ('U', 'V') AND is_ms_shipped = 0 ORDER BY name",
            commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct))).ToList();
    }

    /// <summary>Tên menu tiếng Việt của form (<c>{Mod}_Menu.VN</c>), null nếu không có.</summary>
    public async Task<string?> MenuCaptionAsync(Tenant tenant, string formName, CancellationToken ct)
    {
        if (!TenantNames.IsValidModule(tenant.ModuleId)) return null;
        var menu = $"dbo.{tenant.ModuleId.ToUpperInvariant()}_Menu";
        await using var c = await connections.OpenAsync(tenant, ct);
        if (await c.ExecuteScalarAsync<int>(new CommandDefinition("SELECT CASE WHEN OBJECT_ID(@t) IS NULL THEN 0 ELSE 1 END", new { t = menu }, cancellationToken: ct)) == 0)
            return null;
        // Form mở từ nhiều mục menu (theo Para): lấy mục không có Para trước.
        return (await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT TOP 1 VN FROM {menu} WHERE FormName = @f ORDER BY CASE WHEN COALESCE(Para, '') = '' THEN 0 ELSE 1 END, MenuID",
            new { f = new DbString { Value = formName, IsAnsi = true, Length = 100 } }, commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct)))?.Trim();
    }

    /// <summary>Cột (bỏ cột bí mật), đánh dấu khóa chính, kèm tiêu đề tiếng Việt của ERP. Rỗng nếu không có bảng.</summary>
    public async Task<IReadOnlyList<DbColumn>> ColumnsAsync(Tenant tenant, string table, string formName, CancellationToken ct)
    {
        var formats = await FormatsAsync(tenant, ct);
        await using var c = await connections.OpenAsync(tenant, ct);
        var rows = await c.QueryAsync<(string Name, string Type, int Pk)>(new CommandDefinition(
            "SELECT c.name, t.name, CASE WHEN EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id " +
            "WHERE i.is_primary_key = 1 AND ic.object_id = c.object_id AND ic.column_id = c.column_id) THEN 1 ELSE 0 END " +
            "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(@t) ORDER BY c.column_id",
            new { t = new DbString { Value = table, IsAnsi = false, Length = 300 } }, commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct));
        return rows.Where(r => !ErpScreens.IsSecret(r.Name))
            .Select(r => new DbColumn(r.Name, r.Type, r.Pk == 1, formats.Caption(r.Name, formName, formName)))
            .ToList();
    }

    /// <summary>
    /// Tên thay mã theo quy ước ERP: cột <c>XxxID</c> → bảng danh mục <c>CF_XxxTbl</c> (hoặc <c>CF_Xxx</c>, <c>&lt;tiền tố&gt;XxxTbl</c>) có cột
    /// <c>XxxID</c> và <c>XxxName</c>; hiện thêm <c>XxxName</c> (và <c>Address</c> / <c>Unit</c> nếu có, như mô tả có sẵn). Chỉ đọc tên cột.
    /// </summary>
    public async Task<IReadOnlyList<QuickLookup>> LookupsAsync(Tenant tenant, string table, IReadOnlyList<DbColumn> columns, IReadOnlyList<string> objects,
        CancellationToken ct)
    {
        var have = new HashSet<string>(objects, StringComparer.OrdinalIgnoreCase);
        var prefix = QuickGuesser.Prefix(table);
        var wanted = new List<(string Column, string Base, string Table)>();
        foreach (var c in columns)
        {
            if (c.IsPrimaryKey || c.Name.Length <= 2 || !c.Name.EndsWith("ID", StringComparison.OrdinalIgnoreCase)) continue;
            var b = c.Name[..^2];
            var target = new[] { $"CF_{b}Tbl", $"CF_{b}", $"{prefix}{b}Tbl" }
                .FirstOrDefault(n => have.Contains(n) && !n.Equals(table, StringComparison.OrdinalIgnoreCase));
            if (target is not null) wanted.Add((c.Name, b, objects.First(o => o.Equals(target, StringComparison.OrdinalIgnoreCase))));
        }
        if (wanted.Count == 0) return [];
        await using var c2 = await connections.OpenAsync(tenant, ct);
        var p = new DynamicParameters();
        var ids = new List<string>();
        foreach (var (w, i) in wanted.Select((w, i) => (w, i)))
        {
            p.Add($"t{i}", w.Table);
            ids.Add($"OBJECT_ID(@t{i})");
        }
        var cols = (await c2.QueryAsync<(string Table, string Column)>(new CommandDefinition(
                $"SELECT OBJECT_NAME(object_id), name FROM sys.columns WHERE object_id IN ({string.Join(", ", ids)})", p,
                commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct)))
            .ToLookup(x => x.Table, x => x.Column, StringComparer.OrdinalIgnoreCase);
        var result = new List<QuickLookup>();
        foreach (var w in wanted)
        {
            var tc = cols[w.Table].ToHashSet(StringComparer.OrdinalIgnoreCase);
            var name = $"{w.Base}Name";
            if (!tc.Contains(w.Column) || !tc.Contains(name)) continue;
            var show = new List<string> { tc.First(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)) };
            foreach (var extra in new[] { "Address", "Unit" })
                if (tc.FirstOrDefault(x => x.Equals(extra, StringComparison.OrdinalIgnoreCase)) is { } e) show.Add(e);
            result.Add(new QuickLookup(w.Column, w.Table, tc.First(x => x.Equals(w.Column, StringComparison.OrdinalIgnoreCase)), show));
        }
        return result;
    }

    /// <summary>
    /// Cột nối bảng chi tiết về bảng chính: theo khóa ngoại nếu có; không thì cột trùng tên với khóa chính của bảng chính.
    /// Trả (cột bảng chính, cột bảng chi tiết) hoặc null.
    /// </summary>
    public async Task<(string MasterKey, string DetailKey)?> LinkAsync(Tenant tenant, string master, string detail, string? masterPk, CancellationToken ct)
    {
        await using var c = await connections.OpenAsync(tenant, ct);
        var fk = await c.QueryFirstOrDefaultAsync<(string Parent, string Child)>(new CommandDefinition(
            "SELECT pc.name, cc.name FROM sys.foreign_key_columns f " +
            "JOIN sys.columns pc ON pc.object_id = f.referenced_object_id AND pc.column_id = f.referenced_column_id " +
            "JOIN sys.columns cc ON cc.object_id = f.parent_object_id AND cc.column_id = f.parent_column_id " +
            "WHERE f.referenced_object_id = OBJECT_ID(@m) AND f.parent_object_id = OBJECT_ID(@d)",
            new { m = master, d = detail }, commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct));
        if (fk.Parent is not null) return (fk.Parent, fk.Child);
        if (masterPk is null) return null;
        var same = await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT name FROM sys.columns WHERE object_id = OBJECT_ID(@d) AND name = @c",
            new { d = detail, c = masterPk }, commandTimeout: connections.Options.CommandTimeoutSeconds, cancellationToken: ct));
        return same is null ? null : (masterPk, same);
    }

    /// <summary>
    /// Xem thử: dựng form từ mô tả như Host, đọc <see cref="PreviewRows"/> dòng đầu bảng chính (theo thứ tự đã chọn) và các dòng chi tiết
    /// của dòng đầu đó. Dữ liệu thật, chỉ hiện trên trang quản trị; cột bí mật bị bỏ.
    /// </summary>
    public async Task<IReadOnlyList<QuickPreviewTable>> PreviewAsync(Tenant tenant, QuickSpec spec, CancellationToken ct)
    {
        var parsed = FormOverlays.ParseText(spec.ToJson(), "Tạo nhanh");
        var overlay = parsed[0].Resolve(null);
        await using var c = await connections.OpenAsync(tenant, ct);
        var form = await metadata.BuildFormAsync(c, tenant, spec.Form, spec.Form, overlay, handCoded: true, ct);
        var result = new List<QuickPreviewTable>();
        if (form?.Master is not { } master) return [new QuickPreviewTable("T0", spec.Master.Table, [], [], "Không dựng được bảng chính từ lựa chọn.")];
        await using var tx = (SqlTransaction)await c.BeginTransactionAsync(ct);
        try
        {
            var (head, first) = await ReadAsync(master, null, c, tx, ct);
            result.Add(head);
            foreach (var d in form.Details)
            {
                if (first is null || d.MasterField is null || d.DetailField is null)
                {
                    result.Add(new QuickPreviewTable(d.Id, d.Source, [], [], first is null ? "Bảng chính chưa có dòng nào để xem chi tiết." : "Chưa chọn cột nối."));
                    continue;
                }
                var key = first.TryGetValue(d.MasterField, out var v) ? v : null;
                result.Add((await ReadAsync(d, (d.DetailField, key), c, tx, ct)).Table);
            }
        }
        finally
        {
            try { await tx.RollbackAsync(CancellationToken.None); } catch (InvalidOperationException) { } catch (SqlException) { }
        }
        return result;
    }

    private async Task<(QuickPreviewTable Table, Dictionary<string, object?>? First)> ReadAsync(Core.Metadata.SlotMeta slot, (string Column, object? Value)? filter,
        SqlConnection c, SqlTransaction tx, CancellationToken ct)
    {
        try
        {
            var q = new SlotQuery(slot, _ => (true, null));
            if (filter is { } f) q.Where.Add($"{q.Col(f.Column)} = {q.Param(f.Value)}");
            var data = await SqlReader.ReadAsync(c, tx, q.ListSql(PreviewRows), q.Parameters, connections.Options.CommandTimeoutSeconds, PreviewRows, ct);
            // Mọi cột đều được đọc (như lưới ERP); xem thử chỉ hiện cột hiện sẵn (GD2, theo thứ tự đó) để giống màn web lúc mở.
            var index = Enumerable.Range(0, data.Names.Length).Where(i => !ErpScreens.IsSecret(data.Names[i]))
                .ToDictionary(i => data.Names[i], i => i, StringComparer.OrdinalIgnoreCase);
            var keep = slot.Order.Count == 0 ? index.Values.ToArray()
                : new[] { slot.PrimaryKey }.Concat(slot.Order).Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(index.ContainsKey).Select(n => index[n]).ToArray();
            var rows = data.Rows.Select(r => (IReadOnlyList<string?>)keep.Select(i => Text(r[i])).ToList()).ToList();
            var first = data.Rows.Count == 0 ? null
                : Enumerable.Range(0, data.Names.Length).ToDictionary(i => data.Names[i], i => data.Rows[0][i], StringComparer.OrdinalIgnoreCase);
            return (new QuickPreviewTable(slot.Id, slot.Source, keep.Select(i => data.Names[i]).ToList(), rows, null), first);
        }
        catch (UnsafeMetadataSqlException ex)
        {
            return (new QuickPreviewTable(slot.Id, slot.Source, [], [], ex.Message), null);
        }
        catch (SqlException ex) when (!ErpConnectionFactory.IsConnectivity(ex))
        {
            return (new QuickPreviewTable(slot.Id, slot.Source, [], [], $"SQL lỗi: {SqlErrorText.Describe(ex)}"), null);
        }
    }

    private static string? Text(object? v) => v switch
    {
        null => null,
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture)
            : d.ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        byte[] b => $"({b.Length} byte)",
        IFormattable x => x.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => v.ToString() is { Length: > 200 } s ? s[..200] + "…" : v.ToString(),
    };
}
