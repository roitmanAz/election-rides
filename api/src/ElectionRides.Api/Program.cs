using System.Threading.RateLimiting;
using ElectionRides.Api.Auth;
using ElectionRides.Api.CityScreens;
using ElectionRides.Api.Common;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Data;
using ElectionRides.Api.Email;
using ElectionRides.Api.GovData;
using ElectionRides.Api.SystemAdmin;
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
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Policies.CityStaff, p => p.RequireClaim(SessionPolicy.CityClaim));
    o.AddPolicy(Policies.CityAdmin, p => p.RequireClaim(SessionPolicy.CityClaim).RequireRole(nameof(UserRole.CityAdmin)));
    o.AddPolicy(Policies.SystemAdmin, p => p.RequireRole(nameof(UserRole.SystemAdmin)));
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
app.MapClusters();
app.MapCitySettings();
app.MapStreetImport();
app.MapCityUsers();
app.MapSystemCities();

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
