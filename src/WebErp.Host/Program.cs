using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using WebErp.Config;
using WebErp.Core.Auth;
using WebErp.Core.Menu;
using WebErp.Core.Metadata;
using WebErp.Core.Tenancy;
using WebErp.Erp.Connection;
using WebErp.Erp.Menu;
using WebErp.Erp.Metadata;
using WebErp.Erp.Screens;
using WebErp.Erp.Security;
using WebErp.Host;
using WebErp.Host.Auth;
using WebErp.Host.Endpoints;
using WebErp.Host.Middleware;
using WebErp.Web;

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
// Màn dựng từ metadata ERP (M2 danh sách, M3 xem chứng từ): chỉ đọc.
builder.Services.AddSingleton<ErpMetadata>();
builder.Services.AddSingleton<ErpPermissions>();
builder.Services.AddSingleton<IErpScreens, ErpScreens>();
builder.Services.AddSingleton<WebErp.Core.Print.IErpPrint, WebErp.Erp.Print.ErpPrint>();
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

// FE cùng origin: /config.js + tệp tĩnh của repo FE (bản đóng gói nằm ở wwwroot).
var frontend = new FrontendSettings(options.AppName, "/erp/login", "/erp/session", "/erp/menu", "/erp/home");
if (app.UseWebErpFrontend(frontend, options.FrontendDir) is null)
    app.Logger.LogWarning("Không thấy thư mục FE. Chỉ phục vụ /erp/* và /health.");

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
