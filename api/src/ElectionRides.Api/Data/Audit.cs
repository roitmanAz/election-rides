using ElectionRides.Api.Domain;

namespace ElectionRides.Api.Data;

/// <summary>Adds rows to the audit log. Saved with the same SaveChanges as the action itself.</summary>
public static class Audit
{
    public static void Add(AppDbContext db, AppUser? actor, string action, string? details = null, Guid? cityId = null) =>
        db.Audit.Add(new AuditEntry
        {
            UserId = actor?.Id,
            UserEmail = actor?.Email,
            CityId = cityId ?? actor?.CityId,
            Action = action,
            Details = details,
        });
}
