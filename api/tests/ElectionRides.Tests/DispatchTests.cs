using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElectionRides.Api.CityScreens;
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
    public void Only_requests_to_exactly_the_same_cluster_join_a_ride()
    {
        var other = Guid.NewGuid();
        var first = Q(RideKind.Men, wait: 30);
        var sameCluster = Q(RideKind.Men, wait: 5, at: new GeoPoint(31.781, 35.211));
        var otherCluster = new QueueItem(Guid.NewGuid(), RideKind.Men, 1, false, new GeoPoint(31.781, 35.211), [other],
            new GeoPoint(31.785, 35.215), 5, null, false);
        var p = ProposalEngine.Build([first, sameCluster, otherCluster], [D()]).Single();
        Assert.Equal([first.Id, sameCluster.Id], p.RequestIds);
    }

    [Fact]
    public void A_request_joins_only_if_it_adds_at_most_5_minutes_of_driving()
    {
        // Travel time: 1 minute per 0.001 degree of latitude, in this test.
        ProposalEngine.TravelMinutes t = (a, b) => Math.Abs(a.Lat - b.Lat) * 1000 + Math.Abs(a.Lon - b.Lon) * 1000;
        var first = Q(RideKind.Men, wait: 30, at: new GeoPoint(31.780, 35.215));
        var onTheWay = Q(RideKind.Men, wait: 5, at: new GeoPoint(31.782, 35.215));   // between the pickup and the cluster
        var backwards = Q(RideKind.Men, wait: 6, at: new GeoPoint(31.774, 35.215));  // 6 minutes the other way
        var p = ProposalEngine.Build([first, onTheWay, backwards], [D()], t).Single();
        Assert.Equal([first.Id, onTheWay.Id], p.RequestIds);
    }

    [Fact]
    public void The_driver_who_reaches_the_first_request_soonest_gets_it()
    {
        var far = new DriverItem(Guid.NewGuid(), "רחוק", 4, false, false, null, new GeoPoint(31.90, 35.30));
        var near = new DriverItem(Guid.NewGuid(), "קרוב", 4, false, false, null, new GeoPoint(31.781, 35.211));
        var only = Q(RideKind.Men);
        var ps = ProposalEngine.Build([only], [far, near]);
        Assert.Equal([only.Id], ps.Single(p => p.DriverId == near.Id).RequestIds);
        Assert.Empty(ps.Single(p => p.DriverId == far.Id).RequestIds);
    }

    [Fact]
    public void Regroup_lets_a_group_go_first_only_within_10_minutes_of_the_longest_wait()
    {
        var other = Guid.NewGuid();
        QueueItem There(double wait, double lat) => new(Guid.NewGuid(), RideKind.Women, 1, false, new GeoPoint(lat, 35.30), [other],
            new GeoPoint(lat + 0.002, 35.30), wait, null, false);
        var alone = Q(RideKind.Women, wait: 20);
        var a = There(15, 31.900); var b = There(14, 31.9005); var c = There(13, 31.901);
        // One driver: the regroup takes the three neighbours (waited up to 5 minutes less); the normal order takes the oldest.
        Assert.Equal([alone.Id], ProposalEngine.Build([alone, a, b, c], [D()]).Single().RequestIds);
        Assert.Equal(3, ProposalEngine.Build([alone, a, b, c], [D()], regroup: true).Single().RequestIds.Count);
        // Waited 25 minutes longer than the group: it keeps its turn.
        var longAlone = Q(RideKind.Women, wait: 40);
        Assert.Equal([longAlone.Id], ProposalEngine.Build([longAlone, a, b, c], [D()], regroup: true).Single().RequestIds);
    }

    [Fact]
    public void Start_point_is_the_last_drop_off_then_a_fresh_app_location_then_the_area()
    {
        var now = DateTimeOffset.UtcNow;
        GeoPoint drop = new(31.1, 35.1), app = new(31.2, 35.2), area = new(31.3, 35.3);
        Assert.Equal(drop, ProposalEngine.StartPoint(drop, app, now, area, now));
        Assert.Equal(app, ProposalEngine.StartPoint(null, app, now.AddMinutes(-4), area, now));
        Assert.Equal(area, ProposalEngine.StartPoint(null, app, now.AddMinutes(-6), area, now));
        Assert.Null(ProposalEngine.StartPoint(null, null, null, null, now));
    }

    [Fact]
    public void A_nearer_request_goes_first_only_by_the_minutes_it_saves_in_driving()
    {
        // 1 minute per 0.001 degree; the driver is at the near request.
        ProposalEngine.TravelMinutes t = (a, b) => (Math.Abs(a.Lat - b.Lat) + Math.Abs(a.Lon - b.Lon)) * 1000;
        var driver = new DriverItem(Guid.NewGuid(), "נהג", 4, false, false, null, new GeoPoint(31.780, 35.210));
        QueueItem At(double wait, double lat, Guid cluster) => new(Guid.NewGuid(), RideKind.Men, 1, false, new GeoPoint(lat, 35.210),
            [cluster], new GeoPoint(lat, 35.212), wait, null, false);
        // Near: waited 10, 0 minutes away (score 10). Far: waited 25, 20 minutes away (score 5): the near one goes first.
        var near = At(10, 31.780, Guid.NewGuid());
        var far = At(25, 31.800, Guid.NewGuid());
        Assert.Equal([near.Id], ProposalEngine.Build([far, near], [driver], t).Single().RequestIds);
        // Far waited 40 (score 20): now it goes first, even though it is far.
        var longFar = At(40, 31.800, Guid.NewGuid());
        Assert.Equal([longFar.Id], ProposalEngine.Build([longFar, near], [driver], t).Single().RequestIds);
    }

    [Fact]
    public void A_suspended_request_goes_only_to_a_driver_who_starts_near_it()
    {
        var farAway = new DriverItem(Guid.NewGuid(), "רחוק", 4, false, false, null, new GeoPoint(31.90, 35.30));
        var close = new DriverItem(Guid.NewGuid(), "קרוב", 4, false, false, null, new GeoPoint(31.781, 35.211));
        var suspended = Q(RideKind.Men, wait: 50) with { Suspended = true };
        Assert.Empty(ProposalEngine.Build([suspended], [farAway]).Single().RequestIds);
        Assert.Equal([suspended.Id], ProposalEngine.Build([suspended], [close]).Single().RequestIds);
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
    public async Task Analytics_covers_requests_trips_and_staff_and_the_export_has_no_voter_details()
    {
        var (admin, city, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        await using (var db = api.NewDb())
        {
            var active = await db.Operations.Include(o => o.Cities).FirstOrDefaultAsync(o => o.IsActive)
                ?? db.Operations.Add(new ElectionOperation { Type = ElectionType.Knesset, ElectionDay = new DateOnly(2026, 12, 1), IsActive = true }).Entity;
            active.Cities.Add(await db.Cities.FirstAsync(c => c.Id == city.Id));
            await db.SaveChangesAsync();
        }
        var street = (await Json(await admin.PostAsJsonAsync("/api/city/streets", new { name = "רבי עקיבא" }))).GetProperty("id").GetGuid();
        var cluster = (await Json(await admin.PostAsJsonAsync("/api/city/clusters", new { name = "ישיבה", shortName = "ישיבה", number = 7, boxes = "10-12", streetId = street, buildingNumber = "9", accessible = true }))).GetProperty("id").GetGuid();
        var (op, _, _) = await TestUsers.SignedIn(api, UserRole.Operator, city);
        var (disp, _, _) = await TestUsers.SignedIn(api, UserRole.Dispatcher, city);
        var req = (await Json(await op.PostAsJsonAsync("/api/intake/requests", Req("0527666661", "מרים ויס", "000000083", "Female", street, cluster, wc: true)))).GetProperty("id").GetGuid();
        var drv = (await Json(await disp.PostAsJsonAsync("/api/dispatch/drivers", new { phone = "0548000781", fullName = "אליהו רוזן", seats = 4, wheelchairCar = true, hasSmartphone = false }))).GetProperty("id").GetGuid();
        await disp.PostAsJsonAsync("/api/dispatch/trips", new { driverId = drv, requestIds = new[] { req } });

        var a = await Json(await admin.GetAsync("/api/city/analytics"));
        var totals = a.GetProperty("totals");
        Assert.Equal(1, totals.GetProperty("requests").GetInt32());
        Assert.Equal(1, totals.GetProperty("trips").GetInt32());
        Assert.Equal(1, a.GetProperty("needs").GetProperty("wheelchair").GetInt32());
        Assert.Equal("אליהו רוזן", a.GetProperty("drivers")[0].GetProperty("name").GetString());
        Assert.Equal(1, a.GetProperty("operators")[0].GetProperty("requests").GetInt32());
        Assert.Equal(1, a.GetProperty("clusters")[0].GetProperty("requests").GetInt32());

        var csv = await admin.GetAsync("/api/city/analytics/export");
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("כיסא גלגלים", text);
        Assert.Contains("אליהו רוזן", text);
        Assert.DoesNotContain("מרים ויס", text);
        Assert.DoesNotContain("0527666661", text);
        Assert.Equal(HttpStatusCode.Forbidden, (await op.GetAsync("/api/city/analytics")).StatusCode);
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
    public void Average_wait_counts_every_request_of_the_day_until_it_was_assigned()
    {
        var now = new DateTimeOffset(2026, 12, 15, 12, 0, 0, TimeSpan.Zero);
        DashboardEndpoints.WaitItem Item(int savedAgo, int? assignedAgo, RequestStatus status) =>
            new(now.AddMinutes(-savedAgo), TimeMode.Now, null, assignedAgo is { } a ? now.AddMinutes(-a) : null, status);
        var items = new[]
        {
            Item(60, 50, RequestStatus.Arrived),   // waited 10
            Item(40, 20, RequestStatus.Assigned),  // waited 20
            Item(30, null, RequestStatus.Queued),  // waiting 30 so far
            Item(90, null, RequestStatus.Cancelled), // left out
        };
        Assert.Equal(20, DashboardEndpoints.AverageWait(now, items));
        Assert.Equal(0, DashboardEndpoints.AverageWait(now, []));
    }

    [Fact]
    public async Task A_pickup_window_that_has_not_started_stays_out_of_the_queue()
    {
        var israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
        var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, israel);
        if (local.Hour >= 22) return; // the window must start later today
        var (op, disp, street, cluster) = await Setup();
        var from = new TimeOnly(local.Hour + 1, 0);
        var body = new
        {
            callerPhone = "0527444441",
            passengers = new[] { new { personId = (Guid?)null, fullName = "חנה ברגר", idNumber = "000000091", noId = false, gender = "Female", clusterId = (Guid?)cluster } },
            streetId = street, buildingNumber = "41", entrance = "", driverNotes = "", communityId = (Guid?)null,
            needsWheelchair = false, needsWalker = false, needsHelp = false, timeMode = "Between",
            windowFrom = from.ToString("HH:mm"), windowTo = from.AddHours(1).ToString("HH:mm"),
            urgencyReason = (string?)null, urgencyOther = (string?)null, returnMode = "DriverWaits", keepNonAccessible = false, ignoreDuplicate = false,
        };
        var saved = await op.PostAsJsonAsync("/api/intake/requests", body);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var id = (await Json(saved)).GetProperty("id").GetGuid();

        var state = await Json(await disp.GetAsync("/api/dispatch/state"));
        Assert.DoesNotContain(state.GetProperty("queue").EnumerateArray(), q => q.GetProperty("id").GetGuid() == id);
        Assert.True(state.GetProperty("later").GetInt32() >= 1);
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
