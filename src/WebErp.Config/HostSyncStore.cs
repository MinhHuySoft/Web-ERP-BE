using System.Globalization;
using Dapper;
using WebErp.Core.Tenancy;

namespace WebErp.Config;

/// <summary>Trạng thái một domain do agent đồng bộ IIS quản lý (bảng <c>managed_host</c>).</summary>
public sealed record ManagedHost(
    string Host,
    string State,
    bool Adopted = false,
    string? CertThumbprint = null,
    DateTimeOffset? CertExpires = null,
    string? LastError = null,
    int Attempts = 0,
    DateTimeOffset? NextAttemptAt = null,
    bool RemoveApproved = false,
    DateTimeOffset? LastCheckedAt = null,
    DateTimeOffset? UpdatedAt = null);

/// <summary>Domain mong muốn: có trong <c>tenant_host</c> của ít nhất một công ty (kể cả công ty đang tắt).</summary>
public sealed record DesiredHost(string Host, IReadOnlyList<string> TenantKeys, bool AnyEnabled);

public sealed record HostSyncMeta(DateTimeOffset? RequestedAt, DateTimeOffset? HeartbeatAt, long? AppliedVersion, string? AgentVersion,
    string? StagedVersion, string? LastError);

/// <summary>Mã trạng thái lưu trong DB (tiếng Anh) và chữ hiển thị.</summary>
public static class HostStates
{
    public const string PendingDns = "pending_dns";
    public const string Issuing = "issuing";
    public const string Active = "active";
    public const string Error = "error";
    public const string Removing = "removing";
    public const string RemovalBlocked = "removal_blocked";

    /// <summary>Domain đã có trên site IIS khác: agent không làm gì, khi bỏ thì chỉ xóa dòng, không đụng IIS / chứng chỉ.</summary>
    public const string Conflict = "conflict";

    public static string Label(string? state) => state switch
    {
        Conflict => "Trùng site khác",
        PendingDns => "Chờ DNS",
        Issuing => "Đang cấp chứng chỉ",
        Active => "Hoạt động",
        Error => "Lỗi",
        Removing => "Đang gỡ",
        RemovalBlocked => "Chờ xác nhận gỡ",
        null => "Chờ agent",
        _ => state,
    };

    /// <summary>Tông màu cho ô trạng thái (chuẩn ERP-HTML).</summary>
    public static string Tone(string? state) => state switch
    {
        Active => "success",
        Error or Conflict => "danger",
        RemovalBlocked => "warning",
        _ => "info",
    };
}

/// <summary>
/// Đọc / ghi trạng thái đồng bộ IIS trong <c>config.db</c>. Dùng chung cho agent (LocalSystem) và trang quản trị.
/// Không bao giờ tăng <c>config_meta.version</c>: trạng thái IIS không phải cấu hình công ty, Host không cần nạp lại.
/// </summary>
public sealed class HostSyncStore(ConfigDatabase db, TimeProvider time)
{
    public const string AgentActor = "hostsync";

    public ConfigDatabase Database => db;

    public long ConfigVersion()
    {
        using var c = db.OpenReadOnly();
        return c.ExecuteScalar<long>("SELECT version FROM config_meta WHERE id = 1");
    }

    public IReadOnlyList<DesiredHost> Desired()
    {
        using var c = db.OpenReadOnly();
        return c.Query<(string Host, string Key, long Enabled)>(
                "SELECT h.host, h.tenant_key, t.enabled FROM tenant_host h JOIN tenant t ON t.tenant_key = h.tenant_key ORDER BY h.host, h.sort")
            .GroupBy(r => TenantNames.NormalizeHost(r.Host), StringComparer.OrdinalIgnoreCase)
            .Select(g => new DesiredHost(g.Key, g.Select(r => r.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList(), g.Any(r => r.Enabled != 0)))
            .ToList();
    }

    public IReadOnlyList<ManagedHost> Managed()
    {
        using var c = db.OpenReadOnly();
        return c.Query<Row>("SELECT host, state, adopted, cert_thumbprint, cert_expires, last_error, attempts, next_attempt_at, remove_approved, last_checked_at, updated_at FROM managed_host ORDER BY host")
            .Select(r => r.ToModel()).ToList();
    }

    public ManagedHost? Get(string host) => Managed().FirstOrDefault(m => string.Equals(m.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Thêm hoặc cập nhật một domain (agent).</summary>
    public void Save(ManagedHost h)
    {
        using var c = db.OpenReadWrite();
        c.Execute("""
            INSERT INTO managed_host (host, state, adopted, cert_thumbprint, cert_expires, last_error, attempts, next_attempt_at, remove_approved, last_checked_at, updated_at)
            VALUES (@host, @state, @adopted, @thumb, @expires, @error, @attempts, @next, @approved, @checked, @now)
            ON CONFLICT(host) DO UPDATE SET state = excluded.state, adopted = excluded.adopted, cert_thumbprint = excluded.cert_thumbprint,
              cert_expires = excluded.cert_expires, last_error = excluded.last_error, attempts = excluded.attempts, next_attempt_at = excluded.next_attempt_at,
              remove_approved = excluded.remove_approved, last_checked_at = excluded.last_checked_at, updated_at = excluded.updated_at
            """,
            new
            {
                host = h.Host, state = h.State, adopted = h.Adopted ? 1 : 0, thumb = h.CertThumbprint, expires = Iso(h.CertExpires), error = Short(h.LastError),
                attempts = h.Attempts, next = Iso(h.NextAttemptAt), approved = h.RemoveApproved ? 1 : 0, @checked = Iso(h.LastCheckedAt), now = Iso(time.GetUtcNow()),
            });
    }

    public void Delete(string host)
    {
        using var c = db.OpenReadWrite();
        c.Execute("DELETE FROM managed_host WHERE host = @host", new { host });
    }

    /// <summary>Ghi nhật ký thao tác (không tăng phiên bản cấu hình). Không bao giờ ghi bí mật.</summary>
    public void Audit(string actor, string action, string? target, string? detail)
    {
        using var c = db.OpenReadWrite();
        c.Execute("INSERT INTO admin_audit (at, actor, action, target, detail) VALUES (@at, @actor, @action, @target, @detail)",
            new { at = Iso(time.GetUtcNow()), actor, action, target, detail = Short(detail) });
    }

    public HostSyncMeta Meta()
    {
        using var c = db.OpenReadOnly();
        var r = c.QuerySingleOrDefault<(string? Req, string? Beat, long? Applied, string? Agent, string? Staged, string? Error)>(
            "SELECT requested_at, heartbeat_at, applied_version, agent_version, staged_version, last_error FROM hostsync_meta WHERE id = 1");
        return new HostSyncMeta(Parse(r.Req), Parse(r.Beat), r.Applied, r.Agent, r.Staged, r.Error);
    }

    /// <summary>Agent báo đã chạy xong một vòng.</summary>
    public void Heartbeat(long appliedVersion, string agentVersion, string? stagedVersion, string? lastError)
    {
        using var c = db.OpenReadWrite();
        c.Execute("UPDATE hostsync_meta SET heartbeat_at = @now, applied_version = @v, agent_version = @agent, staged_version = @staged, last_error = @error WHERE id = 1",
            new { now = Iso(time.GetUtcNow()), v = appliedVersion, agent = agentVersion, staged = stagedVersion, error = Short(lastError) });
    }

    /// <summary>Quản trị viên bấm "Đồng bộ lại ngay".</summary>
    public void RequestSync(string actor)
    {
        using var c = db.OpenReadWrite();
        using var tx = c.BeginTransaction();
        var now = Iso(time.GetUtcNow());
        c.Execute("UPDATE hostsync_meta SET requested_at = @now WHERE id = 1", new { now }, tx);
        c.Execute("INSERT INTO admin_audit (at, actor, action, target, detail) VALUES (@now, @actor, 'hostsync.request', NULL, NULL)", new { now, actor }, tx);
        tx.Commit();
    }

    /// <summary>"Thử lại" một domain: bỏ thời gian chờ, thử ngay ở vòng kế tiếp. Trả false nếu domain chưa có trong bảng.</summary>
    public bool Retry(string actor, string host)
    {
        using var c = db.OpenReadWrite();
        using var tx = c.BeginTransaction();
        var now = Iso(time.GetUtcNow());
        var n = c.Execute("UPDATE managed_host SET attempts = 0, next_attempt_at = NULL, updated_at = @now WHERE host = @host", new { host, now }, tx);
        c.Execute("UPDATE hostsync_meta SET requested_at = @now WHERE id = 1", new { now }, tx);
        c.Execute("INSERT INTO admin_audit (at, actor, action, target, detail) VALUES (@now, @actor, 'hostsync.retry', @host, NULL)", new { now, actor, host }, tx);
        tx.Commit();
        return n > 0;
    }

    /// <summary>Quản trị viên xác nhận gỡ các domain đang bị chặn vì vượt ngưỡng gỡ hàng loạt.</summary>
    public int ApproveRemoval(string actor, IReadOnlyList<string> hosts)
    {
        using var c = db.OpenReadWrite();
        using var tx = c.BeginTransaction();
        var now = Iso(time.GetUtcNow());
        var n = 0;
        foreach (var host in hosts)
            n += c.Execute("UPDATE managed_host SET remove_approved = 1, updated_at = @now WHERE host = @host AND state = @blocked",
                new { host, now, blocked = HostStates.RemovalBlocked }, tx);
        c.Execute("UPDATE hostsync_meta SET requested_at = @now WHERE id = 1", new { now }, tx);
        c.Execute("INSERT INTO admin_audit (at, actor, action, target, detail) VALUES (@now, @actor, 'hostsync.approve-removal', NULL, @detail)",
            new { now, actor, detail = string.Join(",", hosts) }, tx);
        tx.Commit();
        return n;
    }

    private static string? Iso(DateTimeOffset? t) => t?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Parse(string? s) => SqliteTenantStore.ParseTime(s);

    /// <summary>Lý do lỗi ngắn gọn, một dòng.</summary>
    private static string? Short(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var one = string.Join(' ', s.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return one.Length <= 500 ? one : one[..500];
    }

    private sealed record Row(string host, string state, long adopted, string? cert_thumbprint, string? cert_expires, string? last_error, long attempts,
        string? next_attempt_at, long remove_approved, string? last_checked_at, string updated_at)
    {
        public ManagedHost ToModel() => new(host, state, adopted != 0, cert_thumbprint, Parse(cert_expires), last_error, (int)attempts, Parse(next_attempt_at),
            remove_approved != 0, Parse(last_checked_at), Parse(updated_at));
    }
}
