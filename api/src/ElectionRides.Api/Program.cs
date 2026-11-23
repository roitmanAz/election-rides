using ElectionRides.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Db"), npg => npg.UseNetTopologySuite()));

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

// Health: the server is up, and whether it can reach the database.
app.MapGet("/api/health", async (AppDbContext db, CancellationToken ct) =>
{
    var dbOk = await db.Database.CanConnectAsync(ct);
    return Results.Json(new { status = dbOk ? "ok" : "degraded", database = dbOk },
        statusCode: dbOk ? 200 : 503);
});

app.Run();

public partial class Program;
