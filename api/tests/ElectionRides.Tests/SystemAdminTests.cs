using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Tests;

[Collection("api")]
public class SystemAdminTests(ApiFactory api)
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    async Task<ElectionOperation> ActiveWith(Guid cityId)
    {
        await using var db = api.NewDb();
        var active = await db.Operations.Include(o => o.Cities).FirstOrDefaultAsync(o => o.IsActive)
            ?? db.Operations.Add(new ElectionOperation { Type = ElectionType.Knesset, ElectionDay = DateOnly.FromDateTime(DateTime.Today.AddDays(7)), IsActive = true }).Entity;
        active.Cities.Add(await db.Cities.FirstAsync(c => c.Id == cityId));
        await db.SaveChangesAsync();
        return active;
    }

    [Fact]
    public async Task Live_status_warns_about_a_city_without_a_city_admin()
    {
        var (sys, _, _) = await TestUsers.SignedIn(api, UserRole.SystemAdmin);
        City city;
        await using (var db = api.NewDb())
        {
            city = new City { Name = "בלי מנהל " + Guid.NewGuid().ToString("N")[..6] };
            db.Cities.Add(city);
            await db.SaveChangesAsync();
        }
        await ActiveWith(city.Id);
        var live = await Json(await sys.GetAsync("/api/admin/live"));
        Assert.Contains(live.GetProperty("alerts").EnumerateArray(),
            a => a.GetProperty("city").GetString() == city.Name && a.GetProperty("text").GetString() == "אין מנהל עיר");
    }

    [Fact]
    public async Task System_admins_cannot_remove_themselves()
    {
        var (sys, _, me) = await TestUsers.SignedIn(api, UserRole.SystemAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, (await sys.DeleteAsync($"/api/admin/sysadmins/{me.Id}")).StatusCode);
        var added = await Json(await sys.PostAsJsonAsync("/api/admin/sysadmins", new { email = $"s{Guid.NewGuid():N}@test.local", fullName = "מנהלת שנייה" }));
        Assert.Equal(HttpStatusCode.NoContent, (await sys.DeleteAsync($"/api/admin/sysadmins/{added.GetProperty("id").GetGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Security_policy_takes_only_listed_choices_and_is_audited()
    {
        var (sys, _, _) = await TestUsers.SignedIn(api, UserRole.SystemAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, (await sys.PutAsJsonAsync("/api/admin/security",
            new { requireEmailCode = true, rememberDeviceHours = 99, idleTimeoutMinutes = 30, maxFailedLogins = 5, lockMinutes = 15 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await sys.PutAsJsonAsync("/api/admin/security",
            new { requireEmailCode = true, rememberDeviceHours = 24, idleTimeoutMinutes = 60, maxFailedLogins = 5, lockMinutes = 15 })).StatusCode);
        var log = await Json(await sys.GetAsync($"/api/admin/audit?q={Uri.EscapeDataString("ניתוק אחרי 60")}"));
        Assert.True(log.GetProperty("rows").GetArrayLength() >= 1);
        // The Hebrew action name is searchable too, even by one letter.
        var byName = await Json(await sys.GetAsync($"/api/admin/audit?q={Uri.EscapeDataString("מדיניות")}"));
        Assert.Contains(byName.GetProperty("rows").EnumerateArray(), r => r.GetProperty("action").GetString() == "מדיניות אבטחה");
        Assert.True((await Json(await sys.GetAsync($"/api/admin/audit?q={Uri.EscapeDataString("מ")}"))).GetProperty("rows").GetArrayLength() >= 1);
        await sys.PutAsJsonAsync("/api/admin/security", new { requireEmailCode = true, rememberDeviceHours = 24, idleTimeoutMinutes = 30, maxFailedLogins = 5, lockMinutes = 15 });
    }

    [Fact]
    public async Task Party_settings_set_the_deletion_days_and_check_emails()
    {
        var (sys, _, _) = await TestUsers.SignedIn(api, UserRole.SystemAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, (await sys.PutAsJsonAsync("/api/admin/party", new { privacyOfficerEmail = "not-an-email", baseDeletionDays = 30 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await sys.PutAsJsonAsync("/api/admin/party",
            new { partyName = "מפלגה", privacyOfficerName = "ממונה", privacyOfficerEmail = "Privacy@Party.Test", legalAdvisorEmail = "legal@party.test", baseDeletionDays = 30 })).StatusCode);
        var p = await Json(await sys.GetAsync("/api/admin/party"));
        Assert.Equal("privacy@party.test", p.GetProperty("privacyOfficerEmail").GetString());
        Assert.Equal(30, p.GetProperty("baseDeletionDays").GetInt32());
        await sys.PutAsJsonAsync("/api/admin/party", new { partyName = "מפלגה", privacyOfficerEmail = "privacy@party.test", baseDeletionDays = (int?)null });
    }

    [Fact]
    public async Task Report_counts_per_city_and_exports_to_excel_and_closing_stops_requests()
    {
        var (sys, _, _) = await TestUsers.SignedIn(api, UserRole.SystemAdmin);
        var (admin, city, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        var op = await ActiveWith(city.Id);
        var report = await Json(await sys.GetAsync($"/api/admin/reports/{op.Id}"));
        Assert.Contains(report.GetProperty("cities").EnumerateArray(), c => c.GetProperty("city").GetString() == city.Name);
        var excel = await sys.GetAsync($"/api/admin/reports/{op.Id}/excel");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", excel.Content.Headers.ContentType?.MediaType);

        // Cities of the active operation can change; a city with requests or drivers cannot leave.
        City other;
        await using (var db = api.NewDb())
        {
            other = new City { Name = "נוספת " + Guid.NewGuid().ToString("N")[..6] };
            db.Cities.Add(other);
            db.Drivers.Add(new Driver { CityId = city.Id, OperationId = op.Id, FullName = "נהג", Phone = "05" + Random.Shared.Next(10000000, 99999999), Seats = 4 });
            await db.SaveChangesAsync();
        }
        var ids = (await Json(await sys.GetAsync("/api/admin/operations"))).EnumerateArray().First(o => o.GetProperty("id").GetGuid() == op.Id)
            .GetProperty("cities").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(HttpStatusCode.NoContent, (await sys.PutAsJsonAsync($"/api/admin/operations/{op.Id}/cities", new { cityIds = ids.Append(other.Id) })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await sys.PutAsJsonAsync($"/api/admin/operations/{op.Id}/cities", new { cityIds = new[] { other.Id } })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await sys.PostAsync($"/api/admin/operations/{op.Id}/close", null)).StatusCode);
        var state = await Json(await admin.GetAsync("/api/city/settings"));
        Assert.False(state.GetProperty("inActiveOperation").GetBoolean());
    }
}
