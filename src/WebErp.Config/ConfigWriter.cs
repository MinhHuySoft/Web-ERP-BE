using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using WebErp.Core.Tenancy;

namespace WebErp.Config;

/// <summary>Một dòng của lệnh liệt kê. Không chứa chuỗi kết nối, chỉ cho biết đã đặt hay chưa.</summary>
public sealed record TenantSummary(Tenant Tenant, bool HasConnection, IReadOnlyList<string> Hosts);

/// <summary>
/// Ghi cấu hình (chỉ công cụ quản trị dùng). Mỗi thao tác: một transaction gồm thay đổi + tăng <c>config_meta.version</c> +
/// một dòng <c>admin_audit</c>. Nhật ký không bao giờ chứa chuỗi kết nối.
/// </summary>
public sealed class ConfigWriter(ConfigDatabase db, SecretProtector protector, TimeProvider time, string actor)
{
    public IReadOnlyList<(int Version, string Name)> Initialize()
    {
        using var c = db.OpenReadWrite();
        return ConfigMigrator.Migrate(c);
    }

    public void AddTenant(string key, string name, string moduleId = "SY", string? taxCode = null, string? databaseLabel = null)
    {
        if (!TenantNames.IsValidKey(key)) throw new ArgumentException("Mã công ty chỉ gồm chữ, số, '_' hoặc '-', tối đa 40 ký tự.", nameof(key));
        if (!TenantNames.IsValidModule(moduleId)) throw new ArgumentException("Mã module chỉ gồm 2–5 chữ cái (vd SY).", nameof(moduleId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Thiếu tên công ty.", nameof(name));
        Change("tenant.add", key, $"name={name}; module={moduleId}", (c, tx, now) =>
            c.Execute("INSERT INTO tenant (tenant_key, name, module_id, tax_code, database_label, created_at, updated_at) VALUES (@key, @name, @module, @tax, @label, @now, @now)",
                new { key, name = name.Trim(), module = moduleId.ToUpperInvariant(), tax = taxCode, label = databaseLabel, now }, tx));
    }

    public void SetConnection(string key, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Chuỗi kết nối rỗng.", nameof(connectionString));
        var protectedValue = protector.Protect(connectionString.Trim());
        Change("tenant.set-connection", key, null, (c, tx, now) =>
            Require(c.Execute("UPDATE tenant SET connection_protected = @p, updated_at = @now WHERE tenant_key = @key", new { p = protectedValue, now, key }, tx), key));
    }

    public void AddHost(string key, string host)
    {
        if (!TenantNames.IsValidHost(host)) throw new ArgumentException($"Tên miền '{host}' không hợp lệ.", nameof(host));
        var h = TenantNames.NormalizeHost(host);
        Change("host.add", key, h, (c, tx, _) =>
        {
            Require(c.ExecuteScalar<long>("SELECT COUNT(*) FROM tenant WHERE tenant_key = @key", new { key }, tx) > 0 ? 1 : 0, key);
            var sort = c.ExecuteScalar<long>("SELECT COALESCE(MAX(sort) + 1, 0) FROM tenant_host WHERE host = @h", new { h }, tx);
            c.Execute("INSERT OR IGNORE INTO tenant_host (host, tenant_key, sort) VALUES (@h, @key, @sort)", new { h, key, sort }, tx);
        });
    }

    public void RemoveHost(string key, string host)
    {
        var h = TenantNames.NormalizeHost(host);
        Change("host.remove", key, h, (c, tx, _) => c.Execute("DELETE FROM tenant_host WHERE host = @h AND tenant_key = @key", new { h, key }, tx));
    }

    public void SetEnabled(string key, bool enabled) =>
        Change(enabled ? "tenant.enable" : "tenant.disable", key, null, (c, tx, now) =>
            Require(c.Execute("UPDATE tenant SET enabled = @e, updated_at = @now WHERE tenant_key = @key", new { e = enabled ? 1 : 0, now, key }, tx), key));

    /// <param name="until">null = tắt bảo trì.</param>
    public void SetMaintenance(string key, DateTimeOffset? until) =>
        Change("tenant.maintenance", key, until is null ? "off" : Iso(until.Value), (c, tx, now) =>
            Require(c.Execute("UPDATE tenant SET maintenance_until = @u, updated_at = @now WHERE tenant_key = @key", new { u = until is null ? null : Iso(until.Value), now, key }, tx), key));

    /// <summary>Đăng xuất mọi người của công ty: cookie cấp trước lúc này bị từ chối (QĐ-07 hệ quả 2).</summary>
    public void LogoutAll(string key) =>
        Change("tenant.logout-all", key, null, (c, tx, now) =>
            Require(c.Execute("UPDATE tenant SET logout_before = @now, updated_at = @now WHERE tenant_key = @key", new { now, key }, tx), key));

    public IReadOnlyList<TenantSummary> List()
    {
        using var c = db.OpenReadOnly();
        var hosts = c.Query<(string Host, string TenantKey)>("SELECT host, tenant_key FROM tenant_host ORDER BY sort, host")
            .ToLookup(h => h.TenantKey, h => h.Host, StringComparer.OrdinalIgnoreCase);
        return c.Query<(string Key, string Name, string Module, string? Conn, long Enabled, string? Maint, string? Logout, string? Tax, string? Label)>(
                "SELECT tenant_key, name, module_id, connection_protected, enabled, maintenance_until, logout_before, tax_code, database_label FROM tenant ORDER BY tenant_key")
            .Select(r => new TenantSummary(
                new Tenant(r.Key, r.Name, r.Module, r.Enabled != 0, SqliteTenantStore.ParseTime(r.Maint), SqliteTenantStore.ParseTime(r.Logout), r.Tax, r.Label),
                r.Conn is not null, hosts[r.Key].ToList()))
            .ToList();
    }

    /// <summary>Bản chụp nhất quán của tệp cấu hình (<c>VACUUM INTO</c>), an toàn khi BE đang chạy.</summary>
    public string Backup(string directory)
    {
        Directory.CreateDirectory(directory);
        var target = System.IO.Path.Combine(directory, $"config-{time.GetUtcNow():yyyyMMdd-HHmmss}.db");
        using var c = db.OpenReadWrite();
        c.Execute("VACUUM INTO @target", new { target });
        return target;
    }

    private void Change(string action, string target, string? detail, Action<SqliteConnection, SqliteTransaction, string> change)
    {
        using var c = db.OpenReadWrite();
        using var tx = c.BeginTransaction();
        var now = Iso(time.GetUtcNow());
        change(c, tx, now);
        c.Execute("UPDATE config_meta SET version = version + 1 WHERE id = 1", transaction: tx);
        c.Execute("INSERT INTO admin_audit (at, actor, action, target, detail) VALUES (@now, @actor, @action, @target, @detail)",
            new { now, actor, action, target, detail }, tx);
        tx.Commit();
    }

    private static void Require(int affected, string key)
    {
        if (affected == 0) throw new InvalidOperationException($"Không có công ty '{key}'.");
    }

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
