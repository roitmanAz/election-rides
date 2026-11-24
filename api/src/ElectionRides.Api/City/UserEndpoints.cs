using System.Security.Claims;
using ElectionRides.Api.Auth;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.CityScreens;

public record NewUserBody(string Email, string FullName, List<string>? Roles, string? Role);
public record RolesBody(List<string> Roles);

/// <summary>
/// City users (spec 3.1): list, invite by email, change roles, remove, release a lock early, resend an invitation.
/// A user may hold several roles in the city (operator, dispatcher, city admin).
/// </summary>
public static class UserEndpoints
{
    static readonly UserRole[] CityRoles = [UserRole.Operator, UserRole.Dispatcher, UserRole.CityAdmin];

    static List<UserRole>? ParseRoles(IEnumerable<string>? names)
    {
        var list = new List<UserRole>();
        foreach (var n in names ?? [])
        {
            if (!Enum.TryParse<UserRole>(n, out var r) || !CityRoles.Contains(r)) return null;
            list.Add(r);
        }
        return list.Count == 0 ? null : list;
    }

    static readonly Dictionary<UserRole, string> Names = new()
    {
        [UserRole.Operator] = "מוקדן", [UserRole.Dispatcher] = "משבץ", [UserRole.CityAdmin] = "מנהל עיר",
    };
    static string RoleText(AppUser u) => string.Join(", ", u.AllRoles().Select(r => Names[r]));

    public static void MapCityUsers(this WebApplication app)
    {
        var g = app.MapGroup("/api/city/users").RequireAuthorization(Policies.CityAdmin);

        g.MapGet("", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var users = await db.Users.Where(u => u.CityId == me.CityId() && u.IsActive)
                .OrderBy(u => u.Role).ThenBy(u => u.FullName).ToListAsync(ct);
            return users.Select(u => new
            {
                u.Id, u.FullName, u.Email, role = u.Role.ToString(), roles = u.AllRoles().Select(r => r.ToString()), u.LastLoginAt,
                invited = u.PasswordHash == null,
                lockedUntil = u.LockedUntil > now ? u.LockedUntil : null,
            });
        });

        g.MapPost("", async (NewUserBody b, ClaimsPrincipal me, AppDbContext db, Invitations inv, CancellationToken ct) =>
        {
            var email = Rules.NormalizeEmail(b.Email);
            var name = Rules.NormalizeName(b.FullName);
            if (!System.Net.Mail.MailAddress.TryCreate(email, out _) || !email.Contains('.')) return Problems.Error("האימייל לא תקין");
            if (name.Length == 0) return Problems.Error("צריך למלא שם");
            var roles = ParseRoles(b.Roles ?? (b.Role is null ? null : [b.Role]));
            if (roles is null) return Problems.Error("צריך לבחור תפקיד");
            if (await db.Users.AnyAsync(u => u.Email == email, ct)) return Problems.Error("האימייל כבר רשום במערכת", 409);

            var user = new AppUser { Email = email, FullName = name, CityId = me.CityId() };
            user.SetRoles(roles);
            db.Users.Add(user);
            var actor = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            Audit.Add(db, actor, "user.add", $"{name} ({email}), {RoleText(user)}");
            await inv.SendAsync(user, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { user.Id });
        });

        g.MapPut("/{id:guid}/roles", async (Guid id, RolesBody b, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var roles = ParseRoles(b.Roles);
            if (roles is null) return Problems.Error("צריך לבחור לפחות תפקיד אחד");
            if (id == me.UserId() && !roles.Contains(UserRole.CityAdmin)) return Problems.Error("אי אפשר להוריד ממך את ניהול העיר");
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.CityId == me.CityId() && u.IsActive, ct);
            if (user is null) return Results.NotFound();
            var before = RoleText(user);
            user.SetRoles(roles);
            var actor = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            Audit.Add(db, actor, "user.roles", $"{user.FullName} ({user.Email}): היה {before}, עכשיו {RoleText(user)}");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            if (id == me.UserId()) return Problems.Error("אי אפשר להסיר את עצמך");
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.CityId == me.CityId() && u.IsActive, ct);
            if (user is null) return Results.NotFound();
            // Removed users keep their history (who took which call), but cannot sign in.
            user.IsActive = false;
            var actor = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            Audit.Add(db, actor, "user.remove", $"{user.FullName} ({user.Email})");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/unlock", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.CityId == me.CityId() && u.IsActive, ct);
            if (user is null) return Results.NotFound();
            user.LockedUntil = null;
            user.FailedLogins = 0;
            var actor = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            Audit.Add(db, actor, "user.unlock", $"{user.FullName} ({user.Email})");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapPost("/{id:guid}/invite", async (Guid id, ClaimsPrincipal me, AppDbContext db, Invitations inv, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id && u.CityId == me.CityId() && u.IsActive, ct);
            if (user is null) return Results.NotFound();
            await inv.SendAsync(user, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }
}
