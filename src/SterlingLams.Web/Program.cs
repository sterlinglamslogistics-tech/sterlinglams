using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;
using SterlingLams.Web.Data;
using SterlingLams.Web.Infrastructure.Extensions;
using SterlingLams.Web.Models.Domain;

var builder = WebApplication.CreateBuilder(args);

// ─── Serilog ────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/sterlinglams-.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

// ─── Database ───────────────────────────────────────────────────────────────
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// ─── Identity ───────────────────────────────────────────────────────────────
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = true;
    options.Password.RequiredLength = 8;
    options.Password.RequireUppercase = false;
    options.SignIn.RequireConfirmedEmail = false;
})
.AddEntityFrameworkStores<ApplicationDbContext>()
.AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.AccessDeniedPath = "/Account/AccessDenied";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});

// ─── Caching ────────────────────────────────────────────────────────────────
var redisConn = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redisConn))
    builder.Services.AddStackExchangeRedisCache(opts => opts.Configuration = redisConn);
else
    builder.Services.AddMemoryCache();

// ─── Session ────────────────────────────────────────────────────────────────
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// ─── Application Services ───────────────────────────────────────────────────
builder.Services.AddSterlingLamsServices(builder.Configuration);

// ─── Background Services ─────────────────────────────────────────────────────
builder.Services.AddHostedService<SterlingLams.Web.Infrastructure.InventorySyncHostedService>();

// ─── MVC ────────────────────────────────────────────────────────────────────
builder.Services.AddControllersWithViews()
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);

builder.Services.AddHttpContextAccessor();

// ─── Forwarded Headers ───────────────────────────────────────────────────────
// Honour X-Forwarded-Proto/-For from the reverse proxy so Request.Scheme and the
// generated callback URLs are correct (HTTPS) behind a load balancer.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// ─── Middleware Pipeline ─────────────────────────────────────────────────────
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "areas",
    pattern: "{area:exists}/{controller=Home}/{action=Index}/{id?}");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapControllers(); // API controllers (WebhooksController)

// ─── DB Initialisation ───────────────────────────────────────────────────────
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<ApplicationDbContext>>();

    try
    {
        // Apply migrations on startup in every environment, so dev exercises the same
        // schema path as production and migration bugs surface locally rather than in prod.
        await db.Database.MigrateAsync();
        logger.LogInformation("Database migrations applied.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Database initialisation failed. Check your connection string.");
        throw; // Fail fast — app cannot run without DB
    }
}

// Seed roles, stores, and categories (all environments)
await SterlingLams.Web.Infrastructure.SeedData.SeedAsync(app.Services);

// One-off Odoo connectivity diagnostics. Runs only when ODOO_DIAG=1, then exits.
if (Environment.GetEnvironmentVariable("ODOO_DIAG") == "1")
{
    var diagLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await SterlingLams.Web.Infrastructure.Odoo.OdooDiagnostics.RunAsync(app.Services, diagLogger);
    return; // one-shot command
}

// One-off: inspect product/warehouse routing. Runs only when ODOO_INSPECT=1.
if (Environment.GetEnvironmentVariable("ODOO_INSPECT") == "1")
{
    var l = app.Services.GetRequiredService<ILogger<Program>>();
    await SterlingLams.Web.Infrastructure.Odoo.OdooInspectRoutes.RunAsync(app.Services, l);
    return; // one-shot command
}

// One-off: force direct delivery routing on store warehouses. Runs only when ODOO_ROUTE_FIX=1.
if (Environment.GetEnvironmentVariable("ODOO_ROUTE_FIX") == "1")
{
    var l = app.Services.GetRequiredService<ILogger<Program>>();
    await SterlingLams.Web.Infrastructure.Odoo.OdooRouteFix.RunAsync(app.Services, l);
    return; // one-shot command
}

// One-off: simulate checkout backend + show stock before/after. Runs only when ODOO_SIM_CHECKOUT=1.
if (Environment.GetEnvironmentVariable("ODOO_SIM_CHECKOUT") == "1")
{
    var l = app.Services.GetRequiredService<ILogger<Program>>();
    await SterlingLams.Web.Infrastructure.Odoo.OdooSimCheckout.RunAsync(app.Services, l);
    return; // one-shot command
}

// One-off: create a test sale order in Odoo. Runs only when ODOO_TEST_ORDER=1.
if (Environment.GetEnvironmentVariable("ODOO_TEST_ORDER") == "1")
{
    var l = app.Services.GetRequiredService<ILogger<Program>>();
    await SterlingLams.Web.Infrastructure.Odoo.OdooTestOrder.RunAsync(app.Services, l);
    return; // one-shot command
}

// One-off: verify Odoo→website stock sync. Runs only when ODOO_VERIFY_SYNC=1.
if (Environment.GetEnvironmentVariable("ODOO_VERIFY_SYNC") == "1")
{
    var l = app.Services.GetRequiredService<ILogger<Program>>();
    await SterlingLams.Web.Infrastructure.Odoo.OdooVerifySync.RunAsync(app.Services, l);
    return; // one-shot command
}

// One-off: seed Odoo with current per-store stock. Runs only when ODOO_PUSH_STOCK=1.
if (Environment.GetEnvironmentVariable("ODOO_PUSH_STOCK") == "1")
{
    var l = app.Services.GetRequiredService<ILogger<Program>>();
    await SterlingLams.Web.Infrastructure.Odoo.OdooPushStock.RunAsync(app.Services, l);
    return; // one-shot command
}

// One-off: push website products into Odoo. Runs only when ODOO_EXPORT_PRODUCTS=1.
if (Environment.GetEnvironmentVariable("ODOO_EXPORT_PRODUCTS") == "1")
{
    var l = app.Services.GetRequiredService<ILogger<Program>>();
    await SterlingLams.Web.Infrastructure.Odoo.OdooExportProducts.RunAsync(app.Services, l);
    return; // one-shot command
}

// One-off: provision Odoo warehouses for each store. Runs only when ODOO_PROVISION=1.
if (Environment.GetEnvironmentVariable("ODOO_PROVISION") == "1")
{
    var l = app.Services.GetRequiredService<ILogger<Program>>();
    await SterlingLams.Web.Infrastructure.Odoo.OdooProvisionStores.RunAsync(app.Services, l);
    return; // one-shot command
}

// One-off category merge maintenance. Runs only when CATEGORY_MERGE is set, then exits.
var mergeSpec = Environment.GetEnvironmentVariable("CATEGORY_MERGE");
if (!string.IsNullOrWhiteSpace(mergeSpec))
{
    var mergeLogger = app.Services.GetRequiredService<ILogger<Program>>();
    await SterlingLams.Web.Infrastructure.Maintenance.CategoryMerge.RunAsync(app.Services, mergeSpec, mergeLogger);
    return; // one-shot command
}

// One-off WooCommerce (.wpress) product import. Runs only when WP_IMPORT=1, then exits.
if (Environment.GetEnvironmentVariable("WP_IMPORT") == "1")
{
    var importLogger = app.Services.GetRequiredService<ILogger<Program>>();
    var importDir = Environment.GetEnvironmentVariable("WP_IMPORT_DIR")
        ?? Path.Combine(builder.Environment.ContentRootPath, "..", "..", ".wpimport");
    await SterlingLams.Web.Infrastructure.WordpressImport.WordpressProductImporter
        .RunAsync(app.Services, importDir, importLogger);
    return; // one-shot command; do not start the web server
}

await app.RunAsync();
