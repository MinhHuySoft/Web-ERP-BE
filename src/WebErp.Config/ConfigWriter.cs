using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using WebErp.Core.Tenancy;

namespace WebErp.Config;

/// <summary>Một dòng của lệnh liệt kê. Không chứa chuỗi kết nối, chỉ cho biết đã đặt hay chưa.</summary>
public sealed record TenantSummary(Tenant Tenant, bool HasConnection, IReadOnlyList<string> Hosts);

/// <summary>Chi tiết để sửa. <see cref="Revision"/> dùng làm <c>__version</c> phát hiện hai người cùng sửa.</summary>
public sealed record TenantDetail(Tenant Tenant, bool HasConnection, IReadOnlyList<string> Hosts, string Revision);

public sealed record AuditEntry(long Id, DateTimeOffset At, string Actor, string Action, string? Target, string? Detail);

/// <summary>Bản ghi đã bị người khác sửa sau khi mở (trả 409).</summary>
public sealed class ConfigConflictException(string message) : Exception(message);

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

    public TenantDetail? Get(string key)
    {
        using var c = db.OpenReadOnly();
        var r = c.QuerySingleOrDefault<(string Key, string Name, string Module, string? Conn, long Enabled, string? Maint, string? Logout, string? Tax, string? Label, long Revision)>(
            "SELECT tenant_key, name, module_id, connection_protected, enabled, maintenance_until, logout_before, tax_code, database_label, revision FROM tenant WHERE tenant_key = @key",
            new { key });
        if (r.Key is null) return null;
        var hosts = c.Query<string>("SELECT host FROM tenant_host WHERE tenant_key = @key ORDER BY sort, host", new { key }).ToList();
        return new TenantDetail(new Tenant(r.Key, r.Name, r.Module, r.Enabled != 0, SqliteTenantStore.ParseTime(r.Maint), SqliteTenantStore.ParseTime(r.Logout), r.Tax, r.Label),
            r.Conn is not null, hosts, r.Revision.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Sửa thông tin công ty và danh sách domain trong một lần. <paramref name="expectedRevision"/> khác số phiên bản hiện tại
    /// (người khác vừa sửa) thì ném <see cref="ConfigConflictException"/>. null = không kiểm (công cụ dòng lệnh, vừa tạo).
    /// </summary>
    public void Update(string key, string name, string moduleId, string? taxCode, string? databaseLabel, bool enabled,
        IReadOnlyList<string> hosts, string? expectedRevision)
    {
        if (!TenantNames.IsValidModule(moduleId)) throw new ArgumentException("Mã module chỉ gồm 2–5 chữ cái (vd SY).", nameof(moduleId));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Thiếu tên công ty.", nameof(name));
        var normalized = NormalizeHosts(hosts);
        Change("tenant.update", key, $"name={name}; module={moduleId}; enabled={enabled}; hosts={string.Join(",", normalized)}", (c, tx, now) =>
        {
            var current = c.ExecuteScalar<long?>("SELECT revision FROM tenant WHERE tenant_key = @key", new { key }, tx)
                          ?? throw new InvalidOperationException($"Không có công ty '{key}'.");
            if (expectedRevision is not null && current.ToString(CultureInfo.InvariantCulture) != expectedRevision)
                throw new ConfigConflictException("Công ty này vừa được người khác sửa. Mở lại để xem bản mới nhất.");
            c.Execute("UPDATE tenant SET name = @name, module_id = @module, tax_code = @tax, database_label = @label, enabled = @e, updated_at = @now, revision = revision + 1 WHERE tenant_key = @key",
                new { name = name.Trim(), module = moduleId.ToUpperInvariant(), tax = Blank(taxCode), label = Blank(databaseLabel), e = enabled ? 1 : 0, now, key }, tx);
            c.Execute("DELETE FROM tenant_host WHERE tenant_key = @key", new { key }, tx);
            var sort = 0;
            foreach (var h in normalized)
                c.Execute("INSERT INTO tenant_host (host, tenant_key, sort) VALUES (@h, @key, @sort)", new { h, key, sort = sort++ }, tx);
        });
    }

    /// <summary>Kiểm và chuẩn hóa danh sách domain (bỏ trùng, bỏ dòng trống).</summary>
    public static IReadOnlyList<string> NormalizeHosts(IEnumerable<string> hosts)
    {
        var list = new List<string>();
        foreach (var raw in hosts)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!TenantNames.IsValidHost(raw)) throw new ArgumentException($"Tên miền '{raw.Trim()}' không hợp lệ.");
            var h = TenantNames.NormalizeHost(raw);
            if (!list.Contains(h, StringComparer.OrdinalIgnoreCase)) list.Add(h);
        }
        return list;
    }

    public bool HasSetting(string name)
    {
        using var c = db.OpenReadOnly();
        return c.ExecuteScalar<int>("SELECT COUNT(*) FROM setting WHERE name = @name AND value_protected IS NOT NULL", new { name }) > 0;
    }

    /// <summary>Dấu phiên bản của một cài đặt (thời điểm đặt lần cuối, "0" nếu chưa đặt), dùng làm <c>__version</c>.</summary>
    public string SettingStamp(string name)
    {
        using var c = db.OpenReadOnly();
        return c.ExecuteScalar<string?>("SELECT updated_at FROM setting WHERE name = @name", new { name }) ?? "0";
    }

    /// <summary>Đặt (hoặc xóa khi <paramref name="value"/> null) một cài đặt chung. Nhật ký không ghi giá trị.</summary>
    public void SetSetting(string name, string? value) =>
        Change(value is null ? "setting.clear" : "setting.set", name, null, (c, tx, now) =>
            c.Execute("INSERT INTO setting (name, value_protected, updated_at) VALUES (@name, @v, @now) " +
                      "ON CONFLICT(name) DO UPDATE SET value_protected = excluded.value_protected, updated_at = excluded.updated_at",
                new { name, v = value is null ? null : protector.ProtectSetting(value), now }, tx));

    public IReadOnlyList<AuditEntry> ReadAudit(int limit)
    {
        using var c = db.OpenReadOnly();
        return c.Query<(long Id, string At, string Actor, string Action, string? Target, string? Detail)>(
                "SELECT id, at, actor, action, target, detail FROM admin_audit ORDER BY id DESC LIMIT @limit", new { limit })
            .Select(r => new AuditEntry(r.Id, SqliteTenantStore.ParseTime(r.At)!.Value, r.Actor, r.Action, r.Target, r.Detail))
            .ToList();
    }

    /// <summary>Phiên bản cấu hình hiện tại (tăng mỗi lần sửa).</summary>
    public long Version()
    {
        using var c = db.OpenReadOnly();
        return c.ExecuteScalar<long>("SELECT version FROM config_meta WHERE id = 1");
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
