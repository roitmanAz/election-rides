using System.Security.Claims;
using ElectionRides.Api.Auth;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.CityScreens;

public record NewUserBody(string Email, string FullName, string Role);

/// <summary>City users (spec 3.1): list, invite by email, remove, release a lock early, resend an invitation.</summary>
public static class UserEndpoints
{
    static readonly UserRole[] CityRoles = [UserRole.Operator, UserRole.Dispatcher, UserRole.CityAdmin];

    public static void MapCityUsers(this WebApplication app)
    {
        var g = app.MapGroup("/api/city/users").RequireAuthorization(Policies.CityAdmin);

        g.MapGet("", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            return await db.Users.Where(u => u.CityId == me.CityId() && u.IsActive)
                .OrderBy(u => u.Role).ThenBy(u => u.FullName)
                .Select(u => new
                {
                    u.Id, u.FullName, u.Email, role = u.Role.ToString(), u.LastLoginAt,
                    invited = u.PasswordHash == null,
                    lockedUntil = u.LockedUntil > now ? u.LockedUntil : null,
                }).ToListAsync(ct);
        });

        g.MapPost("", async (NewUserBody b, ClaimsPrincipal me, AppDbContext db, Invitations inv, CancellationToken ct) =>
        {
            var email = (b.Email ?? "").Trim().ToLowerInvariant();
            var name = Rules.NormalizeName(b.FullName);
            if (!System.Net.Mail.MailAddress.TryCreate(email, out _) || !email.Contains('.')) return Problems.Error("האימייל לא תקין");
            if (name.Length == 0) return Problems.Error("צריך למלא שם");
            if (!Enum.TryParse<UserRole>(b.Role, out var role) || !CityRoles.Contains(role)) return Problems.Error("צריך לבחור תפקיד");
            if (await db.Users.AnyAsync(u => u.Email == email, ct)) return Problems.Error("האימייל כבר רשום במערכת", 409);

            var user = new AppUser { Email = email, FullName = name, Role = role, CityId = me.CityId() };
            db.Users.Add(user);
            var actor = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            Audit.Add(db, actor, "user.add", $"{name} ({email}), {role}");
            await inv.SendAsync(user, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { user.Id });
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
