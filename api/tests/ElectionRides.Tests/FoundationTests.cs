using System.Net;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Tests;

[Collection("api")]
public class FoundationTests(ApiFactory api)
{
    [Fact]
    public async Task Health_reports_ok_when_database_is_reachable()
    {
        var res = await api.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"database\":true", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        var res = await api.CreateClient().GetAsync("/api/health");
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", res.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", res.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task The_per_request_ceiling_returns_429_when_one_address_floods()
    {
        // The test config's ceiling is very high; a client with its own low ceiling proves the limiter is wired.
        using var factory = api.WithConfig(new() { ["RateLimit:PerMinute"] = "5" });
        var client = factory.CreateClient();
        var codes = new List<int>();
        for (var i = 0; i < 12; i++) codes.Add((int)(await client.GetAsync("/api/health")).StatusCode);
        Assert.Contains(429, codes);
    }

    [Fact]
    public async Task Only_one_election_operation_can_be_active()
    {
        await using var db = api.NewDb();
        // Other tests may leave an operation active; start from none.
        await db.Operations.Where(o => o.IsActive).ExecuteUpdateAsync(s => s.SetProperty(o => o.IsActive, false));
        db.Operations.Add(new ElectionOperation { Type = ElectionType.Knesset, ElectionDay = new DateOnly(2026, 10, 27), IsActive = true });
        await db.SaveChangesAsync();

        db.Operations.Add(new ElectionOperation { Type = ElectionType.Municipal, ElectionDay = new DateOnly(2028, 10, 31), IsActive = true });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Staff_user_must_belong_to_a_city_and_system_admin_must_not()
    {
        await using var db = api.NewDb();
        db.Users.Add(new AppUser { Email = "op-no-city@test.local", FullName = "מוקדן בלי עיר", Role = UserRole.Operator });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        db.ChangeTracker.Clear();
        db.Users.Add(new AppUser { Email = "sys@test.local", FullName = "מנהל מערכת", Role = UserRole.SystemAdmin });
        await db.SaveChangesAsync();
    }
}
