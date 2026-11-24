using System.Security.Claims;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.CityScreens;

public record CatalogNameBody(string Name);

/// <summary>
/// The shared community list (spec 3.1, 3.2).
/// City admin: picks communities for the city, adds a new one to the shared list (no approval: it only
/// adds a name others may pick), and asks to rename one (applies to every city, so a system admin approves).
/// System admin: adds and renames directly, and approves or rejects rename requests.
/// </summary>
public static class CommunityCatalogEndpoints
{
    public static void MapCommunityCatalog(this WebApplication app)
    {
        // ---------------- City admin ----------------
        var city = app.MapGroup("/api/city/communities/catalog").RequireAuthorization(Policies.CityAdmin);

        city.MapGet("", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var cityId = me.CityId();
            var picked = (await db.Communities.Where(c => c.CityId == cityId && c.CatalogId != null).Select(c => c.CatalogId!.Value).ToListAsync(ct)).ToHashSet();
            var pending = await db.CommunityRenames.Where(r => r.CityId == cityId && r.Status == RenameStatus.Pending)
                .ToDictionaryAsync(r => r.CatalogId, r => r.NewName, ct);
            return (await db.CommunityCatalog.OrderBy(c => c.Name).ToListAsync(ct))
                .Select(c => new { c.Id, c.Name, chosen = picked.Contains(c.Id), pendingName = pending.GetValueOrDefault(c.Id) });
        });

        // A new name goes into the shared list and is picked for this city at once.
        city.MapPost("/new", async (CatalogNameBody b, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var name = Rules.NormalizeName(b.Name);
            if (name.Length == 0) return Problems.Error("צריך למלא שם");
            if (name.Length > 60) return Problems.Error("השם ארוך מדי");
            var cityId = me.CityId();
            var item = await db.CommunityCatalog.FirstOrDefaultAsync(c => c.Name == name, ct);
            if (item is null)
            {
                item = new CommunityCatalogItem { Id = (await db.CommunityCatalog.MaxAsync(c => (int?)c.Id, ct) ?? 0) + 1, Name = name };
                db.CommunityCatalog.Add(item);
                Audit.Add(db, await Actor(db, me, ct), "catalog.add", name, cityId);
            }
            await Pick(db, cityId, item, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { item.Id, item.Name });
        });

        // Renaming changes every city, so it waits for a system admin. One open request per community per city.
        city.MapPost("/{id:int}/rename", async (int id, CatalogNameBody b, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var item = await db.CommunityCatalog.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item is null) return Results.NotFound();
            var name = Rules.NormalizeName(b.Name);
            if (name.Length == 0) return Problems.Error("צריך למלא שם");
            if (name == item.Name) return Problems.Error("זה כבר השם");
            if (await db.CommunityCatalog.AnyAsync(c => c.Name == name, ct)) return Problems.Error("השם כבר ברשימה הכללית", 409);
            var cityId = me.CityId();
            var actor = await Actor(db, me, ct);
            await db.CommunityRenames.Where(r => r.CatalogId == id && r.CityId == cityId && r.Status == RenameStatus.Pending).ExecuteDeleteAsync(ct);
            db.CommunityRenames.Add(new CommunityRename { CatalogId = id, CityId = cityId, OldName = item.Name, NewName = name, RequestedById = actor.Id });
            Audit.Add(db, actor, "catalog.rename-request", $"מ-{item.Name} ל-{name}", cityId);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---------------- System admin ----------------
        var sys = app.MapGroup("/api/admin/catalog").RequireAuthorization(Policies.SystemAdmin);

        sys.MapGet("", async (AppDbContext db, CancellationToken ct) =>
        {
            var used = await db.Communities.Where(c => c.CatalogId != null).GroupBy(c => c.CatalogId!.Value)
                .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
            var items = (await db.CommunityCatalog.OrderBy(c => c.Name).ToListAsync(ct))
                .Select(c => new { c.Id, c.Name, cities = used.GetValueOrDefault(c.Id) });
            var cities = await db.Cities.ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var requests = (await db.CommunityRenames.Where(r => r.Status == RenameStatus.Pending).OrderBy(r => r.RequestedAt)
                .Join(db.Users, r => r.RequestedById, u => u.Id, (r, u) => new { r.Id, r.CatalogId, r.OldName, r.NewName, r.CityId, r.RequestedAt, by = u.FullName })
                .ToListAsync(ct))
                .Select(r => new { r.Id, r.CatalogId, r.OldName, r.NewName, city = cities.GetValueOrDefault(r.CityId), r.RequestedAt, r.by });
            return Results.Ok(new { items, requests });
        });

        sys.MapPost("", async (CatalogNameBody b, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var name = Rules.NormalizeName(b.Name);
            if (name.Length == 0) return Problems.Error("צריך למלא שם");
            if (await db.CommunityCatalog.AnyAsync(c => c.Name == name, ct)) return Problems.Error("השם כבר ברשימה הכללית", 409);
            var item = new CommunityCatalogItem { Id = (await db.CommunityCatalog.MaxAsync(c => (int?)c.Id, ct) ?? 0) + 1, Name = name };
            db.CommunityCatalog.Add(item);
            Audit.Add(db, await Actor(db, me, ct), "catalog.add", name);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { item.Id, item.Name });
        });

        sys.MapPut("/{id:int}", async (int id, CatalogNameBody b, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var item = await db.CommunityCatalog.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item is null) return Results.NotFound();
            var old = item.Name;
            var error = await Rename(db, item, Rules.NormalizeName(b.Name), ct);
            if (error is not null) return error;
            await db.CommunityRenames.Where(r => r.CatalogId == id && r.Status == RenameStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RenameStatus.Rejected).SetProperty(r => r.DecidedAt, DateTimeOffset.UtcNow), ct);
            Audit.Add(db, await Actor(db, me, ct), "catalog.rename", $"מ-{old} ל-{item.Name}");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Deleting leaves every city's list too (like unticking); where requests already used the
        // community, that city keeps it as its own hand-added one, so past requests keep their community.
        sys.MapDelete("/{id:int}", async (int id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var item = await db.CommunityCatalog.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (item is null) return Results.NotFound();
            var linked = await db.Communities.Where(c => c.CatalogId == id).ToListAsync(ct);
            var linkedIds = linked.Select(c => c.Id).ToList();
            var used = (await db.Requests.Where(r => r.CommunityId != null && linkedIds.Contains(r.CommunityId.Value))
                .Select(r => r.CommunityId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
            foreach (var c in linked)
            {
                if (used.Contains(c.Id)) c.CatalogId = null;
                else db.Communities.Remove(c);
            }
            db.CommunityCatalog.Remove(item);
            Audit.Add(db, await Actor(db, me, ct), "catalog.delete",
                $"{item.Name}, הוסרה מ-{linked.Count - used.Count} ערים" + (used.Count > 0 ? $", נשארה ידנית ב-{used.Count} שכבר יש בהן פניות איתה" : ""));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        sys.MapPost("/requests/{id:guid}/{decision}", async (Guid id, string decision, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var r = await db.CommunityRenames.FirstOrDefaultAsync(x => x.Id == id && x.Status == RenameStatus.Pending, ct);
            if (r is null) return Problems.Error("הבקשה כבר טופלה", 409);
            var actor = await Actor(db, me, ct);
            if (decision == "approve")
            {
                var item = await db.CommunityCatalog.FirstAsync(c => c.Id == r.CatalogId, ct);
                var old = item.Name;
                var error = await Rename(db, item, r.NewName, ct);
                if (error is not null) return error;
                r.Status = RenameStatus.Approved;
                Audit.Add(db, actor, "catalog.rename", $"מ-{old} ל-{item.Name} (בקשה מאושרת)", r.CityId);
            }
            else if (decision == "reject")
            {
                r.Status = RenameStatus.Rejected;
                Audit.Add(db, actor, "catalog.rename-reject", $"מ-{r.OldName} ל-{r.NewName}", r.CityId);
            }
            else return Results.NotFound();
            r.DecidedById = actor.Id;
            r.DecidedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    static async Task<AppUser> Actor(AppDbContext db, ClaimsPrincipal me, CancellationToken ct) => await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);

    /// <summary>Picks a shared-list item for a city; a hand-added community of the same name is linked instead of doubled.</summary>
    public static async Task Pick(AppDbContext db, Guid cityId, CommunityCatalogItem item, CancellationToken ct)
    {
        var mine = await db.Communities.Where(c => c.CityId == cityId && (c.CatalogId == item.Id || c.Name == item.Name)).ToListAsync(ct);
        if (mine.Any(c => c.CatalogId == item.Id)) return;
        var same = mine.FirstOrDefault(c => c.CatalogId == null);
        if (same is not null) same.CatalogId = item.Id;
        else db.Communities.Add(new Community { CityId = cityId, Name = item.Name, CatalogId = item.Id });
    }

    /// <summary>Renames the shared item and the community in every city that picked it.</summary>
    static async Task<IResult?> Rename(AppDbContext db, CommunityCatalogItem item, string name, CancellationToken ct)
    {
        if (name.Length == 0) return Problems.Error("צריך למלא שם");
        if (name.Length > 60) return Problems.Error("השם ארוך מדי");
        if (await db.CommunityCatalog.AnyAsync(c => c.Name == name && c.Id != item.Id, ct)) return Problems.Error("השם כבר ברשימה הכללית", 409);
        var linked = await db.Communities.Where(c => c.CatalogId == item.Id).ToListAsync(ct);
        var cityIds = linked.Select(c => c.CityId).ToList();
        var clash = await db.Communities.Where(c => cityIds.Contains(c.CityId) && c.Name == name && c.CatalogId != item.Id)
            .Join(db.Cities, c => c.CityId, x => x.Id, (c, x) => x.Name).ToListAsync(ct);
        if (clash.Count > 0) return Problems.Error($"ב{string.Join(", ", clash)} כבר יש קהילה ידנית בשם הזה", 409);
        item.Name = name;
        foreach (var c in linked) c.Name = name;
        return null;
    }
}
