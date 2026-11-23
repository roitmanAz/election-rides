using System.Security.Claims;
using System.Text.Json;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Intake;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.VoterBase;

/// <summary>
/// The city admin's voter base screen (spec 3.1, 8): status, preview of a file's columns, import with column
/// mapping, delete now, and the deletion certificates. The file is read twice (preview, import) and never stored.
/// </summary>
public static class VoterBaseEndpoints
{
    const long MaxFile = 300_000_000;

    public static void MapVoterBase(this WebApplication app)
    {
        var g = app.MapGroup("/api/city/base").RequireAuthorization(Policies.CityAdmin);

        g.MapGet("", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var city = me.CityId();
            var info = await db.VoterBases.FirstOrDefaultAsync(b => b.CityId == city, ct);
            var party = await db.PartySettings.SingleAsync(ct);
            var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
            var cityRow = await db.Cities.FirstAsync(c => c.Id == city, ct);
            var day = cityRow.ElectionDayOverride ?? op?.ElectionDay;
            var certificates = await db.DeletionCertificates.Where(c => c.CityId == city).OrderByDescending(c => c.Number)
                .Select(c => new { c.Id, c.Number, c.FileName, c.VoterCount, c.DeletedAt, c.DeletedBy }).ToListAsync(ct);
            return Results.Ok(new
            {
                loaded = info is not null, info?.FileName, info?.VoterCount, info?.LoadedAt, info?.LoadedBy,
                deletionDate = day is not null && party.BaseDeletionDays is { } days ? day.Value.AddDays(days) : (DateOnly?)null,
                deletionDaysSet = party.BaseDeletionDays is not null,
                certificates,
            });
        });

        g.MapPost("/preview", async (IFormFile file, CancellationToken ct) =>
        {
            try
            {
                await using var s = file.OpenReadStream();
                var t = TableFile.Read(s, file.FileName);
                return Results.Ok(new { headers = t.Headers, sample = t.Rows.Take(5), rowCount = t.Rows.Count });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Problems.Error(ex is InvalidDataException ? ex.Message : "לא הצלחתי לקרוא את הקובץ. אפשר לשמור אותו מחדש כ-xlsx או CSV.");
            }
        }).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MaxFile), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxFile });

        g.MapPost("/import", async ([FromForm] IFormFile file, [FromForm] string mapping, ClaimsPrincipal me, AppDbContext db, VoterBaseService svc, CancellationToken ct) =>
        {
            ColumnMap? map;
            try { map = JsonSerializer.Deserialize<ColumnMap>(mapping, JsonSerializerOptions.Web); }
            catch (JsonException) { map = null; }
            if (map is null) return Problems.Error("צריך להתאים את העמודות");
            var user = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            try
            {
                await using var s = file.OpenReadStream();
                var table = TableFile.Read(s, file.FileName);
                return Results.Ok(await svc.ImportAsync(me.CityId(), table, map, file.FileName, user, ct));
            }
            catch (InvalidDataException ex)
            {
                return Problems.Error(ex.Message);
            }
        }).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MaxFile), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = MaxFile });

        g.MapDelete("", async (ClaimsPrincipal me, AppDbContext db, VoterBaseService svc, CancellationToken ct) =>
        {
            var user = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            var cert = await svc.DeleteAsync(me.CityId(), user.FullName, "מחיקה ידנית של מנהל העיר", ct);
            return cert is null ? Problems.Error("אין בייס טעון", 409) : Results.Ok(new { cert.Id, cert.Number });
        });

        g.MapGet("/certificates/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var c = await db.DeletionCertificates.FirstOrDefaultAsync(x => x.Id == id && x.CityId == me.CityId(), ct);
            return c is null ? Results.NotFound() : Results.Ok(c);
        });
    }
}

/// <summary>
/// Deletes each city's base when the set number of days after its election day has passed (spec 12), with a
/// certificate signed "automatic deletion". Without a number of days set by the system admin, nothing is deleted.
/// </summary>
public class AutoDeletion(IServiceProvider sp, ILogger<AutoDeletion> log) : BackgroundService
{
    static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await RunOnce(stop);
            }
            catch (Exception ex) when (!stop.IsCancellationRequested)
            {
                log.LogError(ex, "Automatic deletion of voter bases failed; will try again in an hour");
            }
        } while (await timer.WaitForNextTickAsync(stop));
    }

    public async Task RunOnce(CancellationToken ct)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var party = await db.PartySettings.SingleAsync(ct);
        if (party.BaseDeletionDays is not { } days) return;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Israel).DateTime);
        var cities = await db.VoterBases.Select(b => b.CityId).ToListAsync(ct);
        foreach (var cityId in cities)
        {
            var city = await db.Cities.FirstAsync(c => c.Id == cityId, ct);
            var op = await db.Operations.Where(o => o.Cities.Any(c => c.Id == cityId)).OrderByDescending(o => o.ElectionDay).FirstOrDefaultAsync(ct);
            var day = city.ElectionDayOverride ?? op?.ElectionDay;
            if (day is null || today < day.Value.AddDays(days)) continue;
            await scope.ServiceProvider.GetRequiredService<VoterBaseService>().DeleteAsync(cityId, VoterBaseService.AutomaticDeletion, $"{days} ימים אחרי יום הבחירות", ct);
            log.LogInformation("Voter base of {City} deleted automatically", city.Name);
        }
    }
}
