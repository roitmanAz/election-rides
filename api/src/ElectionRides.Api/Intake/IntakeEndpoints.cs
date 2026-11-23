using System.Security.Claims;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Dispatch;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Maps;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.Intake;

public record PassengerIn(Guid? PersonId, string FullName, string? IdNumber, bool NoId, string Gender, Guid? ClusterId);
public record RequestIn(
    string CallerPhone, List<PassengerIn> Passengers,
    Guid? StreetId, string BuildingNumber, string? Entrance, string? DriverNotes, Guid? CommunityId,
    bool NeedsWheelchair, bool NeedsWalker, bool NeedsHelp,
    string TimeMode, TimeOnly? WindowFrom, TimeOnly? WindowTo, string? UrgencyReason, string? UrgencyOther,
    string ReturnMode, bool KeepNonAccessible, bool IgnoreDuplicate);
public record CancelIn(string Reason, string? Other);

/// <summary>Operator intake (spec 5.1): who is on a phone, new request, edit, cancel, and status search.</summary>
public static class IntakeEndpoints
{
    static readonly RequestStatus[] Open = [RequestStatus.Queued, RequestStatus.Assigned, RequestStatus.PickedUp];
    static readonly RequestStatus[] Editable = [RequestStatus.Queued, RequestStatus.Assigned];
    static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");

    public static void MapIntake(this WebApplication app)
    {
        var g = app.MapGroup("/api/intake").RequireAuthorization(Policies.CityStaff);

        // Everyone known on this phone, from earlier requests (the voter base joins in stage 7).
        g.MapGet("/phone/{phone}", async (string phone, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var p = Rules.DigitsOnly(phone);
            if (!Rules.IsValidPhone(p)) return Problems.Error("מספר הטלפון לא תקין");
            var city = me.CityId();
            var known = await db.People.Where(x => x.CityId == city && x.Phones.Any(ph => ph.Phone == p))
                .OrderBy(x => x.FullName)
                .Select(x => new { personId = (Guid?)x.Id, x.FullName, x.IdNumber, gender = (string?)x.Gender.ToString(), clusterId = x.LastClusterId })
                .ToListAsync(ct);
            // With a voter base, everyone on the phone in the base is shown too, with the real ballot box (spec 5.1).
            var voters = await db.Voters.Where(v => v.CityId == city && v.Phones.Any(ph => ph.Phone == p))
                .Select(v => new { v.IdNumber, fullName = v.FirstName + " " + v.LastName, v.Age, v.BallotBox, v.ClusterId }).ToListAsync(ct);
            var people = known.Select(k =>
            {
                var v = voters.FirstOrDefault(x => x.IdNumber == k.IdNumber);
                return new { k.personId, k.FullName, k.IdNumber, k.gender, clusterId = k.clusterId ?? v?.ClusterId, ballotBox = v?.BallotBox, age = v?.Age, inBase = v is not null };
            }).Concat(voters.Where(v => known.All(k => k.IdNumber != v.IdNumber)).Select(v => new
            {
                personId = (Guid?)null, FullName = v.fullName, IdNumber = (string?)v.IdNumber, gender = (string?)null,
                clusterId = v.ClusterId, ballotBox = (string?)v.BallotBox, age = v.Age, inBase = true,
            })).ToList();
            var open = await OpenRequests(db, city).Where(r => r.CallerPhone == p).Select(r => new { r.Id, r.Number }).ToListAsync(ct);
            return Results.Ok(new { people, openRequests = open });
        });

        g.MapGet("/voter/{id}", async (string id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            if (!Rules.IsValidIdNumber(id)) return Problems.Error("מספר הזהות לא תקין");
            var n = Rules.NormalizeId(id);
            var v = await db.Voters.Where(x => x.CityId == me.CityId() && x.IdNumber == n)
                .Select(x => new { fullName = x.FirstName + " " + x.LastName, x.Age, x.BallotBox, x.ClusterId }).FirstOrDefaultAsync(ct);
            var baseLoaded = await db.VoterBases.AnyAsync(b => b.CityId == me.CityId(), ct);
            return Results.Ok(new { baseLoaded, found = v is not null, voter = v });
        });

        g.MapPost("/requests", async (RequestIn body, ClaimsPrincipal me, AppDbContext db, IGeocoder geo, CityEvents events, CancellationToken ct) =>
        {
            var city = me.CityId();
            var op = await ActiveOperation(db, city, ct);
            if (op is null) return Problems.Error("העיר לא משתתפת עכשיו במבצע בחירות פעיל. מנהל המערכת פותח מבצע.", 409);
            var req = new RideRequest { CityId = city, OperationId = op.Id, CallerPhone = "", BuildingNumber = "", TakenById = me.UserId() };
            var error = await Apply(req, body, db, geo, ct);
            if (error is not null) return error;

            // Running number per city and operation; retried if two operators save at the same moment.
            for (var attempt = 0; ; attempt++)
            {
                req.Number = (await db.Requests.Where(r => r.CityId == city && r.OperationId == op.Id).MaxAsync(r => (int?)r.Number, ct) ?? 0) + 1;
                try
                {
                    db.Requests.Add(req);
                    await db.SaveChangesAsync(ct);
                    await events.Changed(city);
                    return Results.Ok(new { req.Id, req.Number });
                }
                catch (DbUpdateException) when (attempt < 3)
                {
                    db.Entry(req).State = EntityState.Detached;
                }
            }
        });

        g.MapGet("/requests/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var r = await db.Requests.Include(x => x.Passengers).ThenInclude(x => x.Person)
                .FirstOrDefaultAsync(x => x.Id == id && x.CityId == me.CityId(), ct);
            return r is null ? Results.NotFound() : Results.Ok(Detail(r));
        });

        g.MapPut("/requests/{id:guid}", async (Guid id, RequestIn body, ClaimsPrincipal me, AppDbContext db, IGeocoder geo, CityEvents events, CancellationToken ct) =>
        {
            var r = await db.Requests.Include(x => x.Passengers).FirstOrDefaultAsync(x => x.Id == id && x.CityId == me.CityId(), ct);
            if (r is null) return Results.NotFound();
            if (!Editable.Contains(r.Status)) return Problems.Error("אפשר לערוך רק פנייה שעוד לא נאספה", 409);
            var error = await Apply(r, body, db, geo, ct);
            if (error is not null) return error;
            r.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await events.Changed(r.CityId);
            return Results.Ok(new { r.Id, r.Number });
        });

        g.MapPost("/requests/{id:guid}/cancel", async (Guid id, CancelIn body, ClaimsPrincipal me, AppDbContext db, CityEvents events, CancellationToken ct) =>
        {
            var r = await db.Requests.FirstOrDefaultAsync(x => x.Id == id && x.CityId == me.CityId(), ct);
            if (r is null) return Results.NotFound();
            if (!Editable.Contains(r.Status)) return Problems.Error("אפשר לבטל רק פנייה שעוד לא נאספה", 409);
            if (!Rules.CancelReasons.Contains(body.Reason)) return Problems.Error("צריך לבחור סיבה");
            if (body.Reason == "אחר" && string.IsNullOrWhiteSpace(body.Other)) return Problems.Error("צריך לכתוב בקצרה למה");
            r.Status = RequestStatus.Cancelled;
            r.CancelReason = body.Reason == "אחר" ? $"אחר: {body.Other!.Trim()}" : body.Reason;
            r.UpdatedAt = DateTimeOffset.UtcNow;
            var user = await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);
            Audit.Add(db, user, "request.cancel", $"פנייה {r.Number}: {r.CancelReason}");
            // A request already in a trip leaves the trip.
            await db.TripStops.Where(s => s.RequestId == r.Id).ExecuteDeleteAsync(ct);
            await db.SaveChangesAsync(ct);
            await events.Changed(r.CityId);
            return Results.NoContent();
        });

        // The operator's own latest requests (side panel), and how many are waiting in the city queue.
        g.MapGet("/mine", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var city = me.CityId();
            var user = me.UserId();
            var startOfDay = StartOfToday();
            var recent = await db.Requests.Where(r => r.CityId == city && r.TakenById == user)
                .OrderByDescending(r => r.CreatedAt).Take(8)
                .Select(r => new
                {
                    r.Id, r.Number, r.CreatedAt, status = r.Status.ToString(),
                    names = r.Passengers.Select(p => p.Person!.FullName).ToList(),
                    clusterId = r.Passengers.Select(p => p.ClusterId).FirstOrDefault(),
                }).ToListAsync(ct);
            var mineToday = await db.Requests.CountAsync(r => r.CityId == city && r.TakenById == user && r.CreatedAt >= startOfDay, ct);
            var queued = await db.Requests.CountAsync(r => r.CityId == city && r.Status == RequestStatus.Queued, ct);
            return Results.Ok(new { recent, mineToday, queued });
        });

        // Status search by phone, ID number or name, or part of them (spec 5.1).
        g.MapGet("/search", async (string? q, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var text = Rules.NormalizeName(q);
            if (text.Length < 2) return Results.Ok(Array.Empty<object>());
            var city = me.CityId();
            var digits = Rules.DigitsOnly(text);
            var query = db.Requests.Where(r => r.CityId == city);
            query = digits.Length >= 2 && digits.Length == text.Replace("-", "").Replace(" ", "").Length
                ? query.Where(r => r.CallerPhone.Contains(digits) || r.Passengers.Any(p => p.Person!.IdNumber!.Contains(digits)))
                : query.Where(r => r.Passengers.Any(p => p.Person!.FullName.Contains(text)));
            var list = await query.OrderByDescending(r => r.CreatedAt).Take(30)
                .Include(r => r.Passengers).ThenInclude(p => p.Person).ToListAsync(ct);
            var clusters = await ClusterLabels(db, city, ct);
            return Results.Ok(list.Select(r => new
            {
                r.Id, r.Number, r.CallerPhone, status = r.Status.ToString(), r.CreatedAt, r.CancelReason,
                passengers = r.Passengers.Select(p => new { p.Person!.FullName, p.Person.IdNumber, cluster = clusters.GetValueOrDefault(p.ClusterId) }),
            }));
        });
    }

    static IQueryable<RideRequest> OpenRequests(AppDbContext db, Guid city) =>
        db.Requests.Where(r => r.CityId == city && Open.Contains(r.Status));

    public static Task<ElectionOperation?> ActiveOperation(AppDbContext db, Guid city, CancellationToken ct) =>
        db.Operations.FirstOrDefaultAsync(o => o.IsActive && o.Cities.Any(c => c.Id == city), ct);

    static async Task<Dictionary<Guid, object>> ClusterLabels(AppDbContext db, Guid city, CancellationToken ct) =>
        await db.Clusters.Where(c => c.CityId == city)
            .ToDictionaryAsync(c => c.Id, c => (object)new { c.Number, c.ShortName, c.Name }, ct);

    /// <summary>Validates the intake form and applies it to the request. Returns an error, or null when valid.</summary>
    static async Task<IResult?> Apply(RideRequest r, RequestIn b, AppDbContext db, IGeocoder geo, CancellationToken ct)
    {
        var city = r.CityId;
        var phone = Rules.DigitsOnly(b.CallerPhone);
        if (!Rules.IsValidPhone(phone)) return Problems.Error("מספר הטלפון לא תקין");
        if (b.Passengers is not { Count: > 0 }) return Problems.Error("צריך לבחור לפחות נוסע אחד");
        if (b.StreetId is null || !await db.Streets.AnyAsync(s => s.Id == b.StreetId && s.CityId == city, ct))
            return Problems.Error("צריך לבחור רחוב מהרשימה");
        var building = (b.BuildingNumber ?? "").Trim();
        if (!building.All(char.IsDigit) || building.Length is 0 or > 5) return Problems.Error("מספר הבניין הוא ספרות בלבד");
        if (b.CommunityId is not null && !await db.Communities.AnyAsync(c => c.Id == b.CommunityId && c.CityId == city, ct))
            return Problems.Error("הקהילה לא נמצאה ברשימה");
        if (!Enum.TryParse<TimeMode>(b.TimeMode, out var timeMode)) return Problems.Error("צריך לבחור מתי");
        if (!Enum.TryParse<ReturnMode>(b.ReturnMode, out var returnMode)) return Problems.Error("צריך לבחור איך חוזרים");

        string? urgency = null;
        if (timeMode == TimeMode.Between && (b.WindowFrom is null || b.WindowTo is null || b.WindowTo <= b.WindowFrom))
            return Problems.Error("צריך שעת התחלה ושעת סיום, והסיום אחרי ההתחלה");
        if (timeMode == TimeMode.LeaveBy)
        {
            if (b.WindowTo is null) return Problems.Error("צריך לבחור עד איזו שעה");
            if (!Rules.UrgencyReasons.Contains(b.UrgencyReason)) return Problems.Error("צריך לבחור למה דווקא עד אז");
            if (b.UrgencyReason == "אחר" && string.IsNullOrWhiteSpace(b.UrgencyOther)) return Problems.Error("צריך לכתוב בקצרה למה");
            urgency = b.UrgencyReason == "אחר" ? $"אחר: {b.UrgencyOther!.Trim()}" : b.UrgencyReason;
        }

        // Passengers: validate, then find or create each person.
        var clusterIds = b.Passengers.Select(p => p.ClusterId).ToList();
        if (clusterIds.Any(c => c is null)) return Problems.Error("צריך לבחור אשכול לכל נוסע");
        var clusters = await db.Clusters.Where(c => c.CityId == city && clusterIds.Contains(c.Id)).ToListAsync(ct);
        if (clusters.Count != clusterIds.Distinct().Count()) return Problems.Error("האשכול לא נמצא ברשימה");
        if (b.NeedsWheelchair && !b.KeepNonAccessible && clusters.Any(c => !c.Accessible))
            return Problems.Error("האשכול לא נגיש לכיסא גלגלים. צריך לשאול את הפונה: להשאיר את המקום, או לעבור למקום נגיש.", 422);

        var people = new List<(Person person, PassengerIn input)>();
        foreach (var p in b.Passengers)
        {
            var name = Rules.NormalizeName(p.FullName);
            if (name.Length == 0) return Problems.Error("צריך למלא שם לכל נוסע");
            if (!Enum.TryParse<Gender>(p.Gender, out var gender)) return Problems.Error($"צריך לבחור מגדר עבור {name}");
            string? id = null;
            if (!p.NoId)
            {
                if (!Rules.IsValidIdNumber(p.IdNumber)) return Problems.Error($"מספר הזהות של {name} לא תקין");
                id = Rules.NormalizeId(p.IdNumber!);
            }

            Person? person = p.PersonId is { } pid ? await db.People.Include(x => x.Phones).FirstOrDefaultAsync(x => x.Id == pid && x.CityId == city, ct) : null;
            if (person is null && id is not null)
                person = await db.People.Include(x => x.Phones).FirstOrDefaultAsync(x => x.CityId == city && x.IdNumber == id, ct);
            if (person is null && id is null)
                person = await db.People.Include(x => x.Phones)
                    .FirstOrDefaultAsync(x => x.CityId == city && x.IdNumber == null && x.FullName == name && x.Phones.Any(ph => ph.Phone == phone), ct);
            if (person is null)
            {
                person = new Person { CityId = city, FullName = name };
                db.People.Add(person);
            }
            else if (id is not null && person.IdNumber != id &&
                     await db.People.AnyAsync(x => x.CityId == city && x.IdNumber == id && x.Id != person.Id, ct))
                return Problems.Error($"מספר הזהות {id} כבר שייך לאדם אחר", 409);

            person.FullName = name;
            person.Gender = gender;
            if (id is not null) person.IdNumber = id;
            person.LastClusterId = p.ClusterId;
            if (person.Phones.All(ph => ph.Phone != phone))
            {
                var link = new PersonPhone { PersonId = person.Id, Phone = phone };
                person.Phones.Add(link);
                // Keys are set in advance, so tell EF these rows are new (not updates of existing rows).
                db.Entry(link).State = EntityState.Added;
            }
            people.Add((person, p));
        }

        // The same person in another open request today: warn, with a way to open the existing one (spec 5.1).
        if (!b.IgnoreDuplicate)
        {
            var ids = people.Select(x => x.person.Id).ToList();
            var startOfDay = StartOfToday();
            var dup = await OpenRequests(db, city)
                .Where(x => x.Id != r.Id && x.CreatedAt >= startOfDay && x.Passengers.Any(p => ids.Contains(p.PersonId)))
                .Select(x => new { x.Id, x.Number }).FirstOrDefaultAsync(ct);
            if (dup is not null)
                return Results.Json(new { error = $"לאותו אדם כבר יש פנייה פתוחה היום (פנייה מס' {dup.Number})", duplicate = dup }, statusCode: 409);
        }

        // Address location: from the saved addresses, otherwise from the geocoder. Never blocks saving.
        var cached = await db.AddressLocations.FirstOrDefaultAsync(a => a.StreetId == b.StreetId && a.BuildingNumber == building, ct);
        if (cached is null)
        {
            var street = await db.Streets.FirstAsync(s => s.Id == b.StreetId, ct);
            var cityName = await db.Cities.Where(c => c.Id == city).Select(c => c.Name).FirstAsync(ct);
            var found = await geo.FindAsync(cityName, street.Name, building, ct);
            cached = new AddressLocation { StreetId = street.Id, BuildingNumber = building, Location = found.Location, Approximate = found.Approximate };
            // Only a confirmed location is reused; an approximate one is tried again next time.
            if (!found.Approximate) db.AddressLocations.Add(cached);
        }

        r.CallerPhone = phone;
        r.StreetId = b.StreetId.Value;
        r.BuildingNumber = building;
        r.Entrance = string.IsNullOrWhiteSpace(b.Entrance) ? null : b.Entrance.Trim();
        r.DriverNotes = string.IsNullOrWhiteSpace(b.DriverNotes) ? null : b.DriverNotes.Trim();
        r.Location = cached.Location;
        r.ApproximateLocation = cached.Approximate;
        r.CommunityId = b.CommunityId;
        r.NeedsWheelchair = b.NeedsWheelchair;
        r.NeedsWalker = b.NeedsWalker;
        r.NeedsHelp = b.NeedsHelp;
        r.TimeMode = timeMode;
        r.WindowFrom = timeMode == TimeMode.Between ? b.WindowFrom : null;
        r.WindowTo = timeMode == TimeMode.Now ? null : b.WindowTo;
        r.UrgencyReason = urgency;
        r.ReturnMode = returnMode;

        // Verified at once when the ID is in the voter base, with the real ballot box. Otherwise (no ID given,
        // not in the base, or no base loaded) the passenger waits for manual verification (spec 8).
        var idNumbers = people.Where(x => x.person.IdNumber != null).Select(x => x.person.IdNumber!).ToList();
        var inBase = await db.Voters.Where(v => v.CityId == city && idNumbers.Contains(v.IdNumber)).ToDictionaryAsync(v => v.IdNumber, v => v.BallotBox, ct);
        r.Passengers.Clear();
        foreach (var (person, input) in people)
        {
            var box = person.IdNumber is { } pid2 && inBase.TryGetValue(pid2, out var b2) ? b2 : null;
            var passenger = new RequestPassenger
            {
                RequestId = r.Id, PersonId = person.Id, ClusterId = input.ClusterId!.Value, BallotBox = box, PendingVerification = box is null,
            };
            r.Passengers.Add(passenger);
            db.Entry(passenger).State = EntityState.Added;
        }
        return null;
    }

    static DateTimeOffset StartOfToday()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Israel);
        return new DateTimeOffset(now.Date, now.Offset).ToUniversalTime();
    }

    static object Detail(RideRequest r) => new
    {
        r.Id, r.Number, r.CallerPhone, status = r.Status.ToString(), r.CreatedAt,
        r.StreetId, r.BuildingNumber, r.Entrance, r.DriverNotes, r.ApproximateLocation, r.CommunityId,
        r.NeedsWheelchair, r.NeedsWalker, r.NeedsHelp,
        timeMode = r.TimeMode.ToString(), r.WindowFrom, r.WindowTo, r.UrgencyReason,
        returnMode = r.ReturnMode.ToString(),
        passengers = r.Passengers.Select(p => new
        {
            p.PersonId, p.Person!.FullName, p.Person.IdNumber, noId = p.Person.IdNumber == null,
            gender = p.Person.Gender.ToString(), p.ClusterId, p.PendingVerification, p.BallotBox,
        }),
    };
}
