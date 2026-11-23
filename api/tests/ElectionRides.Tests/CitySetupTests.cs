using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElectionRides.Api.Domain;
using ElectionRides.Api.GovData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElectionRides.Tests;

[Collection("api")]
public class CitySetupTests(ApiFactory api)
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    async Task<(HttpClient admin, City city)> CityWithLocality(int code)
    {
        await using var db = api.NewDb();
        var city = new City { Name = "עיר " + Guid.NewGuid().ToString("N")[..6], LocalityCode = code };
        db.Cities.Add(city);
        await db.SaveChangesAsync();
        var (admin, _, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin, city);
        return (admin, city);
    }

    [Fact]
    public async Task Street_reimport_keeps_manual_streets_and_streets_used_by_a_cluster()
    {
        var code = 50_000 + Random.Shared.Next(10_000);
        api.Gov.Streets[code] = [new(101, "רבי עקיבא"), new(102, "חזון איש"), new(103, "דסלר")];
        var (admin, _) = await CityWithLocality(code);

        var first = await Json(await admin.PostAsync("/api/city/streets/import", null));
        Assert.Equal(3, first.GetProperty("added").GetInt32());
        await admin.PostAsJsonAsync("/api/city/streets", new { name = "סמטה פרטית" });

        var streets = await Json(await admin.GetAsync("/api/city/streets"));
        var dessler = streets.EnumerateArray().First(s => s.GetProperty("name").GetString() == "דסלר").GetProperty("id").GetGuid();
        await admin.PostAsJsonAsync("/api/city/clusters", new { name = "ישיבה", shortName = "ישיבה", boxes = "1",
            streetId = dessler, buildingNumber = "3", accessible = true });

        // The official list renames one street and drops two, one of which has a cluster.
        api.Gov.Streets[code] = [new(101, "רבי עקיבא (רחוב)")];
        var second = await Json(await admin.PostAsync("/api/city/streets/import", null));
        Assert.Equal(1, second.GetProperty("renamed").GetInt32());
        Assert.Equal(1, second.GetProperty("removed").GetInt32());

        var names = (await Json(await admin.GetAsync("/api/city/streets"))).EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("רבי עקיבא (רחוב)", names);
        Assert.Contains("סמטה פרטית", names);
        Assert.Contains("דסלר", names);
        Assert.DoesNotContain("חזון איש", names);
    }

    [Fact]
    public async Task Reimport_keeps_a_name_the_city_admin_edited_and_follows_official_renames()
    {
        var code = 60_000 + Random.Shared.Next(10_000);
        api.Gov.Streets[code] = [new(201, "אזולאי עקיבא"), new(202, "הנביאים")];
        var (admin, _) = await CityWithLocality(code);
        await admin.PostAsync("/api/city/streets/import", null);

        var streets = await Json(await admin.GetAsync("/api/city/streets"));
        var id = streets.EnumerateArray().First(s => s.GetProperty("name").GetString() == "אזולאי עקיבא").GetProperty("id").GetGuid();
        await admin.PutAsJsonAsync($"/api/city/streets/{id}", new { name = "עקיבא אזולאי" });

        // Same official list again, plus an official rename of the other street.
        api.Gov.Streets[code] = [new(201, "אזולאי עקיבא"), new(202, "רחוב הנביאים")];
        await admin.PostAsync("/api/city/streets/import", null);

        var names = (await Json(await admin.GetAsync("/api/city/streets"))).EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()).ToList();
        Assert.Contains("עקיבא אזולאי", names);
        Assert.DoesNotContain("אזולאי עקיבא", names);
        Assert.Contains("רחוב הנביאים", names);
    }

    [Fact]
    public async Task System_admin_opens_a_city_from_the_localities_list_and_invites_its_admin()
    {
        var (sys, _, _) = await TestUsers.SignedIn(api, UserRole.SystemAdmin);
        var code = 70_000 + Random.Shared.Next(10_000);
        api.Gov.Localities.Add(new Locality(code, "עיר בדיקה " + code));

        var found = await Json(await sys.GetAsync($"/api/admin/localities?q={Uri.EscapeDataString("עיר בדיקה " + code)}"));
        Assert.Equal(code, found[0].GetProperty("code").GetInt32());

        var email = $"cityadmin{code}@test.local";
        var res = await sys.PostAsJsonAsync("/api/admin/cities", new { localityCode = code, adminName = "דוד כהן", adminEmail = email });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(api.Services.GetRequiredService<ElectionRides.Api.Email.DevEmailSender>().Sent, m => m.To == email);

        var again = await sys.PostAsJsonAsync("/api/admin/cities", new { localityCode = code, adminName = "אחר", adminEmail = "other" + email });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var cities = await Json(await sys.GetAsync("/api/admin/cities"));
        var city = cities.EnumerateArray().First(c => c.GetProperty("name").GetString() == "עיר בדיקה " + code);
        Assert.Equal("דוד כהן", city.GetProperty("admins")[0].GetProperty("fullName").GetString());
        Assert.False(city.GetProperty("setup").GetProperty("hqPhone").GetBoolean());
    }

    [Fact]
    public async Task New_city_can_copy_communities_and_hours_from_another_city()
    {
        var (sys, _, _) = await TestUsers.SignedIn(api, UserRole.SystemAdmin);
        var (admin, source) = await CityWithLocality(1);
        await admin.PostAsJsonAsync("/api/city/communities", new { name = "בעלזא" });
        await admin.PutAsJsonAsync("/api/city/settings", new { hqPhone = "026543210", hoursFrom = "07:00", hoursTo = "22:00" });

        var code = 80_000 + Random.Shared.Next(10_000);
        api.Gov.Localities.Add(new Locality(code, "העתק " + code));
        var res = await Json(await sys.PostAsJsonAsync("/api/admin/cities",
            new { localityCode = code, adminName = "מנהל", adminEmail = $"copy{code}@test.local", copyFromCityId = source.Id }));

        await using var db = api.NewDb();
        var id = res.GetProperty("id").GetGuid();
        Assert.Equal(new TimeOnly(7, 0), (await db.Cities.FirstAsync(c => c.Id == id)).HoursFrom);
        Assert.True(await db.Communities.AnyAsync(c => c.CityId == id && c.Name == "בעלזא"));
        Assert.Null((await db.Cities.FirstAsync(c => c.Id == id)).HqPhone);
    }

    [Fact]
    public async Task City_admin_cannot_use_system_admin_endpoints()
    {
        var (admin, _, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync("/api/admin/cities")).StatusCode);
    }

    [Fact]
    public async Task City_admin_invites_and_removes_users_and_cannot_remove_self()
    {
        var (admin, _, me) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        var email = $"op{Guid.NewGuid():N}@test.local";
        var add = await admin.PostAsJsonAsync("/api/city/users", new { email, fullName = "שרה לוי", role = "Operator" });
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/city/users", new { email, fullName = "שוב", role = "Operator" })).StatusCode);

        var list = await Json(await admin.GetAsync("/api/city/users"));
        var sara = list.EnumerateArray().First(u => u.GetProperty("email").GetString() == email);
        Assert.True(sara.GetProperty("invited").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/city/users/{me.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/city/users/{sara.GetProperty("id").GetGuid()}")).StatusCode);
        Assert.DoesNotContain((await Json(await admin.GetAsync("/api/city/users"))).EnumerateArray(),
            u => u.GetProperty("email").GetString() == email);
    }

    [Fact]
    public async Task City_admin_cannot_create_a_system_admin()
    {
        var (admin, _, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        var res = await admin.PostAsJsonAsync("/api/city/users", new { email = $"x{Guid.NewGuid():N}@test.local", fullName = "x", role = "SystemAdmin" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
