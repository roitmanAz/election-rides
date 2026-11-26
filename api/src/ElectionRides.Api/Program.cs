using System.Security.Claims;
using System.Threading.RateLimiting;
using ElectionRides.Api.Auth;
using ElectionRides.Api.CityScreens;
using ElectionRides.Api.Common;
using ElectionRides.Api.Dispatch;
using ElectionRides.Api.Domain;
using ElectionRides.Api.DriverApp;
using ElectionRides.Api.Data;
using ElectionRides.Api.Email;
using ElectionRides.Api.GovData;
using ElectionRides.Api.Intake;
using ElectionRides.Api.Maps;
using ElectionRides.Api.SystemAdmin;
using ElectionRides.Api.VoterBase;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Db"), npg => npg.UseNetTopologySuite()));

// Email: development keeps messages in the log. Amazon SES is added with the cloud stage.
builder.Services.AddSingleton<DevEmailSender>();
builder.Services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<DevEmailSender>());
builder.Services.AddScoped<Invitations>();
// Address lookup: free OpenStreetMap lookup when configured (development); otherwise every address is
// saved as an approximate location. Google can replace it before going live.
builder.Services.AddHttpClient("nominatim", c =>
{
    c.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("election-rides-dev/0.1");
    c.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddSingleton<IGeocoder>(sp => sp.GetRequiredService<IConfiguration>()["Geocoding:Provider"] == "nominatim"
    ? new NominatimGeocoder(sp.GetRequiredService<IHttpClientFactory>().CreateClient("nominatim"))
    : new NoGeocoder());
// Driving routes for the dispatcher's map (OSRM; the address is in Routing:BaseUrl).
builder.Services.AddHttpClient("routing", c =>
{
    c.DefaultRequestHeaders.UserAgent.ParseAdd("election-rides-dev/0.1");
    c.Timeout = TimeSpan.FromSeconds(6);
});
builder.Services.AddSignalR();
// Cookie keys in the database: a hosted server that sleeps and restarts keeps everyone signed in.
builder.Services.AddDataProtection().SetApplicationName("election-rides").PersistKeysToDbContext<AppDbContext>();
// Behind a hosting proxy (HTTPS ends there): take the caller's address and scheme from its headers.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddScoped<VoterBaseService>();
builder.Services.AddSingleton<AutoDeletion>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AutoDeletion>());
builder.Services.AddSingleton<CityEvents>();

// Official lists from data.gov.il (localities and streets).
builder.Services.AddHttpClient("gov", c =>
{
    c.BaseAddress = new Uri("https://data.gov.il/");
    c.Timeout = TimeSpan.FromSeconds(60);
});
// One shared client, so the localities list is fetched once a day.
builder.Services.AddSingleton<IGovData>(sp => new GovDataClient(sp.GetRequiredService<IHttpClientFactory>().CreateClient("gov")));

builder.Services.AddAuthentication(AuthEndpoints.Scheme)
    .AddCookie(AuthEndpoints.Scheme, o =>
    {
        o.Cookie.Name = "er_session";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        // The idle timeout is enforced by SessionPolicy; this is only an upper bound for a working day.
        o.ExpireTimeSpan = TimeSpan.FromHours(16);
        o.SlidingExpiration = false;
        o.Events = new CookieAuthenticationEvents
        {
            OnValidatePrincipal = SessionPolicy.Validate,
            // An API answers with status codes, not redirects to a login page.
            OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; },
            OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; },
        };
    });
// The driver app has its own sign-in: the personal link, valid until the end of election day.
builder.Services.AddAuthentication().AddCookie(DriverAppEndpoints.Scheme, o =>
{
    o.Cookie.Name = "er_driver";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.Events = new CookieAuthenticationEvents
    {
        OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; },
        OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; },
    };
});
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Policies.CityStaff, p => p.RequireClaim(SessionPolicy.CityClaim));
    o.AddPolicy(Policies.CityAdmin, p => p.RequireClaim(SessionPolicy.CityClaim).RequireRole(nameof(UserRole.CityAdmin)));
    o.AddPolicy(Policies.SystemAdmin, p => p.RequireRole(nameof(UserRole.SystemAdmin)));
    o.AddPolicy(Policies.Driver, p => p.AddAuthenticationSchemes(DriverAppEndpoints.Scheme).RequireClaim(DriverAppEndpoints.DriverClaim));
    o.AddPolicy(Policies.Dispatch, p => p.RequireClaim(SessionPolicy.CityClaim).RequireRole(nameof(UserRole.Dispatcher), nameof(UserRole.CityAdmin)));
});

// The caller's real IP: Cloudflare sets CF-Connecting-IP and strips any the client sent, so it cannot be spoofed
// through the proxy; the connection address is the fallback when the site is reached without Cloudflare.
static string ClientIp(HttpContext http) =>
    http.Request.Headers["CF-Connecting-IP"].FirstOrDefault() ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // Sign-in endpoints: at most 20 requests a minute from one IP (configurable for tests). Brute force is also
    // capped per account by the lockout.
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(ClientIp(http),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimit:AuthPerMinute", 20),
            Window = TimeSpan.FromMinutes(1),
        }));
    // A ceiling on every request, per signed-in user (or per IP before sign-in), far above real use so staff never
    // meet it: it stops one session or address from flooding the server. 300/min while a busy dispatcher does ~20.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
        RateLimitPartition.GetFixedWindowLimiter(
            http.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier) is { } uid ? "u:" + uid : "ip:" + ClientIp(http),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimit:PerMinute", 300),
                Window = TimeSpan.FromMinutes(1),
            }));
});

const string WebCors = "web";
builder.Services.AddCors(o => o.AddPolicy(WebCors, p =>
    p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
     .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// Hosted: the database is brought up to date when the server starts (Database:MigrateOnStart).
if (app.Configuration.GetValue("Database:MigrateOnStart", false))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseForwardedHeaders();

// Security headers on every response (pages and files alike). They constrain the browser; a normal user sees no
// change. CSP allows only what the app loads: its own bundles, Google Fonts and OpenStreetMap tiles; no framing.
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    // Cross-origin requests send only the site's origin (not the path), so the map's tile provider can identify the
    // site by its Referer; same-origin requests send the full URL. "same-origin" would send nothing cross-origin and
    // break providers (like MapTiler) that check the Referer.
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        // The map engine renders in a blob web worker. Images and tile/style data may come from any HTTPS host, so
        // the map provider is configurable; images cannot execute and this does not weaken the script protection.
        "worker-src blob:; font-src 'self' https://fonts.gstatic.com; img-src 'self' data: blob: https:; " +
        "connect-src 'self' https:; frame-ancestors 'none'; base-uri 'self'; object-src 'none'; form-action 'self'";
    if (ctx.Request.IsHttps) h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
    await next();
});

// Hosted as one service: the built screens are served from wwwroot, next to the API.
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseCors(WebCors);
app.UseAuthentication();
app.UseAuthorization();
// After authentication, so the per-request ceiling keys by the signed-in user when there is one.
app.UseRateLimiter();

// Health: the server is up, and whether it can reach the database.
app.MapGet("/api/health", async (AppDbContext db, CancellationToken ct) =>
{
    var dbOk = await db.Database.CanConnectAsync(ct);
    return Results.Json(new { status = dbOk ? "ok" : "degraded", database = dbOk },
        statusCode: dbOk ? 200 : 503);
});
// Public front-end settings. The map's tile source can be changed per deployment (Map:TileUrl / Map:TileAttribution)
// without rebuilding: OpenStreetMap by default, another provider when OSM blocks the server.
app.MapGet("/api/config", (IConfiguration cfg) => Results.Ok(new
{
    // Trim: a value pasted into a hosting dashboard can carry a trailing newline, which would break the tile URL.
    tileUrl = cfg["Map:TileUrl"]?.Trim() is { Length: > 0 } u ? u : "https://tile.openstreetmap.org/{z}/{x}/{y}.png",
    tileAttribution = cfg["Map:TileAttribution"]?.Trim() is { Length: > 0 } a ? a : "© OpenStreetMap",
    // A vector style URL (e.g. a MapTiler style.json) renders through MapLibre with Hebrew labels; null uses tileUrl.
    styleUrl = cfg["Map:StyleUrl"]?.Trim() is { Length: > 0 } s ? s : null,
}));
app.MapAuth();
app.MapCityLists();
app.MapCommunityCatalog();
app.MapRouting();
app.MapClusters();
app.MapCitySettings();
app.MapDashboard();
app.MapAnalytics();
app.MapVoterBase();
app.MapStreetImport();
app.MapCityUsers();
app.MapSystemCities();
app.MapSystem();
app.MapIntake();
app.MapDispatch();
app.MapDriverApp();
app.MapHub<CityHub>("/hubs/city");
// Any other address is a screen of the app (it handles its own routes). Unknown /api addresses stay 404, and
// so do file names (with a dot), so the screens' code and images are served as files, not as the page.
app.MapFallbackToFile("{*path:regex(^(?!api/|hubs/).*$):nonfile}", "index.html");

try
{
    await Invitations.BootstrapAsync(app.Services);
}
catch (Exception ex)
{
    app.Logger.LogError(ex, "Bootstrap of the first system admin failed (is the database up?)");
}

app.Run();

public partial class Program;
