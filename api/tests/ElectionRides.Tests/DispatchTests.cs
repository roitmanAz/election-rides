using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElectionRides.Api.Dispatch;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Tests;

public class ProposalEngineTests
{
    static readonly Guid C1 = Guid.NewGuid();
    static QueueItem Q(RideKind kind, int pax = 1, double wait = 10, double? deadline = null, bool wc = false, GeoPoint? at = null) =>
        new(Guid.NewGuid(), kind, pax, wc, at ?? new GeoPoint(31.78, 35.21), [C1], new GeoPoint(31.785, 35.215), wait, deadline, false);
    static DriverItem D(int seats = 4, bool wc = false, bool soon = false, double? inMin = null) =>
        new(Guid.NewGuid(), "נהג", seats, wc, soon, inMin, null);

    [Fact]
    public void Leave_by_within_30_minutes_comes_first_then_longest_wait()
    {
        var a = Q(RideKind.Men, wait: 40);
        var b = Q(RideKind.Men, wait: 5, deadline: 20);
        var c = Q(RideKind.Men, wait: 50, deadline: 90);
        Assert.Equal([b.Id, c.Id, a.Id], ProposalEngine.Order([a, b, c]).Select(x => x.Id));
    }

    [Fact]
    public void Car_takes_one_gender_or_one_family_and_respects_seats_and_wheelchair()
    {
        var d = D(seats: 3);
        var men = Q(RideKind.Men, pax: 2);
        Assert.Equal("ברכב גברים", ProposalEngine.WhyNot(d, [men], Q(RideKind.Women)));
        Assert.Equal("משפחה נוסעת לבד", ProposalEngine.WhyNot(d, [men], Q(RideKind.Family)));
        Assert.Equal("אין מקום ברכב", ProposalEngine.WhyNot(d, [men], Q(RideKind.Men, pax: 2)));
        Assert.Equal("הרכב לא נגיש", ProposalEngine.WhyNot(d, [], Q(RideKind.Men, wc: true)));
        Assert.Equal("", ProposalEngine.WhyNot(d, [men], Q(RideKind.Men)));
    }

    [Fact]
    public void Proposal_starts_with_the_first_fitting_request_and_adds_nearby_ones()
    {
        var first = Q(RideKind.Women, wait: 30);
        var near = Q(RideKind.Women, wait: 5, at: new GeoPoint(31.781, 35.211));
        var far = Q(RideKind.Women, wait: 6, at: new GeoPoint(31.90, 35.30));
        var p = ProposalEngine.Build([first, near, far], [D()]).Single();
        Assert.Equal([first.Id, near.Id], p.RequestIds);
    }

    [Fact]
    public void Free_drivers_get_proposals_before_soon_free_drivers()
    {
        var soon = D(soon: true, inMin: 5);
        var free = D();
        var only = Q(RideKind.Men);
        var ps = ProposalEngine.Build([only], [soon, free]);
        Assert.Equal(free.Id, ps[0].DriverId);
        Assert.Equal([only.Id], ps[0].RequestIds);
        Assert.Empty(ps[1].RequestIds);
    }
}

[Collection("api")]
public class DispatchTests(ApiFactory api)
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    async Task<(HttpClient op, HttpClient disp, Guid street, Guid cluster)> Setup()
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
        var street = (await Json(await admin.PostAsJsonAsync("/api/city/streets", new { name = "חזון איש" }))).GetProperty("id").GetGuid();
        var cluster = (await Json(await admin.PostAsJsonAsync("/api/city/clusters", new { name = "בית ספר", shortName = "בית ספר", number = 1, boxes = "1-3", streetId = street, buildingNumber = "5", accessible = true }))).GetProperty("id").GetGuid();
        var (op, _, _) = await TestUsers.SignedIn(api, UserRole.Operator, city);
        var (disp, _, _) = await TestUsers.SignedIn(api, UserRole.Dispatcher, city);
        return (op, disp, street, cluster);
    }

    static object Req(string phone, string name, string id, string gender, Guid street, Guid cluster, bool wc = false) => new
    {
        callerPhone = phone,
        passengers = new[] { new { personId = (Guid?)null, fullName = name, idNumber = id, noId = false, gender, clusterId = (Guid?)cluster } },
        streetId = street, buildingNumber = "41", entrance = "", driverNotes = "", communityId = (Guid?)null,
        needsWheelchair = wc, needsWalker = false, needsHelp = false, timeMode = "Now", windowFrom = (string?)null, windowTo = (string?)null,
        urgencyReason = (string?)null, urgencyOther = (string?)null, returnMode = "DriverWaits", keepNonAccessible = false, ignoreDuplicate = false,
    };

    [Fact]
    public async Task Driver_gets_a_proposal_takes_the_ride_and_the_statuses_follow()
    {
        var (op, disp, street, cluster) = await Setup();
        var reqId = (await Json(await op.PostAsJsonAsync("/api/intake/requests", Req("0527111111", "חיים ברגר", "000000018", "Male", street, cluster)))).GetProperty("id").GetGuid();
        var drvId = (await Json(await disp.PostAsJsonAsync("/api/dispatch/drivers", new { phone = "0548000771", fullName = "אברהם גולד", seats = 4, wheelchairCar = false, hasSmartphone = false }))).GetProperty("id").GetGuid();

        var props = await Json(await disp.GetAsync("/api/dispatch/proposals"));
        var mine = props.EnumerateArray().Single(p => p.GetProperty("driverId").GetGuid() == drvId);
        Assert.Equal(reqId, mine.GetProperty("requestIds")[0].GetGuid());

        Assert.Equal(HttpStatusCode.OK, (await disp.PostAsJsonAsync("/api/dispatch/trips", new { driverId = drvId, requestIds = new[] { reqId } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await disp.PostAsJsonAsync($"/api/dispatch/drivers/{drvId}/action", new { action = "Finish" })).StatusCode);

        foreach (var a in new[] { "PickedUp", "DroppedOff", "Free" })
            Assert.Equal(HttpStatusCode.NoContent, (await disp.PostAsJsonAsync($"/api/dispatch/drivers/{drvId}/action", new { action = a })).StatusCode);

        await using var db = api.NewDb();
        Assert.Equal(RequestStatus.Done, (await db.Requests.FirstAsync(r => r.Id == reqId)).Status);
        Assert.Equal(DriverStatus.Free, (await db.Drivers.FirstAsync(d => d.Id == drvId)).Status);
    }

    [Fact]
    public async Task A_woman_cannot_join_a_car_of_men_and_the_reason_is_short()
    {
        var (op, disp, street, cluster) = await Setup();
        var man = (await Json(await op.PostAsJsonAsync("/api/intake/requests", Req("0527222221", "משה רוזנברג", "000000026", "Male", street, cluster)))).GetProperty("id").GetGuid();
        var woman = (await Json(await op.PostAsJsonAsync("/api/intake/requests", Req("0527222222", "מרים וייס", "000000034", "Female", street, cluster)))).GetProperty("id").GetGuid();
        var drv = (await Json(await disp.PostAsJsonAsync("/api/dispatch/drivers", new { phone = "0548000772", fullName = "יוסי לוי", seats = 4, wheelchairCar = false, hasSmartphone = true }))).GetProperty("id").GetGuid();

        var res = await disp.PostAsJsonAsync("/api/dispatch/trips", new { driverId = drv, requestIds = new[] { man, woman } });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("לא ניתן לצרף ליוסי לוי: ברכב גברים", (await Json(res)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Driver_who_finished_is_found_by_phone_and_rejoins()
    {
        var (_, disp, _, _) = await Setup();
        var drv = (await Json(await disp.PostAsJsonAsync("/api/dispatch/drivers", new { phone = "0548000773", fullName = "דוד שטרן", seats = 3, wheelchairCar = true, hasSmartphone = false }))).GetProperty("id").GetGuid();
        await disp.PostAsJsonAsync($"/api/dispatch/drivers/{drv}/action", new { action = "Finish" });

        var found = await Json(await disp.GetAsync("/api/dispatch/drivers/lookup/054-8000773"));
        Assert.True(found.GetProperty("found").GetBoolean());
        Assert.False(found.GetProperty("onList").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await disp.PostAsJsonAsync("/api/dispatch/drivers", new { phone = "0548000773", fullName = "אחר", seats = 3, wheelchairCar = false, hasSmartphone = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await disp.PostAsync($"/api/dispatch/drivers/{drv}/rejoin", null)).StatusCode);
    }

    [Fact]
    public async Task Break_during_a_ride_starts_when_the_driver_is_free()
    {
        var (op, disp, street, cluster) = await Setup();
        var req = (await Json(await op.PostAsJsonAsync("/api/intake/requests", Req("0527333331", "שלמה גולדברג", "000000042", "Male", street, cluster)))).GetProperty("id").GetGuid();
        var drv = (await Json(await disp.PostAsJsonAsync("/api/dispatch/drivers", new { phone = "0548000774", fullName = "מאיר כץ", seats = 4, wheelchairCar = false, hasSmartphone = false }))).GetProperty("id").GetGuid();
        await disp.PostAsJsonAsync("/api/dispatch/trips", new { driverId = drv, requestIds = new[] { req } });
        await disp.PostAsJsonAsync($"/api/dispatch/drivers/{drv}/action", new { action = "Break" });
        await disp.PostAsJsonAsync($"/api/dispatch/drivers/{drv}/action", new { action = "Free" });

        await using var db = api.NewDb();
        Assert.Equal(DriverStatus.Break, (await db.Drivers.FirstAsync(d => d.Id == drv)).Status);
    }

    [Fact]
    public async Task Dashboard_counts_today_and_drivers_and_only_the_city_admin_sees_it()
    {
        var (admin, city, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        await using (var db = api.NewDb())
        {
            var active = await db.Operations.Include(o => o.Cities).FirstOrDefaultAsync(o => o.IsActive)
                ?? db.Operations.Add(new ElectionOperation { Type = ElectionType.Knesset, ElectionDay = new DateOnly(2026, 12, 1), IsActive = true }).Entity;
            active.Cities.Add(await db.Cities.FirstAsync(c => c.Id == city.Id));
            await db.SaveChangesAsync();
        }
        var street = (await Json(await admin.PostAsJsonAsync("/api/city/streets", new { name = "סוקולוב" }))).GetProperty("id").GetGuid();
        var cluster = (await Json(await admin.PostAsJsonAsync("/api/city/clusters", new { name = "מתנ\"ס", shortName = "מתנ\"ס", number = 4, boxes = "6-9", streetId = street, buildingNumber = "3", accessible = true }))).GetProperty("id").GetGuid();
        var (op, _, _) = await TestUsers.SignedIn(api, UserRole.Operator, city);
        var (disp, _, _) = await TestUsers.SignedIn(api, UserRole.Dispatcher, city);
        await op.PostAsJsonAsync("/api/intake/requests", Req("0527555551", "נחמה פישר", "000000075", "Female", street, cluster));
        await disp.PostAsJsonAsync("/api/dispatch/drivers", new { phone = "0548000779", fullName = "צבי אורן", seats = 4, wheelchairCar = false, hasSmartphone = false });

        var d = await Json(await admin.GetAsync("/api/city/dashboard"));
        Assert.Equal(1, d.GetProperty("requestsToday").GetInt32());
        Assert.Equal(1, d.GetProperty("queued").GetInt32());
        Assert.Equal(1, d.GetProperty("drivers").GetProperty("free").GetInt32());
        Assert.Equal(1, d.GetProperty("perHalfHour").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()));
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/city/dashboard")).StatusCode);
    }

    [Fact]
    public async Task Operator_cannot_use_the_dispatcher_screen()
    {
        var (op, _, _, _) = await Setup();
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/dispatch/state")).StatusCode);
    }

    [Fact]
    public void A_pickup_window_starts_waiting_at_its_start_not_when_saved()
    {
        var israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
        var saved = new DateTimeOffset(2026, 12, 15, 10, 0, 0, israel.GetUtcOffset(new DateTime(2026, 12, 15, 10, 0, 0)));
        // Saved at 10:00 for "between 14:00 and 15:00": at 14:20 it has waited 20 minutes, at 12:00 none.
        Assert.Equal(20, Waiting.Minutes(saved.AddHours(4).AddMinutes(20), saved, TimeMode.Between, new TimeOnly(14, 0)), 3);
        Assert.Equal(0, Waiting.Minutes(saved.AddHours(2), saved, TimeMode.Between, new TimeOnly(14, 0)));
        // "Now" requests and a window that already started count from saving.
        Assert.Equal(30, Waiting.Minutes(saved.AddMinutes(30), saved, TimeMode.Now, null), 3);
        Assert.Equal(30, Waiting.Minutes(saved.AddMinutes(30), saved, TimeMode.Between, new TimeOnly(9, 0)), 3);
    }

    [Fact]
    public async Task Route_answers_empty_when_no_routing_server_is_set()
    {
        var (disp, _, _) = await TestUsers.SignedIn(api, UserRole.Dispatcher);
        Assert.Equal(HttpStatusCode.NoContent, (await disp.GetAsync("/api/route?points=31.78,35.21;31.79,35.22")).StatusCode);
    }

    [Fact]
    public async Task Dispatcher_marks_a_pickup_on_the_map_and_the_address_keeps_it()
    {
        var (op, disp, street, cluster) = await Setup();
        // Tests have no address lookup, so the request starts without a location.
        var id = (await Json(await op.PostAsJsonAsync("/api/intake/requests", Req("0527333331", "שרה לוין", "000000042", "Female", street, cluster)))).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await disp.PostAsJsonAsync($"/api/dispatch/requests/{id}/location", new { lat = 40.0, lon = 35.2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await disp.PostAsJsonAsync($"/api/dispatch/requests/{id}/location", new { lat = 31.7869, lon = 35.2236 })).StatusCode);

        var row = (await Json(await disp.GetAsync("/api/dispatch/state"))).GetProperty("queue").EnumerateArray().First(q => q.GetProperty("id").GetGuid() == id);
        Assert.Equal(31.7869, row.GetProperty("location").GetProperty("lat").GetDouble(), 4);
        Assert.False(row.GetProperty("approximateLocation").GetBoolean());

        // The next request from the same address gets the point.
        var next = (await Json(await op.PostAsJsonAsync("/api/intake/requests", Req("0527333332", "רבקה לוין", "000000059", "Female", street, cluster)))).GetProperty("id").GetGuid();
        var row2 = (await Json(await disp.GetAsync("/api/dispatch/state"))).GetProperty("queue").EnumerateArray().First(q => q.GetProperty("id").GetGuid() == next);
        Assert.Equal(35.2236, row2.GetProperty("location").GetProperty("lon").GetDouble(), 4);
    }
}
