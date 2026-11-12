using System.Reflection;
using WebErp.Config;
using WebErp.Core.Auth;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Security;
using WebErp.Web;

namespace WebErp.Admin.Web;

/// <summary>
/// Trang quản trị trên web (M5): site IIS và app pool riêng, chỉ app pool này được ghi <c>config.db</c>.
/// Khởi động là tự chạy migration cấu hình, nên không cần gõ <c>WebErp.Admin init</c>.
/// </summary>
public static class AdminWeb
{
    public static string Version { get; } =
        typeof(AdminWeb).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);
        var section = builder.Configuration.GetSection("WebErp");
        var options = section.Get<AdminOptions>() ?? new AdminOptions();
        options.Hosts = builder.Configuration.GetSection("WebErp:Admin:Hosts").Get<string[]>() ?? [];
        builder.Services.Configure<AdminOptions>(o =>
        {
            section.Bind(o);
            o.Hosts = options.Hosts;
        });

        var paths = WebErpPaths.From(builder.Configuration, builder.Environment.ContentRootPath);
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddWebErpDataProtection(paths, options.DataProtectionCertificateThumbprint);
        builder.Services.AddSingleton<ConfigDatabase>();
        builder.Services.AddSingleton<SecretProtector>();
        builder.Services.AddSingleton<SqliteTenantStore>();
        builder.Services.AddSingleton<AdminAccountStore>();
        builder.Services.AddSingleton(builder.Configuration.GetSection("WebErp:Erp").Get<ErpOptions>() ?? new ErpOptions());
        // Đọc SY_User của công ty để thử hằng mật khẩu trước khi lưu (Cài đặt chung).
        builder.Services.AddSingleton<ITenantStore>(sp => sp.GetRequiredService<SqliteTenantStore>());
        builder.Services.AddSingleton<ErpConnectionFactory>();
        builder.Services.AddSingleton<IErpUsers, ErpUsers>();
        builder.Services.AddScoped<TenantEndpoints.Deps>();
        builder.Services.AddAdminAuth(options);

        var app = builder.Build();

        // Tạo / nâng cấp tệp cấu hình (chỉ tiến trình quản trị có quyền ghi).
        var applied = new ConfigWriter(app.Services.GetRequiredService<ConfigDatabase>(), app.Services.GetRequiredService<SecretProtector>(),
            TimeProvider.System, "khởi động trang quản trị").Initialize();
        if (applied.Count > 0) app.Logger.LogWarning("Đã nâng cấp tệp cấu hình: {Migrations}", string.Join(", ", applied.Select(m => m.Name)));

        app.UseMiddleware<AdminGate>();
        app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = ErrorHandling.HandleAsync });
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        app.UseMiddleware<SecurityHeaders>();
        var frontend = new FrontendSettings("Quản trị ERP Web", "/admin/login", "/admin/session", "/admin/menu", "/admin/home");
        if (app.UseWebErpFrontend(frontend, options.FrontendDir) is null)
            app.Logger.LogWarning("Không thấy thư mục FE. Trang quản trị chỉ phục vụ /admin/* và /health.");

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGet("/health", (AdminAccountStore accounts) =>
            Results.Json(new { status = "ok", version = Version, admins = accounts.Count() }));
        app.MapAccount();
        app.MapTenants();
        app.MapSettings();
        return app;
    }
}
