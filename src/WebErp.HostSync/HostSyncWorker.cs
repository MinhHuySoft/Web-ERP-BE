using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WebErp.Config;

namespace WebErp.HostSync;

/// <summary>
/// Vòng lặp của service: mỗi <see cref="HostSyncOptions.PollSeconds"/> giây xem <c>config_meta.version</c> hoặc nút "Đồng bộ lại ngay"
/// có đổi không; đổi thì chạy một vòng, không đổi thì vẫn chạy đủ một vòng mỗi <see cref="HostSyncOptions.FullSyncSeconds"/> giây.
/// </summary>
public sealed class HostSyncWorker(Reconciler reconciler, HostSyncStore store, HostSyncOptions options, TimeProvider time, ILogger<HostSyncWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("WebErp.HostSync {Version} chạy: site {Site}, IP {Ip}.", reconciler.AgentVersion, options.SiteName, options.PublicIp);
        (long Version, DateTimeOffset? Requested)? last = null;
        var lastRun = DateTimeOffset.MinValue;
        var schemaReady = false;
        var warnedMissing = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!store.Database.Exists)
                {
                    if (!warnedMissing) log.LogWarning("Chưa có {Path}: chờ trang quản trị tạo tệp cấu hình.", store.Database.Path);
                    warnedMissing = true;
                }
                else
                {
                    if (!schemaReady) schemaReady = EnsureSchema(store.Database);
                    var stamp = (store.ConfigVersion(), store.Meta().RequestedAt);
                    var now = time.GetUtcNow();
                    if (stamp != last || now - lastRun >= TimeSpan.FromSeconds(options.FullSyncSeconds))
                    {
                        lastRun = now;
                        var s = await reconciler.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                        last = stamp;
                        if (s.Committed || s.Added > 0 || s.Removed > 0)
                            log.LogInformation("Đồng bộ xong (cấu hình v{Version}): thêm {Added}, gỡ {Removed}, chờ DNS {Pending}, lỗi {Errors}, chờ xác nhận gỡ {Blocked}.",
                                s.ConfigVersion, s.Added, s.Removed, s.Pending, s.Errors, s.Blocked);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Vòng đồng bộ IIS lỗi.");
                try
                {
                    store.Heartbeat(store.ConfigVersion(), reconciler.AgentVersion, reconciler.StagedVersion(), ex.Message);
                }
                catch (Exception inner) when (inner is not OperationCanceledException)
                {
                    log.LogDebug(inner, "Không ghi được heartbeat.");
                }
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Nâng schema cấu hình nếu trang quản trị chưa kịp chạy migration 003 (agent có quyền ghi).</summary>
    internal static bool EnsureSchema(ConfigDatabase db)
    {
        using var c = db.OpenReadWrite();
        if (ConfigMigrator.CurrentVersion(c) >= ConfigMigrator.RequiredVersion) return true;
        ConfigMigrator.Migrate(c);
        return true;
    }
}
