using System.Security.Claims;
using ElectionRides.Api.Auth;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using ElectionRides.Api.GovData;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.SystemAdmin;

public record NewCityBody(int LocalityCode, string AdminName, string AdminEmail, Guid? CopyFromCityId);
public record NewOperationBody(string Type, DateOnly? ElectionDay, List<Guid> CityIds);

/// <summary>
/// The first part of the system admin screen (spec 3.2), built early so cities can be opened:
/// the city list with setup progress, and a new city with its first city admin.
/// The system admin sees counts only, never voter or caller data.
/// </summary>
public static class CityAdminEndpoints
{
    public static void MapSystemCities(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(Policies.SystemAdmin);

        g.MapGet("/localities", async (string? q, IGovData gov, AppDbContext db, CancellationToken ct) =>
        {
            var text = Rules.NormalizeName(q);
            if (text.Length < 2) return Results.Ok(Array.Empty<Locality>());
            var taken = (await db.Cities.Where(c => c.LocalityCode != null).Select(c => c.LocalityCode!.Value).ToListAsync(ct)).ToHashSet();
            try
            {
                var all = await gov.LocalitiesAsync(ct);
                return Results.Ok(all.Where(l => l.Name.Contains(text) && !taken.Contains(l.Code))
                    .OrderBy(l => !l.Name.StartsWith(text)).ThenBy(l => l.Name).Take(20));
            }
            catch (HttpRequestException)
            {
                return Problems.Error("רשימת היישובים לא זמינה כרגע. אפשר לנסות שוב מאוחר יותר.", 502);
            }
        });

        g.MapGet("/cities", async (AppDbContext db, CancellationToken ct) =>
        {
            var cities = await db.Cities.OrderBy(c => c.Name).ToListAsync(ct);
            var ids = cities.Select(c => c.Id).ToList();
            var admins = await db.Users.Where(u => u.CityId != null && ids.Contains(u.CityId.Value) && u.IsActive && u.Role == UserRole.CityAdmin)
                .Select(u => new { u.CityId, u.FullName, u.Email, u.LastLoginAt, invited = u.PasswordHash == null }).ToListAsync(ct);
            var staff = await db.Users.Where(u => u.CityId != null && u.IsActive && (u.Role != UserRole.CityAdmin || u.ExtraRoles.Count > 0))
                .GroupBy(u => u.CityId).Select(x => x.Key).ToListAsync(ct);
            var clusters = await db.Clusters.GroupBy(c => c.CityId).Select(x => x.Key).ToListAsync(ct);
            var streets = await db.Streets.GroupBy(c => c.CityId).Select(x => x.Key).ToListAsync(ct);
            var communities = await db.Communities.GroupBy(c => c.CityId).Select(x => x.Key).ToListAsync(ct);
            var bases = await db.VoterBases.Select(b => b.CityId).ToListAsync(ct);

            return cities.Select(c => new
            {
                c.Id, c.Name, c.IsActive,
                admins = admins.Where(a => a.CityId == c.Id).Select(a => new { a.FullName, a.Email, a.LastLoginAt, a.invited }),
                // The six setup steps (spec 3.2).
                setup = new
                {
                    clusters = clusters.Contains(c.Id),
                    streets = streets.Contains(c.Id),
                    communities = communities.Contains(c.Id),
                    voterBase = bases.Contains(c.Id),
                    users = staff.Contains(c.Id),
                    hqPhone = c.HqPhone != null,
                },
            });
        });

        // Election operations (spec 3.2), the minimum needed to take requests: one active at a time.
        g.MapGet("/operations", async (AppDbContext db, CancellationToken ct) =>
            (await db.Operations.Include(o => o.Cities).OrderByDescending(o => o.ElectionDay).ToListAsync(ct))
                .Select(o => new { o.Id, type = o.Type.ToString(), o.ElectionDay, o.IsActive, o.ClosedAt, cities = o.Cities.Select(c => new { c.Id, c.Name }) }));

        g.MapPost("/operations", async (NewOperationBody b, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            if (!Enum.TryParse<ElectionType>(b.Type, out var type)) return Problems.Error("צריך לבחור סוג בחירות");
            if (b.ElectionDay is null) return Problems.Error("צריך לבחור את יום הבחירות");
            if (b.CityIds is not { Count: > 0 }) return Problems.Error("צריך לבחור לפחות עיר אחת");
            if (await db.Operations.AnyAsync(o => o.IsActive, ct)) return Problems.Error("כבר יש מבצע פעיל. צריך לסגור אותו לפני שפותחים חדש.", 409);
            var cities = await db.Cities.Where(c => b.CityIds.Contains(c.Id)).ToListAsync(ct);
            var op = new ElectionOperation { Type = type, ElectionDay = b.ElectionDay.Value, IsActive = true, Cities = cities };
            db.Operations.Add(op);
            var actor = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            Audit.Add(db, actor, "operation.open", $"{(type == ElectionType.Knesset ? "כנסת" : "רשויות")} {b.ElectionDay:dd.MM.yyyy}, {cities.Count} ערים");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { op.Id });
        });

        g.MapPost("/cities", async (NewCityBody b, ClaimsPrincipal me, AppDbContext db, IGovData gov, Invitations inv, CancellationToken ct) =>
        {
            var email = (b.AdminEmail ?? "").Trim().ToLowerInvariant();
            var adminName = Rules.NormalizeName(b.AdminName);
            if (adminName.Length == 0) return Problems.Error("צריך למלא את שם מנהל העיר");
            if (!System.Net.Mail.MailAddress.TryCreate(email, out _) || !email.Contains('.')) return Problems.Error("האימייל של מנהל העיר לא תקין");
            if (await db.Users.AnyAsync(u => u.Email == email, ct)) return Problems.Error("האימייל כבר רשום במערכת", 409);
            if (await db.Cities.AnyAsync(c => c.LocalityCode == b.LocalityCode, ct)) return Problems.Error("העיר כבר פתוחה במערכת", 409);

            Locality? locality;
            try { locality = (await gov.LocalitiesAsync(ct)).FirstOrDefault(l => l.Code == b.LocalityCode); }
            catch (HttpRequestException) { return Problems.Error("רשימת היישובים לא זמינה כרגע. אפשר לנסות שוב מאוחר יותר.", 502); }
            if (locality is null) return Problems.Error("צריך לבחור עיר מרשימת היישובים");

            var city = new City { Name = locality.Name, LocalityCode = locality.Code };
            db.Cities.Add(city);

            // Copy settings from an existing city: communities and opening hours only (spec 3.2).
            if (b.CopyFromCityId is { } from)
            {
                var source = await db.Cities.FirstOrDefaultAsync(c => c.Id == from, ct);
                if (source is null) return Problems.Error("העיר להעתקה לא נמצאה");
                city.HoursFrom = source.HoursFrom;
                city.HoursTo = source.HoursTo;
                var names = await db.Communities.Where(x => x.CityId == from).Select(x => x.Name).ToListAsync(ct);
                db.Communities.AddRange(names.Select(n => new Community { CityId = city.Id, Name = n }));
            }

            var admin = new AppUser { Email = email, FullName = adminName, Role = UserRole.CityAdmin, CityId = city.Id };
            db.Users.Add(admin);
            var actor = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            Audit.Add(db, actor, "city.add", $"{city.Name}, מנהל עיר {adminName} ({email})", city.Id);
            await inv.SendAsync(admin, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { city.Id, city.Name });
        });
    }
}
