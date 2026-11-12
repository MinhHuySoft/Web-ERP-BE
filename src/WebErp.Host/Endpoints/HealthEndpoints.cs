using System.Reflection;
using WebErp.Config;

namespace WebErp.Host.Endpoints;

/// <summary>
/// <c>/health</c>: tiến trình đang chạy và đúng phiên bản nào (script triển khai so phiên bản này). Luôn 200 khi tiến trình
/// sống, kể cả chưa có cấu hình, để lần triển khai đầu (trước <c>WebErp.Admin init</c>) không bị coi là hỏng.
/// <c>/health/ready</c>: 503 khi chưa đọc được cấu hình (dùng cho load balancer sau này, tài liệu 05).
/// </summary>
public static class HealthEndpoints
{
    public static string Version { get; } =
        typeof(HealthEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    public static void MapHealth(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (SqliteTenantStore store, CancellationToken ct) =>
        {
            var config = await ConfigState(store, ct);
            return Results.Json(new { status = config.Ok ? "ok" : "degraded", version = Version, config = new { ok = config.Ok, version = config.Version, error = config.Error } });
        });
        app.MapGet("/health/ready", async (SqliteTenantStore store, CancellationToken ct) =>
        {
            var config = await ConfigState(store, ct);
            return config.Ok ? Results.Json(new { status = "ok", version = Version }) : Results.Json(new { status = "not-ready", error = config.Error }, statusCode: 503);
        });
    }

    private static async Task<(bool Ok, long? Version, string? Error)> ConfigState(SqliteTenantStore store, CancellationToken ct)
    {
        try
        {
            return (true, await store.GetVersionAsync(ct), null);
        }
        catch (ConfigNotInitializedException)
        {
            return (false, null, "chưa khởi tạo cấu hình");
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return (false, null, "không đọc được cấu hình");
        }
    }
}
