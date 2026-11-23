using System.Security.Claims;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.CityScreens;

public record ClusterBody(
    string Name, string ShortName, int? Number, string Boxes,
    Guid? StreetId, string BuildingNumber, bool? Accessible, string? DropoffNote);

/// <summary>Clusters (spec 3.1): every staff member reads them, the city admin edits them.</summary>
public static class ClusterEndpoints
{
    public static void MapClusters(this WebApplication app)
    {
        var g = app.MapGroup("/api/city/clusters").RequireAuthorization(Policies.CityStaff);

        g.MapGet("", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var city = me.CityId();
            var list = await db.Clusters.Where(c => c.CityId == city)
                .Include(c => c.Street).Include(c => c.Boxes)
                .OrderBy(c => c.Number == null).ThenBy(c => c.Number).ThenBy(c => c.ShortName)
                .ToListAsync(ct);
            return list.Select(Dto);
        });

        g.MapPost("", async (ClusterBody body, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var c = new Cluster { CityId = me.CityId(), Name = "", ShortName = "", BuildingNumber = "" };
            var error = await Apply(c, body, db, ct);
            if (error is not null) return error;
            db.Clusters.Add(c);
            await db.SaveChangesAsync(ct);
            return Results.Ok(Dto(c));
        }).RequireAuthorization(Policies.CityAdmin);

        g.MapPut("/{id:guid}", async (Guid id, ClusterBody body, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var c = await db.Clusters.Include(x => x.Boxes).FirstOrDefaultAsync(x => x.Id == id && x.CityId == me.CityId(), ct);
            if (c is null) return Results.NotFound();
            var error = await Apply(c, body, db, ct);
            if (error is not null) return error;
            await db.SaveChangesAsync(ct);
            return Results.Ok(Dto(c));
        }).RequireAuthorization(Policies.CityAdmin);

        g.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var n = await db.Clusters.Where(x => x.Id == id && x.CityId == me.CityId()).ExecuteDeleteAsync(ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization(Policies.CityAdmin);
    }

    /// <summary>Validates the form and copies it onto the cluster. Returns an error result, or null when valid.</summary>
    static async Task<IResult?> Apply(Cluster c, ClusterBody b, AppDbContext db, CancellationToken ct)
    {
        var name = Rules.NormalizeName(b.Name);
        var shortName = Rules.NormalizeName(b.ShortName);
        var building = (b.BuildingNumber ?? "").Trim();
        if (name.Length == 0) return Problems.Error("צריך למלא את שם המקום");
        if (shortName.Length == 0) return Problems.Error("צריך למלא שם קצר ללוחית");
        if (shortName.Length > 14) return Problems.Error("השם הקצר צריך להיות עד 14 תווים");
        if (b.Number is < 1 or > 9999) return Problems.Error("מספר האשכול צריך להיות בין 1 ל-9999");
        if (b.StreetId is null) return Problems.Error("צריך לבחור רחוב מהרשימה");
        if (building.Length == 0 || building.Length > 10) return Problems.Error("צריך למלא מספר בניין");
        if (b.Accessible is null) return Problems.Error("צריך לבחור אם המקום נגיש לכיסא גלגלים");
        var boxes = Rules.ParseBoxes(b.Boxes);
        if (boxes is null) return Problems.Error("לא הצלחתי לקרוא את הקלפיות. אפשר לכתוב מספר, טווח (31-35) או רשימה (10, 12, 14-16)");

        var city = c.CityId;
        if (!await db.Streets.AnyAsync(s => s.Id == b.StreetId && s.CityId == city, ct))
            return Problems.Error("הרחוב לא נמצא ברשימת הרחובות של העיר");
        if (await db.Clusters.AnyAsync(x => x.CityId == city && x.Id != c.Id && x.ShortName == shortName, ct))
            return Problems.Error("כבר יש אשכול עם השם הקצר הזה", 409);
        if (b.Number is not null && await db.Clusters.AnyAsync(x => x.CityId == city && x.Id != c.Id && x.Number == b.Number, ct))
            return Problems.Error($"כבר יש אשכול מספר {b.Number}", 409);
        var taken = await db.BallotBoxes
            .Where(x => x.CityId == city && x.ClusterId != c.Id && boxes.Contains(x.Number))
            .Select(x => x.Number).ToListAsync(ct);
        if (taken.Count > 0) return Problems.Error($"הקלפיות {string.Join(", ", taken)} כבר שייכות לאשכול אחר", 409);

        c.Name = name;
        c.ShortName = shortName;
        c.Number = b.Number;
        c.StreetId = b.StreetId.Value;
        c.BuildingNumber = building;
        c.Accessible = b.Accessible.Value;
        c.DropoffNote = string.IsNullOrWhiteSpace(b.DropoffNote) ? null : b.DropoffNote.Trim();

        // Replace the box list: keep the ones still listed, add the new ones.
        c.Boxes.RemoveAll(x => !boxes.Contains(x.Number));
        foreach (var n in boxes.Where(n => c.Boxes.All(x => x.Number != n)))
            c.Boxes.Add(new BallotBox { ClusterId = c.Id, CityId = city, Number = n });
        c.Street = await db.Streets.FirstAsync(s => s.Id == c.StreetId, ct);
        return null;
    }

    static object Dto(Cluster c) => new
    {
        c.Id, c.Number, c.Name, c.ShortName, c.Accessible, c.DropoffNote, c.BuildingNumber,
        streetId = c.StreetId, street = c.Street?.Name,
        boxes = c.Boxes.Select(b => b.Number).OrderBy(n => double.TryParse(n, out var d) ? d : 0).ToList(),
    };
}
