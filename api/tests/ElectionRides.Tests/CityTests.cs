using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElectionRides.Api.Common;
using ElectionRides.Api.Domain;

namespace ElectionRides.Tests;

[Collection("api")]
public class CityTests(ApiFactory api)
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task City_admin_manages_communities_and_duplicates_are_refused()
    {
        var (admin, _, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        var added = await Json(await admin.PostAsJsonAsync("/api/city/communities", new { name = "  גור  " }));
        Assert.Equal("גור", added.GetProperty("name").GetString());

        var dup = await admin.PostAsJsonAsync("/api/city/communities", new { name = "גור" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var id = added.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/city/communities/{id}", new { name = "חסידות גור" })).StatusCode);
        var list = await Json(await admin.GetAsync("/api/city/communities"));
        Assert.Equal("חסידות גור", list[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Operator_reads_city_lists_but_cannot_change_them()
    {
        var (admin, city, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        await admin.PostAsJsonAsync("/api/city/streets", new { name = "רבי עקיבא" });
        var (op, _, _) = await TestUsers.SignedIn(api, UserRole.Operator, city);

        Assert.Equal(1, (await Json(await op.GetAsync("/api/city/streets"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await op.PostAsJsonAsync("/api/city/streets", new { name = "חזון איש" })).StatusCode);
    }

    [Fact]
    public async Task A_city_never_sees_another_citys_data()
    {
        var (a, _, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        var (b, _, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        var added = await Json(await a.PostAsJsonAsync("/api/city/communities", new { name = "ויז'ניץ" }));

        Assert.Equal(0, (await Json(await b.GetAsync("/api/city/communities"))).GetArrayLength());
        var id = added.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/city/communities/{id}")).StatusCode);
    }

    [Fact]
    public async Task Cluster_with_box_range_is_saved_and_boxes_cannot_be_reused()
    {
        var (admin, _, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        var street = (await Json(await admin.PostAsJsonAsync("/api/city/streets", new { name = "דסלר" }))).GetProperty("id").GetGuid();

        var body = new { name = "תלמוד תורה אור החיים", shortName = "אור החיים", number = 1, boxes = "31–35",
            streetId = street, buildingNumber = "7", accessible = true, dropoffNote = "כניסה מהחצר" };
        var res = await admin.PostAsJsonAsync("/api/city/clusters", body);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var boxes = (await Json(res)).GetProperty("boxes").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Equal(["31", "32", "33", "34", "35"], boxes);

        var clash = await admin.PostAsJsonAsync("/api/city/clusters", body with { shortName = "אחר", number = 2, boxes = "35, 36" });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("35", (await Json(clash)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Cluster_needs_accessibility_answer_and_a_street_from_the_list()
    {
        var (admin, _, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        var noAccess = await admin.PostAsJsonAsync("/api/city/clusters", new { name = "מקום", shortName = "מקום", boxes = "1",
            streetId = Guid.NewGuid(), buildingNumber = "1", accessible = (bool?)null });
        Assert.Equal(HttpStatusCode.BadRequest, noAccess.StatusCode);

        var badStreet = await admin.PostAsJsonAsync("/api/city/clusters", new { name = "מקום", shortName = "מקום", boxes = "1",
            streetId = Guid.NewGuid(), buildingNumber = "1", accessible = false });
        Assert.Contains("הרחוב לא נמצא", (await Json(badStreet)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Hq_phone_is_validated()
    {
        var (admin, _, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/city/settings", new { hqPhone = "05212345" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/city/settings", new { hqPhone = "02-6543210" })).StatusCode);
        Assert.Equal("026543210", (await Json(await admin.GetAsync("/api/city/settings"))).GetProperty("hqPhone").GetString());
    }

    [Theory]
    [InlineData("0527123456", true)]
    [InlineData("0771234567", true)]
    [InlineData("026543210", true)]
    [InlineData("036543210", true)]
    [InlineData("052712345", false)]
    [InlineData("016543210", false)]
    [InlineData("02654321", false)]
    public void Phone_rules(string phone, bool valid) => Assert.Equal(valid, Rules.IsValidPhone(phone));

    [Fact]
    public void Box_text_is_read_as_numbers_ranges_and_lists()
    {
        Assert.Equal(["10", "12", "14", "15", "16"], Rules.ParseBoxes("10, 12, 14-16"));
        Assert.Equal(["12.1"], Rules.ParseBoxes("12.1"));
        Assert.Null(Rules.ParseBoxes("abc"));
        Assert.Null(Rules.ParseBoxes("20-10"));
    }

    [Fact]
    public async Task City_admin_picks_communities_from_the_shared_list()
    {
        var (admin, _, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        var catalog = (await Json(await admin.GetAsync("/api/city/communities/catalog"))).EnumerateArray().ToList();
        Assert.All(CommunityCatalogItem.Seed, n => Assert.Contains(catalog, c => c.GetProperty("name").GetString() == n));
        int Id(string name) => catalog.First(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetInt32();

        // A hand-added community with the same name is linked, not doubled.
        await admin.PostAsJsonAsync("/api/city/communities", new { name = "בעלזא" });
        await admin.PutAsJsonAsync("/api/city/communities/catalog", new { ids = new[] { Id("גור"), Id("בעלזא"), Id("חב\"ד") } });
        var list = (await Json(await admin.GetAsync("/api/city/communities"))).EnumerateArray().ToList();
        Assert.Equal(3, list.Count);
        Assert.All(list, c => Assert.Equal("Catalog", c.GetProperty("source").GetString()));

        // A community from the list keeps its name; unticking removes it.
        var gur = list.First(c => c.GetProperty("name").GetString() == "גור").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/city/communities/{gur}", new { name = "גור 2" })).StatusCode);
        await admin.PutAsJsonAsync("/api/city/communities/catalog", new { ids = new[] { Id("בעלזא") } });
        Assert.Single((await Json(await admin.GetAsync("/api/city/communities"))).EnumerateArray());
    }

    [Fact]
    public async Task New_community_joins_the_shared_list_and_renaming_waits_for_a_system_admin()
    {
        var (admin, city, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        var (sys, _, _) = await TestUsers.SignedIn(api, UserRole.SystemAdmin);
        var name = "קהילה " + Guid.NewGuid().ToString("N")[..6];

        // Added by a city admin: straight into the shared list, and picked for the city.
        var added = await Json(await admin.PostAsJsonAsync("/api/city/communities/catalog/new", new { name }));
        var id = added.GetProperty("id").GetInt32();
        Assert.Contains((await Json(await admin.GetAsync("/api/city/communities"))).EnumerateArray(), c => c.GetProperty("name").GetString() == name);

        // Rename request: nothing changes until approved.
        var renamed = name + " חדש";
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/city/communities/catalog/{id}/rename", new { name = renamed })).StatusCode);
        var mine = (await Json(await admin.GetAsync("/api/city/communities/catalog"))).EnumerateArray().First(c => c.GetProperty("id").GetInt32() == id);
        Assert.Equal(name, mine.GetProperty("name").GetString());
        Assert.Equal(renamed, mine.GetProperty("pendingName").GetString());

        var request = (await Json(await sys.GetAsync("/api/admin/catalog"))).GetProperty("requests").EnumerateArray()
            .First(r => r.GetProperty("catalogId").GetInt32() == id);
        Assert.Equal(city.Name, request.GetProperty("city").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await sys.PostAsync($"/api/admin/catalog/requests/{request.GetProperty("id").GetGuid()}/approve", null)).StatusCode);
        Assert.Contains((await Json(await admin.GetAsync("/api/city/communities"))).EnumerateArray(), c => c.GetProperty("name").GetString() == renamed);

        // A system admin renames directly; a city admin cannot use the system admin's route.
        Assert.Equal(HttpStatusCode.NoContent, (await sys.PutAsJsonAsync($"/api/admin/catalog/{id}", new { name = name + " ב" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PutAsJsonAsync($"/api/admin/catalog/{id}", new { name = "x" })).StatusCode);

        // Deleting removes it from the shared list and from the city's list (no requests used it).
        Assert.Equal(HttpStatusCode.NoContent, (await sys.DeleteAsync($"/api/admin/catalog/{id}")).StatusCode);
        Assert.DoesNotContain((await Json(await admin.GetAsync("/api/city/communities"))).EnumerateArray(), c => c.GetProperty("name").GetString()!.StartsWith(name));
        Assert.DoesNotContain((await Json(await admin.GetAsync("/api/city/communities/catalog"))).EnumerateArray(), c => c.GetProperty("id").GetInt32() == id);
    }

    [Fact]
    public void Address_search_tries_the_common_spelling_variants()
    {
        Assert.Equal(["קדושת אהרון", "קדושת אהרן"], ElectionRides.Api.Maps.NominatimGeocoder.SpellingVariants("קדושת אהרון"));
        Assert.Equal(["דוויק", "דויק"], ElectionRides.Api.Maps.NominatimGeocoder.SpellingVariants("דוויק"));
        Assert.Equal(["יפו"], ElectionRides.Api.Maps.NominatimGeocoder.SpellingVariants("יפו"));
    }
}
