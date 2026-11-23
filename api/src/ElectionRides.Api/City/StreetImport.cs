using System.Security.Claims;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using ElectionRides.Api.GovData;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.CityScreens;

/// <summary>
/// Imports the city's streets from the official list (spec 3.1). Re-import updates names, adds new
/// streets and removes official streets that left the list, unless a cluster uses them.
/// Manually added streets are always kept; an official street whose name a manual street already has is skipped.
/// </summary>
public static class StreetImport
{
    public record Result(int Added, int Renamed, int Removed, int Total);

    public static async Task<Result> RunAsync(AppDbContext db, IGovData gov, Guid cityId, int localityCode, CancellationToken ct)
    {
        var official = await gov.StreetsAsync(localityCode, ct);
        var existing = await db.Streets.Where(s => s.CityId == cityId).ToListAsync(ct);
        var usedByCluster = (await db.Clusters.Where(c => c.CityId == cityId).Select(c => c.StreetId).ToListAsync(ct)).ToHashSet();
        var byCode = existing.Where(s => s.OfficialCode is not null).ToDictionary(s => s.OfficialCode!.Value);
        var names = existing.Select(s => s.Name).ToHashSet();
        int added = 0, renamed = 0, removed = 0;

        foreach (var o in official)
        {
            if (byCode.TryGetValue(o.Code, out var s))
            {
                if (s.Name != o.Name && !names.Contains(o.Name))
                {
                    names.Remove(s.Name);
                    s.Name = o.Name;
                    names.Add(o.Name);
                    renamed++;
                }
            }
            else if (names.Add(o.Name))
            {
                db.Streets.Add(new Street { CityId = cityId, Name = o.Name, Source = StreetSource.Official, OfficialCode = o.Code });
                added++;
            }
        }

        var codes = official.Select(o => o.Code).ToHashSet();
        foreach (var s in existing.Where(s => s.Source == StreetSource.Official && !codes.Contains(s.OfficialCode ?? -1) && !usedByCluster.Contains(s.Id)))
        {
            db.Streets.Remove(s);
            removed++;
        }

        await db.SaveChangesAsync(ct);
        var total = await db.Streets.CountAsync(s => s.CityId == cityId, ct);
        return new Result(added, renamed, removed, total);
    }

    public static void MapStreetImport(this WebApplication app) =>
        app.MapPost("/api/city/streets/import", async (ClaimsPrincipal me, AppDbContext db, IGovData gov, CancellationToken ct) =>
        {
            var city = await db.Cities.FirstAsync(c => c.Id == me.CityId(), ct);
            if (city.LocalityCode is null) return Problems.Error("לעיר אין סמל יישוב, ולכן אי אפשר לייבא רחובות. אפשר לפנות למנהל המערכת.");
            Result r;
            try
            {
                r = await RunAsync(db, gov, city.Id, city.LocalityCode.Value, ct);
            }
            catch (HttpRequestException)
            {
                return Problems.Error("המאגר הרשמי לא זמין כרגע. אפשר לנסות שוב מאוחר יותר.", 502);
            }
            var user = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            Audit.Add(db, user, "streets.import", $"נוספו {r.Added}, שונו {r.Renamed}, הוסרו {r.Removed}");
            await db.SaveChangesAsync(ct);
            return Results.Ok(r);
        }).RequireAuthorization(Policies.CityAdmin);
}
