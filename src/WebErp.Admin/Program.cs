using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebErp.Config;
using WebErp.Erp.Connection;
using WebErp.Erp.Diagnostics;

namespace WebErp.Admin;

/// <summary>
/// Công cụ quản trị cấu hình chạy trên VPS (M1). Trang quản trị trên web (TOTP) làm ở M5.
/// Chuỗi kết nối luôn đọc từ bàn phím (ẩn) hoặc stdin, không bao giờ từ tham số dòng lệnh (lộ trong lịch sử lệnh).
/// </summary>
public static class Program
{
    private const string Usage = """
        WebErp.Admin — quản trị cấu hình công ty (SQLite)

          init                                       tạo / nâng cấp tệp cấu hình
          tenant list                                liệt kê công ty, domain
          tenant add <mã> --name "<tên>" [--module SY] [--host <domain>]... [--tax-code <mst>] [--db-label <nhãn>]
                                                     thêm công ty, sau đó nhập chuỗi kết nối (ẩn)
          tenant set-connection <mã>                 đặt lại chuỗi kết nối (nhập ẩn hoặc qua stdin)
          tenant host-add <mã> <domain>              gắn thêm domain
          tenant host-remove <mã> <domain>           bỏ domain
          tenant enable <mã> | tenant disable <mã>
          tenant maintenance <mã> <số phút | off>    bật / tắt chế độ bảo trì (BE trả 503)
          tenant logout-all <mã>                     đăng xuất mọi người của công ty
          tenant test <mã>                           thử kết nối DB công ty (chỉ đọc thông tin hệ thống)
          backup [thư mục]                           sao lưu tệp cấu hình (VACUUM INTO) + thư mục khóa

        Tùy chọn chung: --root <thư mục dữ liệu> (mặc định lấy từ appsettings.json / biến môi trường WebErp__Root)
        """;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            var a = new Args(args);
            if (a.Positional.Count == 0 || a.Has("help")) { Console.WriteLine(Usage); return 0; }
            var env = Build(a);
            return await Run(env, a);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or ConfigNotInitializedException or FormatException
                                   or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine("Lỗi: " + ex.Message);
            return 1;
        }
    }

    private static async Task<int> Run(Env env, Args a)
    {
        var cmd = string.Join(' ', a.Positional.Take(2)).ToLowerInvariant();
        switch (a.Positional[0].ToLowerInvariant())
        {
            case "init":
                var applied = env.Writer.Initialize();
                Console.WriteLine(applied.Count == 0 ? "Tệp cấu hình đã ở phiên bản mới nhất." : "Đã chạy: " + string.Join(", ", applied.Select(m => m.Name)));
                Console.WriteLine("Tệp: " + env.Paths.ConfigDb);
                return 0;
            case "backup":
                var dir = a.Positional.Count > 1 ? a.Positional[1] : env.Paths.BackupDir;
                var file = env.Writer.Backup(dir);
                var keysCopy = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + "-keys");
                CopyDirectory(env.Paths.KeysDir, keysCopy);
                Console.WriteLine($"Đã sao lưu: {file}\nKhóa: {keysCopy}\nNhớ chép cả hai ra ngoài VPS, cùng tệp chứng chỉ bọc khóa (nếu có).");
                return 0;
        }

        var key = a.Positional.Count > 2 ? a.Positional[2] : null;
        switch (cmd)
        {
            case "tenant list":
                foreach (var t in env.Writer.List())
                {
                    var s = t.Tenant;
                    var state = !s.Enabled ? "TẮT" : s.InMaintenance(DateTimeOffset.UtcNow) ? $"bảo trì đến {s.MaintenanceUntil:yyyy-MM-dd HH:mm}Z" : "bật";
                    Console.WriteLine($"{s.Key,-16} {s.Name}  [{state}] module={s.ModuleId} kết nối={(t.HasConnection ? "đã đặt" : "CHƯA")}");
                    Console.WriteLine($"{"",-16} domain: {(t.Hosts.Count == 0 ? "(chưa có)" : string.Join(", ", t.Hosts))}");
                }
                return 0;
            case "tenant add":
                Need(key);
                var name = a.Get("name") ?? throw new ArgumentException("Thiếu --name.");
                // Đọc và kiểm chuỗi kết nối TRƯỚC, để nhập sai không để lại công ty dở dang.
                var connection = ReadConnection("Chuỗi kết nối SQL Server của công ty: ");
                env.Writer.AddTenant(key!, name, a.Get("module") ?? "SY", a.Get("tax-code"), a.Get("db-label"));
                foreach (var h in a.All("host")) env.Writer.AddHost(key!, h);
                env.Writer.SetConnection(key!, connection);
                Console.WriteLine($"Đã thêm công ty {key}. Chạy 'tenant test {key}' để thử kết nối.");
                return 0;
            case "tenant set-connection":
                Need(key);
                env.Writer.SetConnection(key!, ReadConnection("Chuỗi kết nối SQL Server mới: "));
                Console.WriteLine("Đã cập nhật chuỗi kết nối.");
                return 0;
            case "tenant host-add":
                Need(key);
                env.Writer.AddHost(key!, Arg(a, 3, "domain"));
                Console.WriteLine("Đã gắn domain.");
                return 0;
            case "tenant host-remove":
                Need(key);
                env.Writer.RemoveHost(key!, Arg(a, 3, "domain"));
                Console.WriteLine("Đã bỏ domain.");
                return 0;
            case "tenant enable":
            case "tenant disable":
                Need(key);
                env.Writer.SetEnabled(key!, cmd == "tenant enable");
                Console.WriteLine("Đã cập nhật.");
                return 0;
            case "tenant maintenance":
                Need(key);
                var v = Arg(a, 3, "số phút hoặc off");
                DateTimeOffset? until = v.Equals("off", StringComparison.OrdinalIgnoreCase) ? null
                    : DateTimeOffset.UtcNow.AddMinutes(int.Parse(v, NumberStyles.None, CultureInfo.InvariantCulture));
                env.Writer.SetMaintenance(key!, until);
                Console.WriteLine(until is null ? "Đã tắt bảo trì." : $"Bảo trì đến {until:yyyy-MM-dd HH:mm}Z.");
                return 0;
            case "tenant logout-all":
                Need(key);
                env.Writer.LogoutAll(key!);
                Console.WriteLine("Mọi phiên của công ty sẽ bị từ chối trong vài giây tới.");
                return 0;
            case "tenant test":
                Need(key);
                var tenant = await env.Store.GetAsync(key!, CancellationToken.None) ?? throw new InvalidOperationException($"Không có công ty '{key}'.");
                var r = await new ErpProbe(new ErpConnectionFactory(env.Store, new ErpOptions())).ProbeAsync(tenant, CancellationToken.None);
                if (!r.Ok) { Console.WriteLine($"KHÔNG kết nối được ({r.RoundTripMs} ms): {r.Error}"); return 2; }
                Console.WriteLine($"Kết nối được: DB {r.Database}, SQL Server {r.ServerVersion}, khứ hồi {r.RoundTripMs} ms.");
                var stored = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(await env.Store.GetConnectionStringAsync(key!, CancellationToken.None) ?? "");
                Console.WriteLine($"Mã hóa: Encrypt={stored.Encrypt}, TrustServerCertificate={stored.TrustServerCertificate}"
                                  + (stored.TrustServerCertificate ? " (chưa kiểm chứng chỉ của máy chủ SQL)" : ""));
                Console.WriteLine($"SY_User: {(r.HasUserTable ? "có" : "KHÔNG")}; {tenant.ModuleId}_GetUserMenuFnc: {(r.HasMenuFunction ? "có" : "KHÔNG")}");
                return r.HasUserTable && r.HasMenuFunction ? 0 : 3;
            default:
                Console.Error.WriteLine("Lệnh không hợp lệ.\n");
                Console.WriteLine(Usage);
                return 1;
        }
    }

    private sealed record Env(WebErpPaths Paths, ConfigWriter Writer, SqliteTenantStore Store);

    private static Env Build(Args a)
    {
        var baseDir = AppContext.BaseDirectory;
        var config = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .AddInMemoryCollection(a.Get("root") is { } root ? new Dictionary<string, string?> { ["WebErp:Root"] = root } : [])
            .Build();
        var paths = WebErpPaths.From(config, Directory.GetCurrentDirectory());
        var services = new ServiceCollection();
        services.AddWebErpDataProtection(paths, config["WebErp:DataProtectionCertificateThumbprint"]);
        var provider = services.BuildServiceProvider().GetRequiredService<IDataProtectionProvider>();
        var db = new ConfigDatabase(paths);
        var protector = new SecretProtector(provider);
        var actor = $"{Environment.UserName}@{Environment.MachineName} (cli)";
        return new Env(paths, new ConfigWriter(db, protector, TimeProvider.System, actor), new SqliteTenantStore(db, protector, TimeProvider.System));
    }

    /// <summary>Đọc chuỗi kết nối (ẩn), chuẩn hóa như BE sẽ dùng, báo các điểm đã đổi. Không bao giờ in lại chuỗi.</summary>
    private static string ReadConnection(string prompt)
    {
        var raw = ReadSecret(prompt);
        NormalizedConnection n;
        try
        {
            n = ErpConnectionStrings.Normalize(raw, new ErpOptions());
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            throw new ArgumentException("Chuỗi kết nối không đọc được (sai cú pháp hoặc từ khóa lạ).");
        }
        if (n.Database is null) throw new ArgumentException("Chuỗi kết nối thiếu tên DB: thêm Database=<tên DB> (hoặc Initial Catalog=<tên DB>).");
        Console.WriteLine($"DB: {n.Database}");
        foreach (var c in n.Changes) Console.WriteLine("  • " + c);
        return n.ConnectionString;
    }

    private static string ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine() ?? "";
        Console.Write(prompt);
        var sb = new StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) break;
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.WriteLine();
        return sb.ToString();
    }

    private static void Need(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Thiếu mã công ty.");
    }

    private static string Arg(Args a, int index, string name) =>
        a.Positional.Count > index ? a.Positional[index] : throw new ArgumentException($"Thiếu {name}.");

    private static void CopyDirectory(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
    }

    /// <summary>Tham số dòng lệnh đơn giản: vị trí + <c>--tên giá-trị</c> (lặp được).</summary>
    private sealed class Args
    {
        private readonly List<(string Name, string? Value)> _options = [];

        public List<string> Positional { get; } = [];

        public Args(string[] args)
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    var name = args[i][2..];
                    var value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
                    _options.Add((name, value));
                }
                else
                {
                    Positional.Add(args[i]);
                }
            }
        }

        public bool Has(string name) => _options.Any(o => o.Name == name);

        public string? Get(string name) => _options.LastOrDefault(o => o.Name == name).Value;

        public IEnumerable<string> All(string name) => _options.Where(o => o.Name == name && o.Value is not null).Select(o => o.Value!);
    }
}
