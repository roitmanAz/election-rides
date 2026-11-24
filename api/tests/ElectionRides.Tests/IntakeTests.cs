using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElectionRides.Api.Common;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Tests;

[Collection("api")]
public class IntakeTests(ApiFactory api)
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    /// <summary>A city in the active operation, with an operator, a street and two clusters (one not accessible).</summary>
    async Task<(HttpClient op, Guid street, Guid accessible, Guid stairs)> Setup()
    {
        var (admin, city, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        await using (var db = api.NewDb())
        {
            var active = await db.Operations.Include(o => o.Cities).FirstOrDefaultAsync(o => o.IsActive);
            if (active is null)
            {
                active = new ElectionOperation { Type = ElectionType.Knesset, ElectionDay = new DateOnly(2026, 12, 1), IsActive = true };
                db.Operations.Add(active);
            }
            active.Cities.Add(await db.Cities.FirstAsync(c => c.Id == city.Id));
            await db.SaveChangesAsync();
        }
        var street = (await Json(await admin.PostAsJsonAsync("/api/city/streets", new { name = "רבי עקיבא" }))).GetProperty("id").GetGuid();
        var a = (await Json(await admin.PostAsJsonAsync("/api/city/clusters", new { name = "בית ספר", shortName = "בית ספר", number = 1, boxes = "1-3", streetId = street, buildingNumber = "5", accessible = true }))).GetProperty("id").GetGuid();
        var b = (await Json(await admin.PostAsJsonAsync("/api/city/clusters", new { name = "ישיבה", shortName = "ישיבה", number = 2, boxes = "4-6", streetId = street, buildingNumber = "9", accessible = false }))).GetProperty("id").GetGuid();
        var (op, _, _) = await TestUsers.SignedIn(api, UserRole.Operator, city);
        return (op, street, a, b);
    }

    static object Request(string phone, Guid street, Guid cluster, string idNumber = "000000018", string name = "יעקב פרידמן",
        bool wheelchair = false, bool keep = false, bool ignoreDup = false, string timeMode = "Now", string? reason = null, string? other = null, string gender = "Male") => new
    {
        callerPhone = phone,
        passengers = new[] { new { personId = (Guid?)null, fullName = name, idNumber, noId = false, gender, clusterId = (Guid?)cluster } },
        streetId = street, buildingNumber = "112", entrance = "ב", driverNotes = "קומה 2",
        communityId = (Guid?)null, needsWheelchair = wheelchair, needsWalker = false, needsHelp = false,
        timeMode, windowFrom = (string?)null, windowTo = timeMode == "LeaveBy" ? "12:30" : null, urgencyReason = reason, urgencyOther = other,
        returnMode = "DriverWaits", keepNonAccessible = keep, ignoreDuplicate = ignoreDup,
    };

    [Fact]
    public async Task Saved_request_gets_a_number_and_the_phone_finds_the_person_next_time()
    {
        var (op, street, cluster, _) = await Setup();
        var res = await op.PostAsJsonAsync("/api/intake/requests", Request("052-7123456", street, cluster));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, (await Json(res)).GetProperty("number").GetInt32());

        var known = await Json(await op.GetAsync("/api/intake/phone/0527123456"));
        Assert.Equal("יעקב פרידמן", known.GetProperty("people")[0].GetProperty("fullName").GetString());
        Assert.Equal(1, known.GetProperty("openRequests").GetArrayLength());
    }

    [Fact]
    public async Task Same_person_twice_on_the_same_day_is_warned_with_the_existing_request()
    {
        var (op, street, cluster, _) = await Setup();
        await op.PostAsJsonAsync("/api/intake/requests", Request("0527000001", street, cluster));
        var dup = await op.PostAsJsonAsync("/api/intake/requests", Request("0527000002", street, cluster));
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        Assert.Equal(1, (await Json(dup)).GetProperty("duplicate").GetProperty("number").GetInt32());

        var anyway = await op.PostAsJsonAsync("/api/intake/requests", Request("0527000002", street, cluster, ignoreDup: true));
        Assert.Equal(HttpStatusCode.OK, anyway.StatusCode);
    }

    [Fact]
    public async Task Wheelchair_to_a_place_that_is_not_accessible_needs_an_answer()
    {
        var (op, street, _, stairs) = await Setup();
        var ask = await op.PostAsJsonAsync("/api/intake/requests", Request("0527000003", street, stairs, wheelchair: true));
        Assert.Equal((HttpStatusCode)422, ask.StatusCode);
        var stay = await op.PostAsJsonAsync("/api/intake/requests", Request("0527000003", street, stairs, wheelchair: true, keep: true));
        Assert.Equal(HttpStatusCode.OK, stay.StatusCode);
    }

    [Fact]
    public async Task Invalid_id_and_phone_are_refused_and_other_reason_needs_text()
    {
        var (op, street, cluster, _) = await Setup();
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/api/intake/requests", Request("0527000004", street, cluster, idNumber: "123456789"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/api/intake/requests", Request("05270000", street, cluster))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync("/api/intake/requests",
            Request("0527000004", street, cluster, timeMode: "LeaveBy", reason: "אחר"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync("/api/intake/requests",
            Request("0527000004", street, cluster, timeMode: "LeaveBy", reason: "אחר", other: "חתונה בבני ברק"))).StatusCode);
    }

    [Fact]
    public async Task Request_can_be_found_edited_and_cancelled_with_a_reason()
    {
        var (op, street, cluster, _) = await Setup();
        var id = (await Json(await op.PostAsJsonAsync("/api/intake/requests", Request("0527000005", street, cluster, name: "מרים וייס", idNumber: "000000026")))).GetProperty("id").GetGuid();

        var found = await Json(await op.GetAsync($"/api/intake/search?q={Uri.EscapeDataString("וייס")}"));
        Assert.Equal(id, found[0].GetProperty("id").GetGuid());
        Assert.Equal(id, (await Json(await op.GetAsync("/api/intake/search?q=27000005")))[0].GetProperty("id").GetGuid());

        var edit = await op.PutAsJsonAsync($"/api/intake/requests/{id}", Request("0527000005", street, cluster, name: "מרים וייס", idNumber: "000000026", ignoreDup: true));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        // Changing a passenger from man to woman is saved.
        Assert.Equal(HttpStatusCode.OK, (await op.PutAsJsonAsync($"/api/intake/requests/{id}",
            Request("0527000005", street, cluster, name: "מרים וייס", idNumber: "000000026", ignoreDup: true, gender: "Female"))).StatusCode);
        var saved = await Json(await op.GetAsync($"/api/intake/requests/{id}"));
        Assert.Equal("Female", saved.GetProperty("passengers")[0].GetProperty("gender").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await op.PostAsJsonAsync($"/api/intake/requests/{id}/cancel", new { reason = "אחר" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsJsonAsync($"/api/intake/requests/{id}/cancel", new { reason = "הפונה ביטל" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await op.PostAsJsonAsync($"/api/intake/requests/{id}/cancel", new { reason = "הפונה ביטל" })).StatusCode);
    }

    [Theory]
    [InlineData("000000018", true)]
    [InlineData("032144586", true)]
    [InlineData("123456782", true)]
    [InlineData("123456789", false)]
    [InlineData("12345678a", false)]
    [InlineData("", false)]
    public void Id_check_digit(string id, bool valid) => Assert.Equal(valid, Rules.IsValidIdNumber(id));
}
