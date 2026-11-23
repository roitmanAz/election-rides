using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Tests;

[Collection("api")]
public class DriverAppTests(ApiFactory api)
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    async Task<(HttpClient op, HttpClient disp, HttpClient app, Guid driver, Guid street, Guid cluster)> Setup()
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
        await admin.PutAsJsonAsync("/api/city/settings", new { hqPhone = "026543210" });
        var street = (await Json(await admin.PostAsJsonAsync("/api/city/streets", new { name = "הרב קוק" }))).GetProperty("id").GetGuid();
        var cluster = (await Json(await admin.PostAsJsonAsync("/api/city/clusters", new { name = "תלמוד תורה", shortName = "תלמוד תורה", number = 3, boxes = "7-9", streetId = street, buildingNumber = "2", accessible = true }))).GetProperty("id").GetGuid();
        var (op, _, _) = await TestUsers.SignedIn(api, UserRole.Operator, city);
        var (disp, _, _) = await TestUsers.SignedIn(api, UserRole.Dispatcher, city);
        var phone = "05480" + Random.Shared.Next(10000, 99999);
        var driver = (await Json(await disp.PostAsJsonAsync("/api/dispatch/drivers", new { phone, fullName = "ישראל שטיין", seats = 6, wheelchairCar = false, hasSmartphone = true }))).GetProperty("id").GetGuid();

        var link = (await Json(await disp.PostAsync($"/api/dispatch/drivers/{driver}/link", null))).GetProperty("url").GetString()!;
        var token = link[(link.LastIndexOf('/') + 1)..];
        var app = api.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsJsonAsync("/api/driver/session", new { token })).StatusCode);
        // The link works once only.
        Assert.Equal(HttpStatusCode.Gone, (await api.CreateClient().PostAsJsonAsync("/api/driver/session", new { token })).StatusCode);
        return (op, disp, app, driver, street, cluster);
    }

    async Task<Guid> NewRequest(HttpClient op, Guid street, Guid cluster, string phone, string id) =>
        (await Json(await op.PostAsJsonAsync("/api/intake/requests", new
        {
            callerPhone = phone,
            passengers = new[] { new { personId = (Guid?)null, fullName = "רבקה כהן", idNumber = id, noId = false, gender = "Female", clusterId = (Guid?)cluster } },
            streetId = street, buildingNumber = "12", entrance = "", driverNotes = "לצלצל באינטרקום", communityId = (Guid?)null,
            needsWheelchair = false, needsWalker = true, needsHelp = false, timeMode = "Now", windowFrom = (string?)null, windowTo = (string?)null,
            urgencyReason = (string?)null, urgencyOther = (string?)null, returnMode = "DriverWaits", keepNonAccessible = false, ignoreDuplicate = true,
        }))).GetProperty("id").GetGuid();

    [Fact]
    public async Task Driver_sees_the_offer_declines_with_a_reason_then_accepts_the_next_and_finishes()
    {
        var (op, disp, app, driver, street, cluster) = await Setup();
        var req = await NewRequest(op, street, cluster, "0527444441", "000000059");
        await disp.PostAsJsonAsync("/api/dispatch/trips", new { driverId = driver, requestIds = new[] { req } });

        var me = await Json(await app.GetAsync("/api/driver/me"));
        Assert.Equal("Offered", me.GetProperty("trip").GetProperty("status").GetString());
        Assert.Equal("026543210", me.GetProperty("hqPhone").GetString());
        var stops = me.GetProperty("trip").GetProperty("stops");
        Assert.Equal("רבקה כהן", stops[0].GetProperty("names")[0].GetString());
        Assert.True(stops[0].GetProperty("comesBack").GetBoolean());
        Assert.Equal("3", stops[1].GetProperty("plate").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await app.PostAsJsonAsync("/api/driver/action", new { action = "Decline", reason = "אחר" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsJsonAsync("/api/driver/action", new { action = "Decline", reason = "רחוק מדי", lat = 31.78, lon = 35.21 })).StatusCode);
        await using (var db = api.NewDb())
        {
            Assert.Equal(RequestStatus.Queued, (await db.Requests.FirstAsync(r => r.Id == req)).Status);
            Assert.NotNull((await db.Drivers.FirstAsync(d => d.Id == driver)).Location);
        }
        var state = await Json(await disp.GetAsync("/api/dispatch/state"));
        Assert.Equal("רחוק מדי", state.GetProperty("drivers").EnumerateArray().First(d => d.GetProperty("id").GetGuid() == driver).GetProperty("lastDecline").GetString());

        await disp.PostAsJsonAsync("/api/dispatch/trips", new { driverId = driver, requestIds = new[] { req } });
        foreach (var a in new[] { "Accept", "Soon5", "Free" })
            Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsJsonAsync("/api/driver/action", new { action = a })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsJsonAsync("/api/driver/action", new { action = "Finish" })).StatusCode);
        Assert.Equal("Finished", (await Json(await app.GetAsync("/api/driver/me"))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsJsonAsync("/api/driver/action", new { action = "BackToWork" })).StatusCode);
    }

    [Fact]
    public async Task Dispatcher_can_withdraw_an_offer_and_sign_ins_do_not_mix()
    {
        var (op, disp, app, driver, street, cluster) = await Setup();
        var req = await NewRequest(op, street, cluster, "0527444442", "000000067");
        await disp.PostAsJsonAsync("/api/dispatch/trips", new { driverId = driver, requestIds = new[] { req } });
        var trip = (await Json(await app.GetAsync("/api/driver/me"))).GetProperty("trip").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await disp.PostAsync($"/api/dispatch/trips/{trip}/withdraw", null)).StatusCode);
        Assert.Equal(JsonValueKind.Null, (await Json(await app.GetAsync("/api/driver/me"))).GetProperty("trip").ValueKind);

        // A driver's sign-in opens only the driver app; staff sign-in does not open it.
        Assert.Contains((await app.GetAsync("/api/dispatch/state")).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.Contains((await disp.GetAsync("/api/driver/me")).StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
    }
}
