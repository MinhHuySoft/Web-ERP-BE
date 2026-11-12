using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;
using WebErp.Config;
using WebErp.Core.Auth;
using WebErp.Core.Menu;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Menu;
using WebErp.Erp.Security;
using WebErp.Host;
using WebErp.Host.Auth;
using WebErp.Host.Endpoints;
using WebErp.Host.Middleware;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

var options = builder.Configuration.GetSection("WebErp").Get<WebErpOptions>() ?? new WebErpOptions();
var paths = WebErpPaths.From(builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.Configure<WebErpOptions>(builder.Configuration.GetSection("WebErp"));
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddWebErpDataProtection(paths, options.DataProtectionCertificateThumbprint);

// Cấu hình công ty (SQLite, chỉ đọc).
builder.Services.AddSingleton<ConfigDatabase>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<SqliteTenantStore>();
builder.Services.AddSingleton<ITenantStore>(sp => sp.GetRequiredService<SqliteTenantStore>());

// DB ERP của từng công ty (chỉ đọc ở M1).
builder.Services.AddSingleton(builder.Configuration.GetSection("WebErp:Erp").Get<ErpOptions>() ?? new ErpOptions());
builder.Services.AddSingleton<ErpConnectionFactory>();
builder.Services.AddSingleton<IErpUsers, ErpUsers>();
builder.Services.AddSingleton<IErpMenu, ErpMenu>();
builder.Services.AddSingleton<MenuService>();

builder.Services.AddWebErpAuth(options);

var app = builder.Build();

if (options.Forwarded.Enabled)
{
    // Chỉ tin X-Forwarded-For/Proto từ proxy đã khai báo. Không dùng X-Forwarded-Host: host quyết định công ty.
    var fwd = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
    foreach (var ip in options.Forwarded.KnownProxies) fwd.KnownProxies.Add(IPAddress.Parse(ip));
    app.UseForwardedHeaders(fwd);
}

app.UseExceptionHandler(new ExceptionHandlerOptions { ExceptionHandler = ErrorHandling.HandleAsync });
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseMiddleware<SecurityHeaders>();
app.UseMiddleware<FrontendConfig>();

// FE cùng origin: tệp tĩnh của repo FE (bản đóng gói nằm ở wwwroot).
var feDir = string.IsNullOrWhiteSpace(options.FrontendDir)
    ? app.Environment.WebRootPath
    : Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, options.FrontendDir));
if (!string.IsNullOrEmpty(feDir) && Directory.Exists(feDir))
{
    var fe = new PhysicalFileProvider(feDir);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fe });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = fe });
}
else
{
    app.Logger.LogWarning("Không thấy thư mục FE '{Dir}'. Chỉ phục vụ /erp/* và /health.", feDir);
}

app.UseRouting();
app.UseMiddleware<TenantResolution>();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealth();
app.MapAuth();
app.MapSession();
app.MapScreens();

app.Run();

/// <summary>Cho WebApplicationFactory trong test.</summary>
public partial class Program;
