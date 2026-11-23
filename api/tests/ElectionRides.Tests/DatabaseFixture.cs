using ElectionRides.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ElectionRides.Tests;

/// <summary>
/// Runs the API against a separate, freshly migrated test database.
/// Locally it uses the docker-compose database; in CI the TEST_DB variable points at the service container.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("TEST_DB")
        ?? "Host=localhost;Port=5433;Database=election_rides_test;Username=er;Password=er_dev_only";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Db"] = ConnectionString,
        }));
    }

    public AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString, o => o.UseNetTopologySuite()).Options);

    public async Task InitializeAsync()
    {
        await using var db = NewDb();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;
}
