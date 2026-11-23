using ElectionRides.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ElectionRides.Api.GovData;

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
            ["RateLimit:AuthPerMinute"] = "10000",
        }));
        // Tests never call data.gov.il.
        builder.ConfigureServices(s => s.Replace(ServiceDescriptor.Singleton<IGovData>(Gov)));
    }

    public FakeGovData Gov { get; } = new();

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

/// <summary>All API tests share one freshly migrated database and run one class at a time.</summary>
[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFactory>;

public class FakeGovData : IGovData
{
    public List<Locality> Localities { get; } = [new(3000, "ירושלים"), new(6100, "בני ברק"), new(1139, "כרמיאל")];
    public Dictionary<int, List<OfficialStreet>> Streets { get; } = new();

    public Task<IReadOnlyList<Locality>> LocalitiesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Locality>>(Localities);

    public Task<IReadOnlyList<OfficialStreet>> StreetsAsync(int localityCode, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OfficialStreet>>(Streets.GetValueOrDefault(localityCode) ?? []);
}
