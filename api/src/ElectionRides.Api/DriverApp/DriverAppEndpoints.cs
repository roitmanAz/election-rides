using System.Security.Claims;
using ElectionRides.Api.Auth;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Dispatch;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Intake;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace ElectionRides.Api.DriverApp;

public record SessionIn(string Token);
public record DriverAppActionIn(string Action, double? Lat, double? Lon, string? Reason, string? Other);

/// <summary>
/// The driver app (spec 5.4, 5.5, 5.7, prototype docs/driver.html): sign in with the personal link,
/// see the offer or the ride with distances, and the few buttons the driver has.
/// The location is sent with each button press; there is no background tracking.
/// </summary>
public static class DriverAppEndpoints
{
    public const string Scheme = "driver";
    public const string DriverClaim = "driver";
    public static readonly string[] DeclineReasons = ["רחוק מדי", "אין מספיק מקום ברכב", "צריך הפסקה", "אחר"];
    /// <summary>Straight-line distance in version 1; minutes at a city driving speed.</summary>
    const double CityKmPerHour = 25;

    public static void MapDriverApp(this WebApplication app)
    {
        app.MapPost("/api/driver/session", async (SessionIn b, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var hash = Secrets.Hash(b.Token ?? "");
            var d = await db.Drivers.FirstOrDefaultAsync(x => x.AccessTokenHash == hash, ct);
            if (d is null || d.AccessTokenExpires < DateTimeOffset.UtcNow)
                return Problems.Error("הקישור לא בתוקף. אפשר לבקש מהמטה קישור חדש.", 410);
            var expires = d.AccessTokenExpires!.Value;
            // One use: the link signs in this phone, and cannot be used again.
            d.AccessTokenHash = null;
            await db.SaveChangesAsync(ct);
            var id = new ClaimsIdentity([new Claim(DriverClaim, d.Id.ToString()), new Claim(SessionPolicy.CityClaim, d.CityId.ToString())], Scheme);
            await http.SignInAsync(Scheme, new ClaimsPrincipal(id), new AuthenticationProperties { IsPersistent = true, ExpiresUtc = expires });
            return Results.NoContent();
        }).RequireRateLimiting("auth");

        var g = app.MapGroup("/api/driver").RequireAuthorization(Policies.Driver);

        g.MapGet("/me", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var d = await db.Drivers.FirstAsync(x => x.Id == DriverId(me), ct);
            var city = await db.Cities.FirstAsync(c => c.Id == d.CityId, ct);
            var trip = await db.Trips.Include(t => t.Stops)
                .FirstOrDefaultAsync(t => t.DriverId == d.Id && (t.Status == TripStatus.Active || t.Status == TripStatus.Offered), ct);
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(new
            {
                d.FullName, d.Seats, status = d.Status.ToString(), d.BreakAfterTrip,
                minutesToFree = d.FreeAt is { } f ? Math.Max(0, (int)Math.Ceiling((f - now).TotalMinutes)) : (int?)null,
                hqPhone = city.HqPhone, cityName = city.Name,
                trip = trip is null ? null : await TripView(db, trip, d, ct),
            });
        });

        g.MapPost("/action", async (DriverAppActionIn b, ClaimsPrincipal me, AppDbContext db, CityEvents events, CancellationToken ct) =>
        {
            var d = await db.Drivers.FirstAsync(x => x.Id == DriverId(me), ct);
            var now = DateTimeOffset.UtcNow;
            if (b.Lat is { } lat && b.Lon is { } lon && Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180)
            {
                d.Location = new Point(lon, lat) { SRID = 4326 };
                d.LocationAt = now;
            }

            var offer = await db.Trips.Include(t => t.Stops).FirstOrDefaultAsync(t => t.DriverId == d.Id && t.Status == TripStatus.Offered, ct);
            IResult? error = null;
            switch (b.Action)
            {
                case "Accept":
                    if (offer is null) return Problems.Error("ההצעה כבר לא בתוקף", 409);
                    offer.Status = TripStatus.Active;
                    offer.AcceptedAt = now;
                    d.Status = DriverStatus.OnTrip;
                    d.StatusSince = now;
                    d.FreeAt = null;
                    break;
                case "Decline":
                    if (offer is null) return Problems.Error("ההצעה כבר לא בתוקף", 409);
                    if (!DeclineReasons.Contains(b.Reason)) return Problems.Error("צריך לבחור סיבה");
                    if (b.Reason == "אחר" && string.IsNullOrWhiteSpace(b.Other)) return Problems.Error("צריך לכתוב בקצרה למה");
                    await DispatchEndpoints.CancelOffer(db, offer, b.Reason == "אחר" ? $"אחר: {b.Other!.Trim()}" : b.Reason, ct);
                    if (b.Reason == "צריך הפסקה")
                    {
                        d.Status = DriverStatus.Break;
                        d.StatusSince = now;
                    }
                    break;
                case "BackToWork":
                    if (d.Status != DriverStatus.Finished) return Problems.Error("אתה כבר ברשימה");
                    var op = await IntakeEndpoints.ActiveOperation(db, d.CityId, ct);
                    if (op is null) return Problems.Error("אין עכשיו מבצע בחירות פעיל");
                    d.OperationId = op.Id;
                    d.Status = DriverStatus.Free;
                    d.StatusSince = now;
                    break;
                case "Soon5" or "Soon10" or "Free" or "Break" or "CancelBreakAfter" or "BackFromBreak" or "Finish":
                    error = await DispatchEndpoints.DriverAction(db, d.CityId, d.Id, b.Action, ct);
                    break;
                default:
                    return Problems.Error("פעולה לא מוכרת");
            }
            if (error is not null) return error;
            await db.SaveChangesAsync(ct);
            await events.Changed(d.CityId);
            return Results.NoContent();
        });
    }

    static Guid DriverId(ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(DriverClaim)!);

    /// <summary>The offer or ride as the driver sees it: stops in order with distances, then the destination.</summary>
    static async Task<object> TripView(AppDbContext db, Trip trip, Driver d, CancellationToken ct)
    {
        var stops = trip.Stops.OrderBy(s => s.Order).ToList();
        var reqIds = stops.Where(s => s.RequestId != null).Select(s => s.RequestId!.Value).ToList();
        var reqs = await db.Requests.Where(r => reqIds.Contains(r.Id)).Include(r => r.Street)
            .Include(r => r.Passengers).ThenInclude(p => p.Person).ToDictionaryAsync(r => r.Id, ct);
        var clusterIds = stops.Where(s => s.ClusterId != null).Select(s => s.ClusterId!.Value).ToList();
        var clusters = await db.Clusters.Where(c => clusterIds.Contains(c.Id)).Include(c => c.Street).ToDictionaryAsync(c => c.Id, ct);

        GeoPoint? prev = d.Location is { } dl ? new GeoPoint(dl.Y, dl.X) : null;
        double totalKm = 0;
        var known = true;
        object Leg(Point? at)
        {
            var here = at is null ? null : new GeoPoint(at.Y, at.X);
            double? km = prev is not null && here is not null ? Math.Round(ProposalEngine.Km(prev, here), 1) : null;
            if (km is null) known = false; else totalKm += km.Value;
            prev = here ?? prev;
            return new { km, minutes = km is null ? (int?)null : Math.Max(1, (int)Math.Round(km.Value / CityKmPerHour * 60)) };
        }

        var view = stops.Select(s =>
        {
            if (s.Kind == StopKind.Pickup && s.RequestId is { } rid && reqs.TryGetValue(rid, out var r))
                return (object)new
                {
                    kind = "Pickup", distance = Leg(r.Location),
                    address = $"{r.Street?.Name} {r.BuildingNumber}", r.Entrance, r.DriverNotes,
                    names = r.Passengers.Select(x => x.Person!.FullName).ToList(),
                    r.NeedsWheelchair, r.NeedsWalker, r.NeedsHelp, phone = r.CallerPhone,
                    comesBack = r.ReturnMode == ReturnMode.DriverWaits,
                    location = DispatchEndpoints.Point(r.Location) is { } rp ? new { rp.lat, rp.lon } : null,
                };
            var c = clusters.GetValueOrDefault(s.ClusterId ?? Guid.Empty);
            return new
            {
                kind = "DropOff", distance = Leg(c?.Location),
                plate = c is null ? "" : c.Number?.ToString() ?? c.ShortName, name = c?.Name,
                address = c is null ? null : $"{c.Street?.Name} {c.BuildingNumber}", dropoffNote = c?.DropoffNote,
                location = DispatchEndpoints.Point(c?.Location) is { } cp ? new { cp.lat, cp.lon } : null,
            };
        }).ToList();

        return new
        {
            trip.Id, status = trip.Status.ToString(), offeredAt = trip.CreatedAt, stops = view,
            passengers = reqs.Values.Sum(r => r.Passengers.Count),
            totalKm = known ? Math.Round(totalKm, 1) : (double?)null,
            totalMinutes = known ? Math.Max(1, (int)Math.Round(totalKm / CityKmPerHour * 60)) : (int?)null,
        };
    }
}
