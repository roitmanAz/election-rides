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
        Assert.Equal(112, catalog.Count);
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
}
