using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using WebErp.Config;
using WebErp.HostSync.Platform;

namespace WebErp.HostSync;

/// <summary>
/// <c>WebErp.HostSync.exe</c>:
/// <list type="bullet">
/// <item>không có lệnh / <c>run</c>: Windows Service (hoặc chạy trong console để thử);</item>
/// <item><c>once</c>: chạy đúng một vòng đồng bộ rồi thoát;</item>
/// <item><c>adopt &lt;domain&gt;...</c>: nhận quản lý domain đã cấu hình tay (erp, tinphong);</item>
/// <item><c>status</c>: in trạng thái các domain; <c>version</c>: in phiên bản.</item>
/// </list>
/// </summary>
public static class Program
{
    public const string ServiceName = "WebErpHostSync";
    public const string EventSource = "WebErp.HostSync";

    public static string Version { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    public static async Task<int> Main(string[] args)
    {
        var command = args.Length == 0 ? "run" : args[0].ToLowerInvariant();
        if (command == "version")
        {
            Console.WriteLine(Version);
            return 0;
        }
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("WebErp.HostSync chỉ chạy trên Windows (IIS).");
            return 2;
        }
        return await RunAsync(command, args.Skip(1).ToArray());
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunAsync(string command, string[] rest)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = command == "run" ? rest : [],
            ContentRootPath = AppContext.BaseDirectory,
        });
        var options = builder.Configuration.GetSection("WebErp:HostSync").Get<HostSyncOptions>() ?? new HostSyncOptions();
        var paths = WebErpPaths.From(builder.Configuration, AppContext.BaseDirectory);

        builder.Services.AddWindowsService(o => o.ServiceName = ServiceName);
        builder.Services.Configure<EventLogSettings>(s =>
        {
            s.SourceName = EventSource;
            s.LogName = "Application";
        });
        builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(paths.Root, "logs"), LogLevel.Information));

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ConfigDatabase>();
        builder.Services.AddSingleton(sp => new HostSyncStore(sp.GetRequiredService<ConfigDatabase>(), TimeProvider.System));
        builder.Services.AddSingleton<IIisAdmin, IisAdmin>();
        builder.Services.AddSingleton<ISslBindings, NetshSslBindings>();
        builder.Services.AddSingleton<IAcmeClient, WacsClient>();
        builder.Services.AddSingleton<ICertificateStore, WindowsCertificateStore>();
        builder.Services.AddSingleton<IDnsCheck, PublicDnsCheck>();
        builder.Services.AddSingleton<IHttpsProbe, LocalHttpsProbe>();
        builder.Services.AddSingleton(sp => new Reconciler(
            sp.GetRequiredService<HostSyncStore>(), sp.GetRequiredService<IIisAdmin>(), sp.GetRequiredService<ISslBindings>(),
            sp.GetRequiredService<IAcmeClient>(), sp.GetRequiredService<ICertificateStore>(), sp.GetRequiredService<IDnsCheck>(),
            sp.GetRequiredService<IHttpsProbe>(), options, TimeProvider.System, sp.GetRequiredService<ILogger<Reconciler>>())
        {
            AgentVersion = Version,
            StagedVersion = () => StagedVersion(paths),
        });
        if (command == "run") builder.Services.AddHostedService<HostSyncWorker>();

        using var app = builder.Build();
        var store = app.Services.GetRequiredService<HostSyncStore>();
        switch (command)
        {
            case "run":
                await app.RunAsync();
                return 0;
            case "once":
            {
                if (!store.Database.Exists) return Fail($"Chưa có {store.Database.Path}.");
                HostSyncWorker.EnsureSchema(store.Database);
                var s = await app.Services.GetRequiredService<Reconciler>().RunOnceAsync(CancellationToken.None);
                Console.WriteLine($"config v{s.ConfigVersion}: domain {s.Ensured}, them {s.Added}, go {s.Removed}, cho DNS {s.Pending}, loi {s.Errors}, cho xac nhan go {s.Blocked}, commit IIS {s.Committed}");
                return s.Errors > 0 ? 1 : 0;
            }
            case "adopt":
            {
                if (rest.Length == 0) return Fail("Cách dùng: WebErp.HostSync.exe adopt <domain> [domain...]");
                if (!store.Database.Exists) return Fail($"Chưa có {store.Database.Path}.");
                HostSyncWorker.EnsureSchema(store.Database);
                var rec = app.Services.GetRequiredService<Reconciler>();
                var code = 0;
                foreach (var host in rest)
                {
                    var error = rec.Adopt(host);
                    // ASCII: console quản trị trên VPS không hiện được tiếng Việt.
                    Console.WriteLine(error is null ? $"OK adopt {host}" : $"FAIL adopt {host}: {Ascii(error)}");
                    if (error is not null) code = 1;
                }
                return code;
            }
            case "status":
            {
                if (!store.Database.Exists) return Fail($"Chưa có {store.Database.Path}.");
                var meta = store.Meta();
                Console.WriteLine($"agent {meta.AgentVersion} heartbeat {meta.HeartbeatAt:O} config v{meta.AppliedVersion} staged {meta.StagedVersion}");
                foreach (var m in store.Managed())
                    Console.WriteLine($"{m.Host,-40} {m.State,-16} adopted={(m.Adopted ? 1 : 0)} cert={m.CertThumbprint} expires={m.CertExpires:yyyy-MM-dd} {Ascii(m.LastError ?? "")}");
                return 0;
            }
            default:
                return Fail("Lệnh: run | once | adopt <domain>... | status | version");
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(Ascii(message));
        return 2;
    }

    /// <summary>Bỏ dấu tiếng Việt cho console (dòng lệnh quản trị trên VPS hiển thị sai Unicode).</summary>
    internal static string Ascii(string s)
    {
        var d = s.Replace('đ', 'd').Replace('Đ', 'D').Normalize(System.Text.NormalizationForm.FormD);
        var b = new System.Text.StringBuilder(d.Length);
        foreach (var ch in d)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark && ch < 128) b.Append(ch);
        return b.ToString();
    }

    /// <summary>Phiên bản agent mới deploy chép vào <c>tools\hostsync-staging</c>, chờ quản trị viên cài (setup-hostsync.ps1).</summary>
    internal static string? StagedVersion(WebErpPaths paths)
    {
        var f = Path.Combine(paths.Root, "tools", "hostsync-staging", "version.txt");
        try
        {
            return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
