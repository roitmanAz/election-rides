using System.Security.Claims;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.CityScreens;

public record NameBody(string Name);

/// <summary>
/// Communities and streets (spec 3.1): the same list behaviour for both. Every staff member of the
/// city can read them (the operator picks from them); only the city admin changes them.
/// A name appears once per city.
/// </summary>
public static class CityListEndpoints
{
    const string Duplicate = "השם כבר ברשימה";
    const string Empty = "צריך למלא שם";

    public static void MapCityLists(this WebApplication app)
    {
        var g = app.MapGroup("/api/city").RequireAuthorization(Policies.CityStaff);

        // ---- Communities ----
        g.MapGet("/communities", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
            await db.Communities.Where(c => c.CityId == me.CityId()).OrderBy(c => c.Name)
                .Select(c => new { c.Id, c.Name }).ToListAsync(ct));

        g.MapPost("/communities", async (NameBody body, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var name = Rules.NormalizeName(body.Name);
            if (name.Length == 0) return Problems.Error(Empty);
            var city = me.CityId();
            if (await db.Communities.AnyAsync(c => c.CityId == city && c.Name == name, ct)) return Problems.Error(Duplicate, 409);
            var c = new Community { CityId = city, Name = name };
            db.Communities.Add(c);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { c.Id, c.Name });
        }).RequireAuthorization(Policies.CityAdmin);

        g.MapPut("/communities/{id:guid}", async (Guid id, NameBody body, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var name = Rules.NormalizeName(body.Name);
            if (name.Length == 0) return Problems.Error(Empty);
            var city = me.CityId();
            var c = await db.Communities.FirstOrDefaultAsync(x => x.Id == id && x.CityId == city, ct);
            if (c is null) return Results.NotFound();
            if (await db.Communities.AnyAsync(x => x.CityId == city && x.Name == name && x.Id != id, ct)) return Problems.Error(Duplicate, 409);
            c.Name = name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { c.Id, c.Name });
        }).RequireAuthorization(Policies.CityAdmin);

        g.MapDelete("/communities/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var n = await db.Communities.Where(x => x.Id == id && x.CityId == me.CityId()).ExecuteDeleteAsync(ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization(Policies.CityAdmin);

        // ---- Streets ----
        g.MapGet("/streets", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
            await db.Streets.Where(s => s.CityId == me.CityId()).OrderBy(s => s.Name)
                .Select(s => new { s.Id, s.Name, source = s.Source.ToString() }).ToListAsync(ct));

        g.MapPost("/streets", async (NameBody body, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var name = Rules.NormalizeName(body.Name);
            if (name.Length == 0) return Problems.Error(Empty);
            var city = me.CityId();
            if (await db.Streets.AnyAsync(s => s.CityId == city && s.Name == name, ct)) return Problems.Error(Duplicate, 409);
            var s = new Street { CityId = city, Name = name, Source = StreetSource.Manual };
            db.Streets.Add(s);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { s.Id, s.Name, source = s.Source.ToString() });
        }).RequireAuthorization(Policies.CityAdmin);

        g.MapPut("/streets/{id:guid}", async (Guid id, NameBody body, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var name = Rules.NormalizeName(body.Name);
            if (name.Length == 0) return Problems.Error(Empty);
            var city = me.CityId();
            var s = await db.Streets.FirstOrDefaultAsync(x => x.Id == id && x.CityId == city, ct);
            if (s is null) return Results.NotFound();
            if (await db.Streets.AnyAsync(x => x.CityId == city && x.Name == name && x.Id != id, ct)) return Problems.Error(Duplicate, 409);
            s.Name = name;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { s.Id, s.Name, source = s.Source.ToString() });
        }).RequireAuthorization(Policies.CityAdmin);

        g.MapDelete("/streets/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var city = me.CityId();
            if (await db.Clusters.AnyAsync(c => c.CityId == city && c.StreetId == id, ct))
                return Problems.Error("יש אשכול ברחוב הזה. צריך קודם לשנות את הכתובת של האשכול.", 409);
            var n = await db.Streets.Where(x => x.Id == id && x.CityId == city).ExecuteDeleteAsync(ct);
            return n == 0 ? Results.NotFound() : Results.NoContent();
        }).RequireAuthorization(Policies.CityAdmin);
    }
}
