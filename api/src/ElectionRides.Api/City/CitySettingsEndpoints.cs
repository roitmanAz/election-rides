using System.Security.Claims;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.CityScreens;

public record CitySettingsBody(string? HqPhone, TimeOnly? HoursFrom, TimeOnly? HoursTo, DateOnly? ElectionDay);

/// <summary>
/// City settings (spec 3.1): HQ phone (required, shown to drivers), opening hours, and election day.
/// The election type and default day come from the active operation; a city can set its own day (runoff).
/// </summary>
public static class CitySettingsEndpoints
{
    public static void MapCitySettings(this WebApplication app)
    {
        var g = app.MapGroup("/api/city/settings").RequireAuthorization(Policies.CityStaff);

        g.MapGet("", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var id = me.CityId();
            var city = await db.Cities.FirstAsync(c => c.Id == id, ct);
            var op = await db.Operations.Where(o => o.IsActive && o.Cities.Any(c => c.Id == id)).FirstOrDefaultAsync(ct);
            return Results.Ok(new
            {
                city.Name, city.HqPhone, city.HoursFrom, city.HoursTo,
                electionDay = city.ElectionDayOverride ?? op?.ElectionDay,
                electionType = op?.Type.ToString(),
                inActiveOperation = op is not null,
            });
        });

        g.MapPut("", async (CitySettingsBody b, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var id = me.CityId();
            var city = await db.Cities.FirstAsync(c => c.Id == id, ct);
            var phone = Rules.DigitsOnly(b.HqPhone);
            if (!Rules.IsValidPhone(phone))
                return Problems.Error("מספר הטלפון לא תקין. נייד: 10 ספרות שמתחילות ב-05 או 07. קווי: 9 ספרות שמתחילות ב-02, 03, 04, 08 או 09");
            if (b.HoursFrom is not null && b.HoursTo is not null && b.HoursTo <= b.HoursFrom)
                return Problems.Error("שעת הסיום צריכה להיות אחרי שעת הפתיחה");

            var op = await db.Operations.Where(o => o.IsActive && o.Cities.Any(c => c.Id == id)).FirstOrDefaultAsync(ct);
            city.HqPhone = phone;
            city.HoursFrom = b.HoursFrom;
            city.HoursTo = b.HoursTo;
            city.ElectionDayOverride = b.ElectionDay == op?.ElectionDay ? null : b.ElectionDay;
            var user = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            Audit.Add(db, user, "city.settings", $"טלפון המטה {phone}");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }).RequireAuthorization(Policies.CityAdmin);
    }
}
