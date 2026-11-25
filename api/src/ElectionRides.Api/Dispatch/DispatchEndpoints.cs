using System.Security.Claims;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Intake;
using Microsoft.EntityFrameworkCore;
using ElectionRides.Api.Maps;
using NetTopologySuite.Geometries;

namespace ElectionRides.Api.Dispatch;

public record DriverIn(string Phone, string FullName, int Seats, bool WheelchairCar, bool HasSmartphone,
    Guid? AreaStreetId = null, string? AreaBuilding = null);
public record SuspendIn(bool Suspend);
public record TripIn(Guid DriverId, List<Guid> RequestIds);
public record JoinIn(Guid RequestId);
public record DriverActionIn(string Action, string? Reason = null);
public record LocationIn(double Lat, double Lon);

/// <summary>Dispatcher screen (spec 5.3 to 5.7): queue, drivers, proposals, trips and driver statuses.</summary>
public static class DispatchEndpoints
{
    static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
    static readonly DriverStatus[] Available = [DriverStatus.Free, DriverStatus.Soon];

    public static void MapDispatch(this WebApplication app)
    {
        var g = app.MapGroup("/api/dispatch").RequireAuthorization(Policies.Dispatch);

        g.MapGet("/state", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var city = me.CityId();
            var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
            if (op is null) return Results.Ok(new { active = false });
            var now = DateTimeOffset.UtcNow;
            var queue = await QueueItems(db, city, op.Id, now, ct);
            var rides = await SentRides(db, city, op.Id, ct);
            object? JoinFor(QueueItem r) => rides
                .Where(x => ProposalEngine.WhyNotJoin(x, r) == "")
                .Select(x => new { tripId = x.TripId, driverId = x.Driver.Id, driverName = x.Driver.Name })
                .FirstOrDefault();
            var drivers = await db.Drivers.Where(d => d.CityId == city && d.OperationId == op.Id && d.Status != DriverStatus.Finished)
                .OrderBy(d => d.FullName).ToListAsync(ct);
            var trips = await db.Trips.Where(t => t.CityId == city && (t.Status == TripStatus.Active || t.Status == TripStatus.Offered)).Include(t => t.Stops).ToListAsync(ct);
            // A driver who could not take a ride in the last 15 minutes: the dispatcher sees the reason.
            var since = now.AddMinutes(-15);
            var declines = await db.Trips.Where(t => t.CityId == city && t.Status == TripStatus.Cancelled && t.DeclineReason != null && t.CreatedAt >= since)
                .OrderByDescending(t => t.CreatedAt).Select(t => new { t.DriverId, t.DeclineReason }).ToListAsync(ct);
            var tripReqIds = trips.SelectMany(t => t.Stops).Where(s => s.RequestId != null).Select(s => s.RequestId!.Value).ToList();
            var tripReqs = await db.Requests.Where(r => tripReqIds.Contains(r.Id)).Include(r => r.Street)
                .Include(r => r.Passengers).ThenInclude(p => p.Person).ToDictionaryAsync(r => r.Id, ct);

            return Results.Ok(new
            {
                active = true,
                queue = queue.Where(q => !q.item.NotYet).Select(q => q.dto),
                // Waiting requests that can join a ride already sent, right on the way (spec 7).
                joins = queue.Where(q => !q.item.NotYet).Select(q => (id: q.item.Id, join: JoinFor(q.item))).Where(x => x.join is not null)
                    .Select(x => new { requestId = x.id, x.join }),
                // Requests for a later pickup window: shown only as a count until their window starts.
                later = queue.Count(q => q.item.NotYet),
                drivers = drivers.Select(d =>
                {
                    var trip = trips.FirstOrDefault(t => t.DriverId == d.Id);
                    return new
                    {
                        d.Id, d.FullName, d.Phone, d.HasSmartphone, d.Seats, d.WheelchairCar, status = d.Status.ToString(),
                        d.StatusSince, d.FreeAt, d.BreakAfterTrip,
                        minutesToFree = d.FreeAt is { } f ? Math.Max(0, (int)Math.Ceiling((f - now).TotalMinutes)) : (int?)null,
                        location = Point(d.Location),
                        d.AreaStreetId, d.AreaBuilding,
                        hasLink = d.AccessTokenExpires > now,
                        lastDecline = declines.FirstOrDefault(x => x.DriverId == d.Id)?.DeclineReason,
                        trip = trip is null ? null : new
                        {
                            trip.Id, status = trip.Status.ToString(), offeredAt = trip.CreatedAt,
                            stops = trip.Stops.OrderBy(s => s.Order).Select(s =>
                            {
                                var r = s.RequestId is { } rid ? tripReqs.GetValueOrDefault(rid) : null;
                                return new
                                {
                                    kind = s.Kind.ToString(), s.ClusterId, s.DoneAt, requestId = s.RequestId,
                                    names = r?.Passengers.Select(p => p.Person!.FullName).ToList(),
                                    address = r is null ? null : $"{r.Street?.Name} {r.BuildingNumber}{(r.Entrance is null ? "" : " כניסה " + r.Entrance)}",
                                    phone = r?.CallerPhone, status = r?.Status.ToString(),
                                    notes = r is null ? null : string.Join(", ", new[]
                                    {
                                        r.NeedsWheelchair ? "כיסא גלגלים" : null, r.NeedsWalker ? "הליכון" : null, r.NeedsHelp ? "צריך עזרה" : null,
                                        r.WithEscort ? "עם מלווה" : null,
                                        r.DriverNotes,
                                    }.Where(x => !string.IsNullOrWhiteSpace(x))),
                                    returnsWithDriver = r?.ReturnMode == ReturnMode.DriverWaits,
                                };
                            }),
                        },
                    };
                }),
            });
        });

        // Proposals. regroup=true is "regroup by proximity": driving times on the roads, and a group of requests to the
        // same cluster may go before a single request that waited up to 10 minutes longer (spec 7).
        g.MapGet("/proposals", async (bool? regroup, ClaimsPrincipal me, AppDbContext db, IConfiguration config,
            IHttpClientFactory http, ILoggerFactory logs, CancellationToken ct) =>
        {
            var city = me.CityId();
            var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
            if (op is null) return Results.Ok(Array.Empty<Proposal>());
            var now = DateTimeOffset.UtcNow;
            var queue = (await QueueItems(db, city, op.Id, now, ct)).Select(q => q.item).ToList();
            var drivers = await AvailableDrivers(db, city, op.Id, now, ct);
            if (regroup != true) return Results.Ok(ProposalEngine.Build(queue, drivers));
            var points = queue.SelectMany(q => new[] { q.Pickup, q.Destination }).Concat(drivers.Select(d => d.Location))
                .OfType<GeoPoint>();
            var times = await RoadTimes.For(points, config, http, logs.CreateLogger("RoadTimes"), ct);
            return Results.Ok(ProposalEngine.Build(queue, drivers, times, regroup: true));
        });

        // Confirm a proposal (as is, or after the dispatcher removed or added requests).
        g.MapPost("/trips", async (TripIn b, ClaimsPrincipal me, AppDbContext db, CityEvents events, CancellationToken ct) =>
        {
            var city = me.CityId();
            var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
            if (op is null) return Problems.Error("אין מבצע פעיל", 409);
            var now = DateTimeOffset.UtcNow;
            var driver = (await AvailableDrivers(db, city, op.Id, now, ct)).FirstOrDefault(d => d.Id == b.DriverId);
            if (driver is null) return Problems.Error("הנהג כבר לא פנוי", 409);
            if (b.RequestIds is not { Count: > 0 }) return Problems.Error("צריך לפחות פנייה אחת בהצעה");

            var queue = (await QueueItems(db, city, op.Id, now, ct)).Select(q => q.item).ToDictionary(q => q.Id);
            var car = new List<QueueItem>();
            foreach (var id in b.RequestIds.Distinct())
            {
                if (!queue.TryGetValue(id, out var r)) return Problems.Error("אחת הפניות כבר לא בתור. ההצעות עודכנו.", 409);
                var why = ProposalEngine.WhyNot(driver, car, r);
                if (why != "") return Problems.Error($"לא ניתן לצרף ל{driver.Name}: {why}");
                car.Add(r);
            }

            var clusterPoints = await db.Clusters.Where(c => c.CityId == city)
                .ToDictionaryAsync(c => c.Id, c => Point(c.Location) is { } p ? new GeoPoint(p.lat, p.lon) : null, ct);
            var (pickups, drops) = ProposalEngine.OrderStops(driver.Location, car, clusterPoints);
            var d = await db.Drivers.FirstAsync(x => x.Id == driver.Id, ct);
            // A driver with the app gets an offer to accept; a driver without it confirmed on the phone.
            var trip = new Trip
            {
                CityId = city, OperationId = op.Id, DriverId = driver.Id, AssignedById = me.UserId(),
                Status = d.HasSmartphone ? TripStatus.Offered : TripStatus.Active,
                AcceptedAt = d.HasSmartphone ? null : now,
            };
            var order = 0;
            foreach (var p in pickups) trip.Stops.Add(new TripStop { TripId = trip.Id, Order = ++order, Kind = StopKind.Pickup, RequestId = p });
            foreach (var c in drops) trip.Stops.Add(new TripStop { TripId = trip.Id, Order = ++order, Kind = StopKind.DropOff, ClusterId = c });
            db.Trips.Add(trip);

            await db.Requests.Where(r => pickups.Contains(r.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Assigned).SetProperty(r => r.UpdatedAt, now).SetProperty(r => r.AssignedAt, now), ct);
            if (!d.HasSmartphone)
            {
                d.Status = DriverStatus.OnTrip;
                d.StatusSince = now;
                d.FreeAt = null;
            }
            await db.SaveChangesAsync(ct);
            await events.Changed(city);
            return Results.Ok(new { trip.Id });
        });

        // Driver statuses (spec 5.5, 5.7). The dispatcher updates for drivers without the app, or when needed.
        g.MapPost("/drivers/{id:guid}/action", async (Guid id, DriverActionIn b, ClaimsPrincipal me, AppDbContext db, CityEvents events, CancellationToken ct) =>
        {
            var city = me.CityId();
            var error = await DriverAction(db, city, id, b.Action, ct, b.Reason);
            if (error is not null) return error;
            await events.Changed(city);
            return Results.NoContent();
        });

        // A driver from earlier (finished today, or an earlier election) is found by phone: "is this him?" (spec 5.7).
        g.MapGet("/drivers/lookup/{phone}", async (string phone, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var city = me.CityId();
            var p = Rules.DigitsOnly(phone);
            var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
            var d = await db.Drivers.FirstOrDefaultAsync(x => x.CityId == city && x.Phone == p, ct);
            if (d is null) return Results.Ok(new { found = false });
            var onList = d.OperationId == op?.Id && d.Status != DriverStatus.Finished;
            return Results.Ok(new { found = true, onList, d.Id, d.FullName, d.Seats, d.WheelchairCar, d.HasSmartphone });
        });

        g.MapPost("/drivers", async (DriverIn b, ClaimsPrincipal me, AppDbContext db, IGeocoder geo, CityEvents events, CancellationToken ct) =>
        {
            var city = me.CityId();
            var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
            if (op is null) return Problems.Error("אין מבצע פעיל", 409);
            var error = ValidateDriver(b);
            if (error is not null) return error;
            var phone = Rules.DigitsOnly(b.Phone);
            if (await db.Drivers.AnyAsync(x => x.CityId == city && x.Phone == phone, ct))
                return Problems.Error("הטלפון כבר שייך לנהג שמור. אפשר להחזיר אותו לרשימה.", 409);
            var d = new Driver
            {
                CityId = city, Phone = phone, FullName = Rules.NormalizeName(b.FullName), Seats = b.Seats,
                WheelchairCar = b.WheelchairCar, HasSmartphone = b.HasSmartphone, OperationId = op.Id, Status = DriverStatus.Free,
            };
            var areaError = await SetArea(d, b, city, db, geo, ct);
            if (areaError is not null) return areaError;
            db.Drivers.Add(d);
            await db.SaveChangesAsync(ct);
            await events.Changed(city);
            return Results.Ok(new { d.Id });
        });

        // "Yes, it's him": back on today's list as free.
        g.MapPost("/drivers/{id:guid}/rejoin", async (Guid id, ClaimsPrincipal me, AppDbContext db, CityEvents events, CancellationToken ct) =>
        {
            var city = me.CityId();
            var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
            if (op is null) return Problems.Error("אין מבצע פעיל", 409);
            var d = await db.Drivers.FirstOrDefaultAsync(x => x.Id == id && x.CityId == city, ct);
            if (d is null) return Results.NotFound();
            if (d.OperationId == op.Id && d.Status != DriverStatus.Finished) return Problems.Error("הנהג כבר ברשימה של היום", 409);
            d.OperationId = op.Id;
            d.Status = DriverStatus.Free;
            d.StatusSince = DateTimeOffset.UtcNow;
            d.FreeAt = null;
            d.BreakAfterTrip = false;
            await db.SaveChangesAsync(ct);
            await events.Changed(city);
            return Results.NoContent();
        });

        g.MapPut("/drivers/{id:guid}", async (Guid id, DriverIn b, ClaimsPrincipal me, AppDbContext db, IGeocoder geo, CityEvents events, CancellationToken ct) =>
        {
            var city = me.CityId();
            var d = await db.Drivers.FirstOrDefaultAsync(x => x.Id == id && x.CityId == city, ct);
            if (d is null) return Results.NotFound();
            var error = ValidateDriver(b);
            if (error is not null) return error;
            var phone = Rules.DigitsOnly(b.Phone);
            if (await db.Drivers.AnyAsync(x => x.CityId == city && x.Phone == phone && x.Id != id, ct))
                return Problems.Error("הטלפון הזה שייך לנהג אחר", 409);
            d.Phone = phone;
            d.FullName = Rules.NormalizeName(b.FullName);
            d.Seats = b.Seats;
            d.WheelchairCar = b.WheelchairCar;
            d.HasSmartphone = b.HasSmartphone;
            var areaError = await SetArea(d, b, city, db, geo, ct);
            if (areaError is not null) return areaError;
            await db.SaveChangesAsync(ct);
            await events.Changed(city);
            return Results.NoContent();
        });

        // "Suspend": a request from a far area is kept aside and offered only to a driver who starts near it (spec 7).
        g.MapPost("/requests/{id:guid}/suspend", async (Guid id, SuspendIn b, ClaimsPrincipal me, AppDbContext db, CityEvents events, CancellationToken ct) =>
        {
            var city = me.CityId();
            var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id && x.CityId == city && x.Status == RequestStatus.Queued, ct);
            if (r is null) return Problems.Error("הפנייה כבר לא בתור", 409);
            r.SuspendedAt = b.Suspend ? DateTimeOffset.UtcNow : null;
            r.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await events.Changed(city);
            return Results.NoContent();
        });

        // "Join a ride already sent" (spec 7): a waiting request right on the way of a ride with a free seat.
        g.MapPost("/trips/{id:guid}/join", async (Guid id, JoinIn b, ClaimsPrincipal me, AppDbContext db, CityEvents events, CancellationToken ct) =>
        {
            var city = me.CityId();
            var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
            if (op is null) return Problems.Error("אין מבצע פעיל", 409);
            var now = DateTimeOffset.UtcNow;
            var ride = (await SentRides(db, city, op.Id, ct)).FirstOrDefault(x => x.TripId == id);
            if (ride is null) return Problems.Error("הנסיעה כבר הסתיימה", 409);
            var r = (await QueueItems(db, city, op.Id, now, ct)).Select(q => q.item).FirstOrDefault(q => q.Id == b.RequestId);
            if (r is null) return Problems.Error("הפנייה כבר לא בתור", 409);
            var why = ProposalEngine.WhyNotJoin(ride, r);
            if (why != "") return Problems.Error($"לא ניתן לצרף לנסיעה של {ride.Driver.Name}: {why}");

            // The new pickup goes among the pickups still ahead, nearest next from where the driver is now.
            var trip = await db.Trips.Include(t => t.Stops).FirstAsync(t => t.Id == id, ct);
            var done = trip.Stops.Where(s => s.DoneAt != null).OrderBy(s => s.Order).ToList();
            var ahead = trip.Stops.Where(s => s.DoneAt == null && s.Kind == StopKind.Pickup).ToList();
            var drops = trip.Stops.Where(s => s.Kind == StopKind.DropOff).OrderBy(s => s.Order).ToList();
            var points = ride.Ahead.Append(r).ToDictionary(x => x.Id, x => x.Pickup);
            var newStop = new TripStop { TripId = trip.Id, Kind = StopKind.Pickup, RequestId = r.Id };
            var left = ahead.Append(newStop).ToList();
            var cur = ride.Position ?? points.Values.FirstOrDefault(p => p is not null);
            var ordered = new List<TripStop>();
            while (left.Count > 0)
            {
                var next = cur is null ? left[0] : left.MinBy(s => points.GetValueOrDefault(s.RequestId!.Value) is { } p ? ProposalEngine.Estimate(cur, p) : double.MaxValue)!;
                ordered.Add(next);
                left.Remove(next);
                cur = points.GetValueOrDefault(next.RequestId!.Value) ?? cur;
            }
            var order = 0;
            foreach (var s in done.Concat(ordered).Concat(drops.Where(s => s.DoneAt == null))) s.Order = ++order;
            // Added explicitly: its id is already set, so through the trip's list it would look like an existing row.
            db.TripStops.Add(newStop);
            await db.Requests.Where(x => x.Id == r.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, RequestStatus.Assigned).SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.AssignedAt, now), ct);
            await db.SaveChangesAsync(ct);
            await events.Changed(city);
            return Results.NoContent();
        });

        // An offer the driver did not take: back to the queue (spec 5.4).
        g.MapPost("/trips/{id:guid}/withdraw", async (Guid id, ClaimsPrincipal me, AppDbContext db, CityEvents events, CancellationToken ct) =>
        {
            var city = me.CityId();
            var trip = await db.Trips.Include(t => t.Stops).FirstOrDefaultAsync(t => t.Id == id && t.CityId == city && t.Status == TripStatus.Offered, ct);
            if (trip is null) return Problems.Error("ההצעה כבר לא ממתינה", 409);
            await CancelOffer(db, trip, null, ct);
            await db.SaveChangesAsync(ct);
            await events.Changed(city);
            return Results.NoContent();
        });

        // Personal sign-in link for the driver app; the dispatcher copies it and sends it (spec 12).
        g.MapPost("/drivers/{id:guid}/link", async (Guid id, ClaimsPrincipal me, AppDbContext db, IConfiguration config, CancellationToken ct) =>
        {
            var city = me.CityId();
            var d = await db.Drivers.FirstOrDefaultAsync(x => x.Id == id && x.CityId == city, ct);
            if (d is null) return Results.NotFound();
            if (!d.HasSmartphone) return Problems.Error("לנהג בלי סמארטפון אין אפליקציה");
            var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
            var cityRow = await db.Cities.FirstAsync(c => c.Id == city, ct);
            var token = Auth.Secrets.NewToken();
            d.AccessTokenHash = Auth.Secrets.Hash(token);
            d.AccessTokenExpires = EndOfElectionDay(cityRow.ElectionDayOverride ?? op?.ElectionDay);
            await db.SaveChangesAsync(ct);
            var baseUrl = (config["App:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
            return Results.Ok(new { url = $"{baseUrl}/driver/{token}", expires = d.AccessTokenExpires });
        });

        // "Verified" removes the tag only (spec 5.3).
        g.MapPost("/requests/{id:guid}/verify", async (Guid id, ClaimsPrincipal me, AppDbContext db, CityEvents events, CancellationToken ct) =>
        {
            var city = me.CityId();
            var n = await db.Passengers.Where(p => p.RequestId == id && db.Requests.Any(r => r.Id == id && r.CityId == city))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.PendingVerification, false), ct);
            if (n == 0) return Results.NotFound();
            await events.Changed(city);
            return Results.NoContent();
        });

        // The dispatcher marks the pickup on the map when the address was not found or is only the street's
        // centre (spec 10). The point is also kept for the address, so the next request from there gets it.
        g.MapPost("/requests/{id:guid}/location", async (Guid id, LocationIn b, ClaimsPrincipal me, AppDbContext db, CityEvents events, CancellationToken ct) =>
        {
            if (b.Lat is < 29 or > 34 || b.Lon is < 34 or > 36) return Problems.Error("הנקודה מחוץ לישראל");
            var city = me.CityId();
            var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id && x.CityId == city, ct);
            if (r is null) return Results.NotFound();
            var point = new NetTopologySuite.Geometries.Point(b.Lon, b.Lat) { SRID = 4326 };
            r.Location = point;
            r.ApproximateLocation = false;
            var saved = await db.AddressLocations.FirstOrDefaultAsync(a => a.StreetId == r.StreetId && a.BuildingNumber == r.BuildingNumber, ct);
            if (saved is null) db.AddressLocations.Add(new AddressLocation { StreetId = r.StreetId, BuildingNumber = r.BuildingNumber, Location = point, Approximate = false });
            else { saved.Location = point; saved.Approximate = false; saved.CheckedAt = DateTimeOffset.UtcNow; }
            await db.SaveChangesAsync(ct);
            await events.Changed(city);
            return Results.NoContent();
        });
    }

    /// <summary>The driver's optional area: a street of the city and a number, found on the map (or cleared).</summary>
    static async Task<IResult?> SetArea(Driver d, DriverIn b, Guid city, AppDbContext db, IGeocoder geo, CancellationToken ct)
    {
        var building = b.AreaBuilding?.Trim() ?? "";
        if (b.AreaStreetId is not { } streetId)
        {
            d.AreaStreetId = null;
            d.AreaBuilding = null;
            d.AreaLocation = null;
            return null;
        }
        if (d.AreaStreetId == streetId && (d.AreaBuilding ?? "") == building && d.AreaLocation is not null) return null;
        var street = await db.Streets.FirstOrDefaultAsync(s => s.Id == streetId && s.CityId == city, ct);
        if (street is null) return Problems.Error("צריך לבחור רחוב מהרשימה");
        var cityName = await db.Cities.Where(c => c.Id == city).Select(c => c.Name).FirstAsync(ct);
        d.AreaStreetId = streetId;
        d.AreaBuilding = building == "" ? null : building;
        d.AreaLocation = (await geo.FindAsync(cityName, street.Name, building, ct)).Location;
        return null;
    }

    static IResult? ValidateDriver(DriverIn b)
    {
        if (!Rules.IsValidPhone(b.Phone)) return Problems.Error("מספר הטלפון לא תקין");
        if (Rules.NormalizeName(b.FullName).Length == 0) return Problems.Error("צריך למלא שם");
        if (b.Seats is < 1 or > 8) return Problems.Error("מספר המקומות צריך להיות בין 1 ל-8");
        return null;
    }

    internal static async Task<IResult?> DriverAction(AppDbContext db, Guid city, Guid id, string action, CancellationToken ct, string? reason = null)
    {
        var d = await db.Drivers.FirstOrDefaultAsync(x => x.Id == id && x.CityId == city, ct);
        if (d is null) return Results.NotFound();
        var trip = await db.Trips.Include(t => t.Stops).FirstOrDefaultAsync(t => t.DriverId == id && t.Status == TripStatus.Active, ct);
        if (action == "Finish" && await db.Trips.AnyAsync(t => t.DriverId == id && t.Status == TripStatus.Offered, ct))
            return Problems.Error("לנהג יש הצעה שממתינה לאישור");
        var now = DateTimeOffset.UtcNow;
        var requestIds = trip?.Stops.Where(s => s.RequestId != null).Select(s => s.RequestId!.Value).ToList() ?? [];

        async Task SetRequests(RequestStatus st) =>
            await db.Requests.Where(r => requestIds.Contains(r.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, st).SetProperty(r => r.UpdatedAt, now), ct);
        void SetStatus(DriverStatus st)
        {
            d.Status = st;
            d.StatusSince = now;
            d.FreeAt = null;
        }

        switch (action)
        {
            case "PickedUp":
                if (trip is null) return Problems.Error("לנהג אין נסיעה");
                trip.Stops.Where(s => s.Kind == StopKind.Pickup).ToList().ForEach(s => s.DoneAt ??= now);
                await SetRequests(RequestStatus.PickedUp);
                break;
            case "DroppedOff":
                if (trip is null) return Problems.Error("לנהג אין נסיעה");
                trip.Stops.ForEach(s => s.DoneAt ??= now);
                await SetRequests(RequestStatus.Arrived);
                break;
            case "Soon5" or "Soon10":
                if (trip is null) return Problems.Error("מתפנה בעוד: רק לנהג בנסיעה");
                d.Status = DriverStatus.Soon;
                d.StatusSince = now;
                d.FreeAt = now.AddMinutes(action == "Soon5" ? 5 : 10);
                break;
            case "Free":
                if (trip is not null)
                {
                    // The ride ends: riders who came back with the driver were taken home; the others reached the polling place.
                    trip.Status = TripStatus.Done;
                    trip.Stops.ForEach(s => s.DoneAt ??= now);
                    await SetRequests(RequestStatus.Done);
                }
                SetStatus(d.BreakAfterTrip ? DriverStatus.Break : DriverStatus.Free);
                d.BreakAfterTrip = false;
                break;
            case "Break":
                if (trip is not null) d.BreakAfterTrip = true;
                else SetStatus(DriverStatus.Break);
                break;
            case "CancelBreakAfter":
                d.BreakAfterTrip = false;
                break;
            case "BackFromBreak":
                if (d.Status != DriverStatus.Break) return Problems.Error("הנהג לא בהפסקה");
                SetStatus(DriverStatus.Free);
                break;
            // The driver cannot do a ride he took (spec 5.4): before any pickup only. Its requests go back to
            // the queue as they were (keeping their waiting time), and the driver is free (or on a break).
            case "CancelTrip":
                if (trip is null) return Problems.Error("לנהג אין נסיעה פעילה");
                if (trip.Stops.Any(s => s.Kind == StopKind.Pickup && s.DoneAt != null))
                    return Problems.Error("הנוסעים כבר ברכב. אפשר לבטל נסיעה רק לפני האיסוף.");
                if (string.IsNullOrWhiteSpace(reason)) return Problems.Error("צריך לבחור סיבה");
                await CancelOffer(db, trip, reason.Trim(), ct);
                SetStatus(reason == "צריך הפסקה" ? DriverStatus.Break : DriverStatus.Free);
                d.BreakAfterTrip = false;
                break;
            case "Finish":
                if (trip is not null) return Problems.Error("אי אפשר לסיים לעבוד באמצע נסיעה");
                SetStatus(DriverStatus.Finished);
                break;
            default:
                return Problems.Error("פעולה לא מוכרת");
        }
        await db.SaveChangesAsync(ct);
        return null;
    }

    internal static (double lat, double lon)? Point(Point? p) => p is null ? null : (p.Y, p.X);

    /// <summary>The offer ends: its requests go back to the queue, as they were.</summary>
    internal static async Task CancelOffer(AppDbContext db, Trip trip, string? reason, CancellationToken ct)
    {
        trip.Status = TripStatus.Cancelled;
        trip.DeclineReason = reason;
        var ids = trip.Stops.Where(s => s.RequestId != null).Select(s => s.RequestId!.Value).ToList();
        await db.Requests.Where(r => ids.Contains(r.Id) && r.Status == RequestStatus.Assigned)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RequestStatus.Queued).SetProperty(r => r.AssignedAt, (DateTimeOffset?)null), ct);
    }

    /// <summary>Links work until the end of election day (Israel time); without a day yet, for 24 hours.</summary>
    static DateTimeOffset EndOfElectionDay(DateOnly? day)
    {
        var now = DateTimeOffset.UtcNow;
        if (day is null) return now.AddHours(24);
        var end = day.Value.ToDateTime(new TimeOnly(23, 59));
        var at = new DateTimeOffset(end, Israel.GetUtcOffset(end)).ToUniversalTime();
        return at > now ? at : now.AddHours(24);
    }

    static async Task<List<DriverItem>> AvailableDrivers(AppDbContext db, Guid city, Guid op, DateTimeOffset now, CancellationToken ct)
    {
        var drivers = await db.Drivers.Where(d => d.CityId == city && d.OperationId == op && Available.Contains(d.Status) && !d.BreakAfterTrip
            && !db.Trips.Any(t => t.DriverId == d.Id && (t.Status == TripStatus.Active || t.Status == TripStatus.Offered))).ToListAsync(ct);
        var ends = await LastDropOffs(db, city, op, drivers.Select(d => d.Id).ToList(), ct);
        static GeoPoint? Geo(Point? pt) => Point(pt) is { } p ? new GeoPoint(p.lat, p.lon) : null;
        return drivers.Select(d => new DriverItem(
            d.Id, d.FullName, d.Seats, d.WheelchairCar, d.Status == DriverStatus.Soon,
            d.FreeAt is { } f ? Math.Max(0, (f - now).TotalMinutes) : null,
            ProposalEngine.StartPoint(ends.GetValueOrDefault(d.Id), Geo(d.Location), d.LocationAt, Geo(d.AreaLocation), now))).ToList();
    }

    /// <summary>
    /// Where each driver's last finished ride ended: the home of the last passenger brought back (when the driver
    /// waited and brought people home; the first picked up is the last brought back), otherwise the last cluster.
    /// </summary>
    static async Task<Dictionary<Guid, GeoPoint?>> LastDropOffs(AppDbContext db, Guid city, Guid op, List<Guid> driverIds, CancellationToken ct)
    {
        var trips = await db.Trips.Where(t => t.CityId == city && t.OperationId == op && t.Status == TripStatus.Done && driverIds.Contains(t.DriverId))
            .Select(t => new { t.DriverId, t.CreatedAt, stops = t.Stops.OrderBy(s => s.Order).Select(s => new { s.Kind, s.RequestId, s.ClusterId, s.DoneAt }).ToList() })
            .ToListAsync(ct);
        var last = trips.GroupBy(t => t.DriverId)
            .ToDictionary(g => g.Key, g => g.MaxBy(t => t.stops.Max(s => s.DoneAt) ?? t.CreatedAt)!);
        var reqIds = last.Values.SelectMany(t => t.stops).Where(s => s.RequestId != null).Select(s => s.RequestId!.Value).ToList();
        var reqs = await db.Requests.Where(r => reqIds.Contains(r.Id)).Select(r => new { r.Id, r.Location, r.ReturnMode }).ToDictionaryAsync(r => r.Id, ct);
        var clusterIds = last.Values.SelectMany(t => t.stops).Where(s => s.ClusterId != null).Select(s => s.ClusterId!.Value).ToList();
        var clusters = await db.Clusters.Where(c => clusterIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Location, ct);

        var result = new Dictionary<Guid, GeoPoint?>();
        foreach (var (driver, trip) in last)
        {
            var home = trip.stops.Where(s => s.Kind == StopKind.Pickup && s.RequestId is { } id && reqs.GetValueOrDefault(id)?.ReturnMode == ReturnMode.DriverWaits)
                .Select(s => reqs[s.RequestId!.Value].Location).FirstOrDefault(l => l is not null);
            var cluster = trip.stops.Where(s => s.Kind == StopKind.DropOff && s.ClusterId is not null)
                .Select(s => clusters.GetValueOrDefault(s.ClusterId!.Value)).LastOrDefault(l => l is not null);
            result[driver] = (home ?? cluster) is { } pt ? new GeoPoint(pt.Y, pt.X) : null;
        }
        return result;
    }

    /// <summary>Rides already sent (offered or active), as the "join on the way" rule sees them.</summary>
    static async Task<List<ProposalEngine.SentRide>> SentRides(AppDbContext db, Guid city, Guid op, CancellationToken ct)
    {
        var trips = await db.Trips.Where(t => t.CityId == city && t.OperationId == op && (t.Status == TripStatus.Active || t.Status == TripStatus.Offered))
            .Include(t => t.Stops).Include(t => t.Driver).ToListAsync(ct);
        if (trips.Count == 0) return [];
        var reqIds = trips.SelectMany(t => t.Stops).Where(s => s.RequestId != null).Select(s => s.RequestId!.Value).ToList();
        var reqs = await db.Requests.Where(r => reqIds.Contains(r.Id)).Include(r => r.Passengers).ThenInclude(p => p.Person)
            .ToDictionaryAsync(r => r.Id, ct);
        var clusters = await db.Clusters.Where(c => c.CityId == city).ToDictionaryAsync(c => c.Id, c => c.Location, ct);
        static GeoPoint? Geo(Point? pt) => Point(pt) is { } p ? new GeoPoint(p.lat, p.lon) : null;

        var result = new List<ProposalEngine.SentRide>();
        foreach (var t in trips)
        {
            var d = t.Driver!;
            QueueItem Item(RideRequest r)
            {
                var genders = r.Passengers.Select(p => p.Person!.Gender).Distinct().ToList();
                var kind = genders.Count > 1 ? RideKind.Family : genders[0] == Gender.Male ? RideKind.Men : RideKind.Women;
                return new QueueItem(r.Id, kind, r.Passengers.Count + (r.WithEscort ? 1 : 0), r.NeedsWheelchair, Geo(r.Location),
                    r.Passengers.Select(p => p.ClusterId).Distinct().ToList(), null, 0, null, false);
            }
            var pickups = t.Stops.Where(s => s.Kind == StopKind.Pickup && s.RequestId is { } id && reqs.ContainsKey(id)).OrderBy(s => s.Order).ToList();
            var inCar = pickups.Select(s => Item(reqs[s.RequestId!.Value])).ToList();
            var ahead = pickups.Where(s => s.DoneAt == null).Select(s => Item(reqs[s.RequestId!.Value])).ToList();
            var lastDone = pickups.LastOrDefault(s => s.DoneAt != null);
            var dropIds = t.Stops.Where(s => s.Kind == StopKind.DropOff && s.ClusterId != null).Select(s => s.ClusterId!.Value).ToList();
            var position = lastDone is not null ? Geo(reqs[lastDone.RequestId!.Value].Location) : Geo(d.Location);
            var driver = new DriverItem(d.Id, d.FullName, d.Seats, d.WheelchairCar, false, null, Geo(d.Location));
            result.Add(new ProposalEngine.SentRide(t.Id, driver, position, inCar, ahead, dropIds,
                dropIds.Count > 0 ? Geo(clusters.GetValueOrDefault(dropIds[0])) : null,
                t.Stops.Any(s => s.Kind == StopKind.DropOff && s.DoneAt != null)));
        }
        return result;
    }

    /// <summary>Waiting requests, as rules input and as screen rows.</summary>
    static async Task<List<(QueueItem item, object dto)>> QueueItems(AppDbContext db, Guid city, Guid op, DateTimeOffset now, CancellationToken ct)
    {
        var reqs = await db.Requests.Where(r => r.CityId == city && r.OperationId == op && r.Status == RequestStatus.Queued)
            .Include(r => r.Street).Include(r => r.Passengers).ThenInclude(p => p.Person).ToListAsync(ct);
        var clusters = await db.Clusters.Where(c => c.CityId == city).ToDictionaryAsync(c => c.Id, ct);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Israel).DateTime);
        DateTimeOffset At(TimeOnly t) => new DateTimeOffset(today.ToDateTime(t), Israel.GetUtcOffset(now)).ToUniversalTime();

        return reqs.Select(r =>
        {
            var genders = r.Passengers.Select(p => p.Person!.Gender).Distinct().ToList();
            var kind = genders.Count > 1 ? RideKind.Family : genders[0] == Gender.Male ? RideKind.Men : RideKind.Women;
            var pickup = Point(r.Location) is { } pp ? new GeoPoint(pp.lat, pp.lon) : null;
            var clusterIds = r.Passengers.Select(p => p.ClusterId).Distinct().ToList();
            var dest = clusters.GetValueOrDefault(clusterIds[0]) is { Location: { } loc } ? new GeoPoint(loc.Y, loc.X) : null;
            double? toDeadline = r.TimeMode == TimeMode.LeaveBy && r.WindowTo is { } to ? (At(to) - now).TotalMinutes : null;
            // A pickup window that has not started yet stays out of the queue and the proposals until it starts.
            var notYet = r.TimeMode == TimeMode.Between && r.WindowFrom is { } from && At(from) > now;
            var wait = Waiting.Minutes(now, r.CreatedAt, r.TimeMode, r.WindowFrom);
            var item = new QueueItem(r.Id, kind, r.Passengers.Count + (r.WithEscort ? 1 : 0), r.NeedsWheelchair, pickup, clusterIds, dest, wait, toDeadline, notYet,
                r.SuspendedAt is not null);
            object dto = new
            {
                r.Id, r.Number, kind = kind.ToString(), waitMinutes = (int)Math.Floor(wait),
                minutesToDeadline = toDeadline is null ? (int?)null : (int)Math.Floor(toDeadline.Value),
                deadlineSoon = ProposalEngine.DeadlineSoon(item), notYet, suspended = r.SuspendedAt is not null,
                passengers = r.Passengers.Select(p => new { p.Person!.FullName, gender = p.Person.Gender.ToString(), p.ClusterId, p.BallotBox }),
                pending = r.Passengers.Any(p => p.PendingVerification),
                r.CallerPhone, street = r.Street?.Name, r.BuildingNumber, r.Entrance, r.DriverNotes, r.ApproximateLocation,
                location = pickup, clusterIds, r.NeedsWheelchair, r.NeedsWalker, r.NeedsHelp, r.WithEscort,
                timeMode = r.TimeMode.ToString(), r.WindowFrom, r.WindowTo, r.UrgencyReason, returnMode = r.ReturnMode.ToString(),
                r.CreatedAt,
            };
            return (item, dto);
        }).ToList();
    }
}
