using System.Net;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Tests;

public class FoundationTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_reports_ok_when_database_is_reachable()
    {
        var res = await api.CreateClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("\"database\":true", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_one_election_operation_can_be_active()
    {
        await using var db = api.NewDb();
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
