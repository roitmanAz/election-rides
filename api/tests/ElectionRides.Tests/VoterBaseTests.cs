using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Email;
using ElectionRides.Api.VoterBase;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElectionRides.Tests;

[Collection("api")]
public class VoterBaseTests(ApiFactory api)
{
    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    // Columns in a different order from the screen, plus a column that must not be stored.
    const string Csv = "﻿מספר זהות,שם משפחה,שם פרטי,קלפי,טלפון בית,נייד,גיל,מקצוע\n" +
                       "032144586,פרידמן,יעקב,2,02-6543210,052-7123456,81,מורה\n" +
                       "000000018,פרידמן,רחל,2,02-6543210,,79,\n" +
                       "123456789,טעות,תז,2,,,,\n" +
                       "000000026,כהן,שמואל,99,,054-1111111,,\n";

    static MultipartFormDataContent Form(string csv, object? mapping = null)
    {
        var f = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        f.Add(file, "file", "base.csv");
        if (mapping is not null) f.Add(new StringContent(JsonSerializer.Serialize(mapping)), "mapping");
        return f;
    }

    static readonly object Map = new { idNumber = 0, lastName = 1, firstName = 2, ballotBox = 3, phones = new[] { 4, 5 }, age = 6 };

    async Task<(HttpClient admin, City city, Guid cluster, Guid street)> CityWithCluster()
    {
        var (admin, city, _) = await TestUsers.SignedIn(api, UserRole.CityAdmin);
        await using (var db = api.NewDb())
        {
            var active = await db.Operations.Include(o => o.Cities).FirstOrDefaultAsync(o => o.IsActive)
                ?? db.Operations.Add(new ElectionOperation { Type = ElectionType.Knesset, ElectionDay = new DateOnly(2026, 12, 1), IsActive = true }).Entity;
            active.Cities.Add(await db.Cities.FirstAsync(c => c.Id == city.Id));
            await db.SaveChangesAsync();
        }
        var street = (await Json(await admin.PostAsJsonAsync("/api/city/streets", new { name = "עזרא" }))).GetProperty("id").GetGuid();
        var cluster = (await Json(await admin.PostAsJsonAsync("/api/city/clusters", new { name = "בית ספר בית יעקב", shortName = "בית יעקב", number = 2, boxes = "1-3", streetId = street, buildingNumber = "4", accessible = true }))).GetProperty("id").GetGuid();
        return (admin, city, cluster, street);
    }

    [Fact]
    public async Task Preview_shows_the_columns_and_import_keeps_only_mapped_fields()
    {
        var (admin, city, cluster, _) = await CityWithCluster();
        var preview = await Json(await admin.PostAsync("/api/city/base/preview", Form(Csv)));
        Assert.Equal("מספר זהות", preview.GetProperty("headers")[0].GetString());
        Assert.Equal(4, preview.GetProperty("rowCount").GetInt32());

        var result = await Json(await admin.PostAsync("/api/city/base/import", Form(Csv, Map)));
        Assert.Equal(3, result.GetProperty("imported").GetInt32());
        Assert.Equal(1, result.GetProperty("skipped").GetInt32());
        Assert.Contains("99", result.GetProperty("boxesWithoutCluster").EnumerateArray().Select(x => x.GetString()));

        await using var db = api.NewDb();
        var yaakov = await db.Voters.Include(v => v.Phones).FirstAsync(v => v.CityId == city.Id && v.IdNumber == "032144586");
        Assert.Equal(cluster, yaakov.ClusterId);
        Assert.Equal(81, yaakov.Age);
        Assert.Equal(2, yaakov.Phones.Count);
        var status = await Json(await admin.GetAsync("/api/city/base"));
        Assert.True(status.GetProperty("loaded").GetBoolean());
        Assert.Equal(3, status.GetProperty("voterCount").GetInt32());
    }

    [Fact]
    public async Task Operator_sees_the_family_on_the_phone_and_a_base_id_is_verified_with_the_real_box()
    {
        var (admin, city, cluster, street) = await CityWithCluster();
        await admin.PostAsync("/api/city/base/import", Form(Csv, Map));
        var (op, _, _) = await TestUsers.SignedIn(api, UserRole.Operator, city);

        var phone = await Json(await op.GetAsync("/api/intake/phone/026543210"));
        Assert.Equal(2, phone.GetProperty("people").GetArrayLength());
        Assert.True(phone.GetProperty("people")[0].GetProperty("inBase").GetBoolean());

        var res = await Json(await op.PostAsJsonAsync("/api/intake/requests", new
        {
            callerPhone = "026543210",
            passengers = new[] { new { personId = (Guid?)null, fullName = "יעקב פרידמן", idNumber = "032144586", noId = false, gender = "Male", clusterId = (Guid?)cluster } },
            streetId = street, buildingNumber = "8", entrance = "", driverNotes = "", communityId = (Guid?)null,
            needsWheelchair = false, needsWalker = false, needsHelp = false, timeMode = "Now", windowFrom = (string?)null, windowTo = (string?)null,
            urgencyReason = (string?)null, urgencyOther = (string?)null, returnMode = "DriverWaits", keepNonAccessible = false, ignoreDuplicate = true,
        }));
        var detail = await Json(await op.GetAsync($"/api/intake/requests/{res.GetProperty("id").GetGuid()}"));
        var p = detail.GetProperty("passengers")[0];
        Assert.False(p.GetProperty("pendingVerification").GetBoolean());
        Assert.Equal("2", p.GetProperty("ballotBox").GetString());
    }

    [Fact]
    public async Task Deleting_the_base_makes_a_certificate_sent_to_the_privacy_officer_and_keeps_requests()
    {
        var (admin, city, _, _) = await CityWithCluster();
        await using (var db = api.NewDb())
        {
            var party = await db.PartySettings.SingleAsync();
            party.PrivacyOfficerEmail = "privacy@party.test";
            await db.SaveChangesAsync();
        }
        await admin.PostAsync("/api/city/base/import", Form(Csv, Map));
        var del = await Json(await admin.DeleteAsync("/api/city/base"));
        var cert = await Json(await admin.GetAsync($"/api/city/base/certificates/{del.GetProperty("id").GetGuid()}"));
        Assert.Equal(3, cert.GetProperty("voterCount").GetInt32());
        Assert.Contains("privacy@party.test", cert.GetProperty("sentTo").GetString());
        Assert.Contains(api.Services.GetRequiredService<DevEmailSender>().Sent, m => m.To == "privacy@party.test" && m.Subject.Contains("אישור מחיקה"));

        await using var db2 = api.NewDb();
        Assert.False(await db2.Voters.AnyAsync(v => v.CityId == city.Id));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync("/api/city/base")).StatusCode);
    }

    [Fact]
    public async Task Base_is_deleted_automatically_the_set_number_of_days_after_election_day()
    {
        var (admin, city, _, _) = await CityWithCluster();
        await admin.PostAsync("/api/city/base/import", Form(Csv, Map));
        await using (var db = api.NewDb())
        {
            (await db.Cities.FirstAsync(c => c.Id == city.Id)).ElectionDayOverride = DateOnly.FromDateTime(DateTime.Today.AddDays(-40));
            (await db.PartySettings.SingleAsync()).BaseDeletionDays = 30;
            await db.SaveChangesAsync();
        }
        try
        {
            await api.Services.GetRequiredService<AutoDeletion>().RunOnce(default);
            await using var db = api.NewDb();
            Assert.False(await db.Voters.AnyAsync(v => v.CityId == city.Id));
            Assert.Equal(VoterBaseService.AutomaticDeletion, (await db.DeletionCertificates.Where(c => c.CityId == city.Id).FirstAsync()).DeletedBy);
        }
        finally
        {
            await using var db = api.NewDb();
            (await db.PartySettings.SingleAsync()).BaseDeletionDays = null;
            await db.SaveChangesAsync();
        }
    }
}
