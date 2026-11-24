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
builder.Services.AddSignalR();
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

// Sign-in endpoints: at most 20 requests a minute from one address (configurable for tests).
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = http.RequestServices.GetRequiredService<IConfiguration>().GetValue("RateLimit:AuthPerMinute", 20),
            Window = TimeSpan.FromMinutes(1),
        }));
});

const string WebCors = "web";
builder.Services.AddCors(o => o.AddPolicy(WebCors, p =>
    p.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
     .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseCors(WebCors);
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Health: the server is up, and whether it can reach the database.
app.MapGet("/api/health", async (AppDbContext db, CancellationToken ct) =>
{
    var dbOk = await db.Database.CanConnectAsync(ct);
    return Results.Json(new { status = dbOk ? "ok" : "degraded", database = dbOk },
        statusCode: dbOk ? 200 : 503);
});
app.MapAuth();
app.MapCityLists();
app.MapCommunityCatalog();
app.MapClusters();
app.MapCitySettings();
app.MapDashboard();
app.MapVoterBase();
app.MapStreetImport();
app.MapCityUsers();
app.MapSystemCities();
app.MapSystem();
app.MapIntake();
app.MapDispatch();
app.MapDriverApp();
app.MapHub<CityHub>("/hubs/city");

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
