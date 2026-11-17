using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WebErp.Core.Metadata;
using WebErp.Core.Tenancy;

namespace WebErp.Erp.Metadata;

/// <summary>Nơi một tệp mô tả đang dùng được lấy ra.</summary>
public enum OverlaySource
{
    /// <summary>Tệp nhúng trong BE (<c>Metadata/Overlays</c>).</summary>
    BuiltIn,

    /// <summary>Sửa trên trang quản trị, áp cho mọi công ty.</summary>
    Global,

    /// <summary>Sửa trên trang quản trị, riêng một công ty.</summary>
    Tenant,
}

/// <summary>
/// Tệp mô tả form viết tay áp cho MỘT công ty: mô tả riêng của công ty → mô tả chung trên trang quản trị → tệp nhúng trong BE.
/// Mô tả trên trang quản trị lỗi JSON thì bỏ qua (giữ bản kế tiếp) và ghi log; trang quản trị đã kiểm trước khi lưu.
/// </summary>
public sealed class OverlaySet
{
    private readonly IReadOnlyDictionary<string, (FormOverlays.Overlay Overlay, OverlaySource Source)> _items;
    private readonly IReadOnlySet<string> _runtimeForms;

    internal OverlaySet(IReadOnlyDictionary<string, (FormOverlays.Overlay, OverlaySource)> items, IReadOnlySet<string> runtimeForms, string stamp)
    {
        _items = items;
        _runtimeForms = runtimeForms;
        Stamp = stamp;
    }

    /// <summary>Chỉ tệp nhúng (không có nguồn cấu hình: test, công cụ dòng lệnh).</summary>
    public static OverlaySet BuiltInOnly { get; } = new(
        FormOverlays.BuiltIn.ToDictionary(kv => kv.Key, kv => (kv.Value, OverlaySource.BuiltIn), StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase), "0");

    /// <summary>Đổi khi mô tả trên trang quản trị đổi: ghép vào khóa bộ đệm metadata để form đọc lại theo mô tả mới.</summary>
    public string Stamp { get; }

    public bool Has(string formName) => _items.ContainsKey(formName);

    public IReadOnlyCollection<string> Names => _items.Keys.ToList();

    public OverlaySource? SourceOf(string formName) => _items.TryGetValue(formName, out var i) ? i.Source : null;

    /// <summary>
    /// Form viết tay: lớp đã biết của WinForms, hoặc form trang quản trị có mô tả (mô tả trên web luôn che cấu hình DB cùng tên,
    /// như lớp viết tay che <c>SY_FrmLstTbl</c>; dùng cho form của bản ERP mới mà BE chưa biết tên lớp).
    /// </summary>
    public bool IsHandCoded(string formName) => HandCodedForms.Names.Contains(formName) || _runtimeForms.Contains(formName);

    public (FrmLstRow Lst, IReadOnlyList<FrmCfgRow> Rows, bool UseDbConfig, IReadOnlyList<FrmFltRow> Filters)? Get(string formName, string? para) =>
        _items.TryGetValue(formName, out var i) ? i.Overlay.Resolve(para) : null;
}

/// <summary>
/// Gộp tệp mô tả nhúng với mô tả sửa trên trang quản trị (<see cref="IFormOverlayStore"/>). Đọc lại khi phiên bản cấu hình đổi,
/// giữ kết quả theo công ty.
/// </summary>
public sealed class OverlayCatalog(IFormOverlayStore? store = null, ILogger<OverlayCatalog>? logger = null)
{
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;
    private readonly Lock _gate = new();
    private long _version = -1;
    private IReadOnlyList<(FormOverlayRecord Record, IReadOnlyList<FormOverlays.Overlay> Parsed)> _records = [];
    private Dictionary<string, OverlaySet> _byTenant = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<OverlaySet> ForTenantAsync(Tenant tenant, CancellationToken ct)
    {
        if (store is null) return OverlaySet.BuiltInOnly;
        var snapshot = await store.GetFormOverlaysAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            if (snapshot.Version != _version)
            {
                _records = Parse(snapshot.Items);
                _byTenant = new Dictionary<string, OverlaySet>(StringComparer.OrdinalIgnoreCase);
                _version = snapshot.Version;
            }
            if (_byTenant.TryGetValue(tenant.Key, out var hit)) return hit;
            var set = Build(tenant.Key);
            _byTenant[tenant.Key] = set;
            return set;
        }
    }

    private List<(FormOverlayRecord, IReadOnlyList<FormOverlays.Overlay>)> Parse(IReadOnlyList<FormOverlayRecord> items)
    {
        var list = new List<(FormOverlayRecord, IReadOnlyList<FormOverlays.Overlay>)>();
        foreach (var r in items)
        {
            try
            {
                var parsed = FormOverlays.ParseText(r.Json, $"mô tả {r.Form} ({r.TenantKey ?? "mọi công ty"})");
                if (!string.Equals(parsed[0].Form, r.Form, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"mô tả {r.Form}: khóa \"form\" là '{parsed[0].Form}', không khớp.");
                list.Add((r, parsed));
            }
            catch (InvalidDataException ex)
            {
                _log.LogWarning("Bỏ qua mô tả form trên trang quản trị: {Error}", ex.Message);
            }
        }
        return list;
    }

    private OverlaySet Build(string tenantKey)
    {
        var items = FormOverlays.BuiltIn.ToDictionary(kv => kv.Key, kv => (kv.Value, OverlaySource.BuiltIn), StringComparer.OrdinalIgnoreCase);
        var runtime = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var revisions = new List<string>();
        // Chung trước, riêng công ty sau (ghi đè).
        foreach (var (record, parsed) in _records.Where(x => x.Record.TenantKey is null)
                     .Concat(_records.Where(x => string.Equals(x.Record.TenantKey, tenantKey, StringComparison.OrdinalIgnoreCase))))
        {
            var source = record.TenantKey is null ? OverlaySource.Global : OverlaySource.Tenant;
            foreach (var o in parsed)
            {
                items[o.Form] = (o, source);
                runtime.Add(o.Form);
            }
            revisions.Add($"{record.Form}@{record.TenantKey}#{record.Revision.ToString(CultureInfo.InvariantCulture)}");
        }
        // Dấu đổi khi (và chỉ khi) mô tả áp cho công ty này đổi: bộ đệm form của công ty khác không bị xóa vô ích.
        var stamp = revisions.Count == 0 ? "0" : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("|", revisions))))[..12];
        return new OverlaySet(items, runtime, stamp);
    }
}
