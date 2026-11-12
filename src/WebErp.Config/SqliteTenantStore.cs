using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using WebErp.Core.Tenancy;

namespace WebErp.Config;

/// <summary>
/// <see cref="ITenantStore"/> đọc SQLite. Giữ bản chụp trong bộ nhớ, cứ <see cref="CheckInterval"/> lại so
/// <c>config_meta.version</c>; khác thì nạp lại. Nhờ vậy sửa cấu hình bằng công cụ quản trị có hiệu lực trong vài giây, không
/// phải khởi động lại BE.
/// </summary>
public sealed class SqliteTenantStore(ConfigDatabase db, SecretProtector protector, TimeProvider time) : ITenantStore
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _snapshot;
    private DateTimeOffset _checkedAt;

    public async ValueTask<IReadOnlyList<Tenant>> FindByHostAsync(string host, CancellationToken ct)
    {
        var s = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return s.ByHost.TryGetValue(TenantNames.NormalizeHost(host), out var list) ? list : [];
    }

    public async ValueTask<Tenant?> GetAsync(string key, CancellationToken ct)
    {
        var s = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return s.ByKey.GetValueOrDefault(key);
    }

    public async ValueTask<string?> GetConnectionStringAsync(string key, CancellationToken ct)
    {
        var s = await GetSnapshotAsync(ct).ConfigureAwait(false);
        return s.Connections.TryGetValue(key, out var p) && p is not null ? protector.Unprotect(p) : null;
    }

    /// <summary>Phiên bản cấu hình đang dùng (cho /health).</summary>
    public async ValueTask<long> GetVersionAsync(CancellationToken ct) => (await GetSnapshotAsync(ct).ConfigureAwait(false)).Version;

    private async ValueTask<Snapshot> GetSnapshotAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var current = _snapshot;
        if (current is not null && now - _checkedAt < CheckInterval) return current;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_snapshot is not null && now - _checkedAt < CheckInterval) return _snapshot;
            using var c = db.OpenReadOnly();
            var schema = ConfigMigrator.CurrentVersion(c);
            if (schema < ConfigMigrator.RequiredVersion)
                throw new InvalidOperationException($"Tệp cấu hình ở phiên bản {schema}, cần {ConfigMigrator.RequiredVersion}. Chạy 'WebErp.Admin init'.");
            var version = c.ExecuteScalar<long>("SELECT version FROM config_meta WHERE id = 1");
            if (_snapshot is null || _snapshot.Version != version) _snapshot = Load(c, version);
            _checkedAt = now;
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Snapshot Load(SqliteConnection c, long version)
    {
        var rows = c.Query<TenantRow>(
            "SELECT tenant_key AS TenantKey, name AS Name, module_id AS ModuleId, connection_protected AS ConnectionProtected, enabled AS Enabled, " +
            "maintenance_until AS MaintenanceUntil, logout_before AS LogoutBefore, tax_code AS TaxCode, database_label AS DatabaseLabel FROM tenant").ToList();
        var byKey = new Dictionary<string, Tenant>(StringComparer.OrdinalIgnoreCase);
        var connections = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            byKey[r.TenantKey] = new Tenant(r.TenantKey, r.Name, r.ModuleId, r.Enabled != 0, ParseTime(r.MaintenanceUntil), ParseTime(r.LogoutBefore), r.TaxCode, r.DatabaseLabel);
            connections[r.TenantKey] = r.ConnectionProtected;
        }

        var byHost = new Dictionary<string, IReadOnlyList<Tenant>>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in c.Query<(string Host, string TenantKey, long Sort)>("SELECT host, tenant_key, sort FROM tenant_host ORDER BY host, sort, tenant_key")
                     .GroupBy(h => TenantNames.NormalizeHost(h.Host)))
            byHost[g.Key] = g.Select(h => byKey.GetValueOrDefault(h.TenantKey)).OfType<Tenant>().ToList();

        return new Snapshot(version, byKey, byHost, connections);
    }

    internal static DateTimeOffset? ParseTime(string? s) =>
        string.IsNullOrEmpty(s) ? null : DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record Snapshot(
        long Version,
        Dictionary<string, Tenant> ByKey,
        Dictionary<string, IReadOnlyList<Tenant>> ByHost,
        Dictionary<string, string?> Connections);

    private sealed class TenantRow
    {
        public string TenantKey { get; set; } = "";
        public string Name { get; set; } = "";
        public string ModuleId { get; set; } = "SY";
        public string? ConnectionProtected { get; set; }
        public long Enabled { get; set; }
        public string? MaintenanceUntil { get; set; }
        public string? LogoutBefore { get; set; }
        public string? TaxCode { get; set; }
        public string? DatabaseLabel { get; set; }
    }
}
