using System.Globalization;
using System.Security.Claims;
using System.Text;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Dispatch;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Intake;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.CityScreens;

/// <summary>
/// City admin analytics (spec 3.1): the whole active operation, or one day of it. Numbers, requests per hour and
/// per day, waits, operators, drivers, clusters, communities, needs and cancel reasons, and an export for Excel.
/// Staff names appear; voters' names, phones and ID numbers never do.
/// </summary>
public static class AnalyticsEndpoints
{
    static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");

    static readonly Dictionary<RequestStatus, string> StatusNames = new()
    {
        [RequestStatus.Queued] = "בתור", [RequestStatus.Assigned] = "שובץ", [RequestStatus.PickedUp] = "נאסף",
        [RequestStatus.Arrived] = "הגיע לקלפי", [RequestStatus.Done] = "הסתיים", [RequestStatus.Cancelled] = "בוטל",
    };

    record Row(Guid Id, int Number, DateTimeOffset CreatedAt, RequestStatus Status, DateTimeOffset? AssignedAt, TimeMode TimeMode,
        TimeOnly? WindowFrom, Guid TakenById, Guid? CommunityId, bool Wheelchair, bool Walker, bool Help, string? CancelReason,
        int? IntakeSeconds, (double lat, double lon)? Location, List<Guid> Clusters, int Passengers);

    static DateOnly LocalDay(DateTimeOffset t) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, Israel).DateTime);

    /// <summary>Minutes from the start of the wait until a driver was assigned (queued: until now); null if not counted.</summary>
    static double? WaitOf(Row r, DateTimeOffset now)
    {
        if (r.Status == RequestStatus.Cancelled) return null;
        var since = Waiting.Since(r.CreatedAt, r.TimeMode, r.WindowFrom);
        var end = r.AssignedAt ?? (r.Status == RequestStatus.Queued ? now : null);
        if (end is null || since > now) return null;
        return Math.Max(0, (end.Value - since).TotalMinutes);
    }

    static async Task<(ElectionOperation? op, List<Row> rows, List<DateOnly> days)> Load(ClaimsPrincipal me, AppDbContext db, string? day, CancellationToken ct)
    {
        var city = me.CityId();
        var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
        if (op is null) return (null, [], []);
        var raw = await db.Requests.Where(r => r.CityId == city && r.OperationId == op.Id)
            .Select(r => new
            {
                r.Id, r.Number, r.CreatedAt, r.Status, r.AssignedAt, r.TimeMode, r.WindowFrom, r.TakenById, r.CommunityId,
                r.NeedsWheelchair, r.NeedsWalker, r.NeedsHelp, r.CancelReason, r.IntakeSeconds, r.Location,
                clusters = r.Passengers.Select(p => p.ClusterId).ToList(),
            })
            .ToListAsync(ct);
        var rows = raw.Select(r => new Row(r.Id, r.Number, r.CreatedAt, r.Status, r.AssignedAt, r.TimeMode, r.WindowFrom, r.TakenById,
            r.CommunityId, r.NeedsWheelchair, r.NeedsWalker, r.NeedsHelp, r.CancelReason, r.IntakeSeconds,
            DispatchEndpoints.Point(r.Location), r.clusters, r.clusters.Count)).ToList();
        var days = rows.Select(r => LocalDay(r.CreatedAt)).Distinct().Order().ToList();
        if (DateOnly.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            rows = rows.Where(r => LocalDay(r.CreatedAt) == d).ToList();
        return (op, rows, days);
    }

    public static void MapAnalytics(this WebApplication app)
    {
        app.MapGet("/api/city/analytics", async (string? day, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var (op, rows, days) = await Load(me, db, day, ct);
            if (op is null) return Results.Ok(new { active = false });
            var city = me.CityId();
            var now = DateTimeOffset.UtcNow;
            var ids = rows.Select(r => r.Id).ToHashSet();
            var waits = rows.Select(r => WaitOf(r, now)).OfType<double>().ToList();

            // Trips that picked up these requests (sent and taken; offers that ended are left out).
            var trips = await db.Trips.Where(t => t.CityId == city && t.OperationId == op.Id && (t.Status == TripStatus.Active || t.Status == TripStatus.Done))
                .Select(t => new { t.Id, t.DriverId, stops = t.Stops.OrderBy(s => s.Order).Select(s => new { s.Kind, s.RequestId, s.ClusterId }).ToList() })
                .ToListAsync(ct);
            trips = trips.Where(t => t.stops.Any(s => s.RequestId is { } id && ids.Contains(id))).ToList();

            var clusters = await db.Clusters.Where(c => c.CityId == city)
                .Select(c => new { c.Id, c.Number, c.Name, c.Location }).ToListAsync(ct);
            var clusterPoint = clusters.ToDictionary(c => c.Id, c => DispatchEndpoints.Point(c.Location));
            var rowById = rows.ToDictionary(r => r.Id);

            // Estimated kilometres of a trip: straight lines between its stops, times 1.3 for the roads.
            double TripKm(IEnumerable<(Guid? req, Guid? cluster)> stops)
            {
                (double lat, double lon)? prev = null;
                double km = 0;
                foreach (var (req, cl) in stops)
                {
                    var p = req is { } r ? rowById.GetValueOrDefault(r)?.Location : cl is { } c ? clusterPoint.GetValueOrDefault(c) : null;
                    if (p is null) continue;
                    if (prev is { } a) km += ProposalEngine.Km(new GeoPoint(a.lat, a.lon), new GeoPoint(p.Value.lat, p.Value.lon)) * 1.3;
                    prev = p;
                }
                return km;
            }

            var staffIds = rows.Select(r => r.TakenById).Distinct().ToList();
            var staff = await db.Users.Where(u => staffIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
            var driverIds = trips.Select(t => t.DriverId).Distinct().ToList();
            var driverNames = await db.Drivers.Where(d => driverIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.FullName, ct);
            var communities = await db.Communities.Where(c => c.CityId == city).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

            var served = rows.Where(r => r.Status is RequestStatus.Arrived or RequestStatus.Done).ToList();
            var tripPassengers = trips.Select(t => t.stops.Where(s => s.RequestId is { } id && ids.Contains(id)).Sum(s => rowById[s.RequestId!.Value].Passengers)).ToList();
            var intakes = rows.Where(r => r.IntakeSeconds is > 0).Select(r => r.IntakeSeconds!.Value).ToList();

            int Round(double v) => (int)Math.Round(v);
            return Results.Ok(new
            {
                active = true,
                days = days.Select(d => d.ToString("yyyy-MM-dd")),
                totals = new
                {
                    requests = rows.Count,
                    passengers = rows.Where(r => r.Status != RequestStatus.Cancelled).Sum(r => r.Passengers),
                    arrived = served.Count,
                    cancelled = rows.Count(r => r.Status == RequestStatus.Cancelled),
                    queued = rows.Count(r => r.Status == RequestStatus.Queued),
                    averageWait = waits.Count == 0 ? 0 : Round(waits.Average()),
                    longestWait = waits.Count == 0 ? 0 : Round(waits.Max()),
                    trips = trips.Count,
                    passengersPerTrip = tripPassengers.Count == 0 ? 0 : Math.Round(tripPassengers.Average(), 1),
                    averageIntakeSeconds = intakes.Count == 0 ? (int?)null : Round(intakes.Average()),
                },
                perHour = rows.GroupBy(r => TimeZoneInfo.ConvertTime(r.CreatedAt, Israel).Hour).OrderBy(g => g.Key)
                    .Select(g => new { hour = $"{g.Key:00}:00", count = g.Count() }),
                perDay = rows.GroupBy(r => LocalDay(r.CreatedAt)).OrderBy(g => g.Key)
                    .Select(g => new { day = g.Key.ToString("yyyy-MM-dd"), count = g.Count() }),
                waitBuckets = new[]
                {
                    new { label = "עד 5 דקות", count = waits.Count(w => w < 5) },
                    new { label = "5 עד 15 דקות", count = waits.Count(w => w is >= 5 and < 15) },
                    new { label = "15 עד 30 דקות", count = waits.Count(w => w is >= 15 and < 30) },
                    new { label = "30 דקות ומעלה", count = waits.Count(w => w >= 30) },
                },
                operators = rows.GroupBy(r => r.TakenById).Select(g =>
                {
                    var secs = g.Where(r => r.IntakeSeconds is > 0).Select(r => r.IntakeSeconds!.Value).ToList();
                    return new
                    {
                        name = staff.GetValueOrDefault(g.Key) ?? "",
                        requests = g.Count(),
                        cancelled = g.Count(r => r.Status == RequestStatus.Cancelled),
                        averageIntakeSeconds = secs.Count == 0 ? (int?)null : Round(secs.Average()),
                    };
                }).OrderByDescending(x => x.requests),
                drivers = trips.GroupBy(t => t.DriverId).Select(g => new
                {
                    name = driverNames.GetValueOrDefault(g.Key) ?? "",
                    trips = g.Count(),
                    passengers = g.Sum(t => t.stops.Where(s => s.RequestId is { } id && ids.Contains(id)).Sum(s => rowById[s.RequestId!.Value].Passengers)),
                    km = Math.Round(g.Sum(t => TripKm(t.stops.Select(s => (s.RequestId, s.ClusterId)))), 1),
                }).OrderByDescending(x => x.trips),
                clusters = rows.Where(r => r.Status != RequestStatus.Cancelled).SelectMany(r => r.Clusters.Distinct().Select(c => (c, r)))
                    .GroupBy(x => x.c).Select(g =>
                    {
                        var c = clusters.FirstOrDefault(x => x.Id == g.Key);
                        return new { number = c?.Number, name = c?.Name ?? "", requests = g.Count() };
                    }).OrderByDescending(x => x.requests),
                communities = rows.Where(r => r.Status != RequestStatus.Cancelled).GroupBy(r => r.CommunityId)
                    .Select(g => new { name = g.Key is { } id ? communities.GetValueOrDefault(id) ?? "" : "בלי קהילה", requests = g.Count() })
                    .OrderByDescending(x => x.requests),
                needs = new
                {
                    wheelchair = rows.Count(r => r.Wheelchair && r.Status != RequestStatus.Cancelled),
                    walker = rows.Count(r => r.Walker && r.Status != RequestStatus.Cancelled),
                    help = rows.Count(r => r.Help && r.Status != RequestStatus.Cancelled),
                },
                timeModes = new
                {
                    now = rows.Count(r => r.TimeMode == TimeMode.Now),
                    between = rows.Count(r => r.TimeMode == TimeMode.Between),
                    leaveBy = rows.Count(r => r.TimeMode == TimeMode.LeaveBy),
                },
                cancelReasons = rows.Where(r => r.Status == RequestStatus.Cancelled)
                    .GroupBy(r => (r.CancelReason ?? "").StartsWith("אחר") ? "אחר" : r.CancelReason ?? "")
                    .Select(g => new { reason = g.Key, count = g.Count() }).OrderByDescending(x => x.count),
            });
        }).RequireAuthorization(Policies.CityAdmin);

        // One line per request, for Excel (UTF-8 with BOM so Hebrew opens right). No voter details.
        app.MapGet("/api/city/analytics/export", async (string? day, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var (op, rows, _) = await Load(me, db, day, ct);
            if (op is null) return Results.NotFound();
            var city = me.CityId();
            var now = DateTimeOffset.UtcNow;
            var staffIds = rows.Select(r => r.TakenById).Distinct().ToList();
            var staff = await db.Users.Where(u => staffIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
            var clusters = await db.Clusters.Where(c => c.CityId == city).ToDictionaryAsync(c => c.Id, c => c.Number != null ? $"{c.Number} {c.Name}" : c.Name, ct);
            var communities = await db.Communities.Where(c => c.CityId == city).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
            var driverOf = await db.Trips.Where(t => t.CityId == city && t.OperationId == op.Id && (t.Status == TripStatus.Active || t.Status == TripStatus.Done))
                .SelectMany(t => t.Stops.Where(s => s.RequestId != null).Select(s => new { s.RequestId, name = t.Driver!.FullName }))
                .ToListAsync(ct);

            static string Cell(string? v) => v is null ? "" : v.Contains(',') || v.Contains('"') || v.Contains('\n') ? $"\"{v.Replace("\"", "\"\"")}\"" : v;
            var sb = new StringBuilder();
            sb.AppendLine("מס' פנייה,תאריך,שעה,סטטוס,נוסעים,המתנה עד שיבוץ (דקות),מוקדן,זמן קליטה (שניות),נהג,אשכול,קהילה,צרכים,זמן,סיבת ביטול");
            foreach (var r in rows.OrderBy(r => r.Number))
            {
                var local = TimeZoneInfo.ConvertTime(r.CreatedAt, Israel);
                var wait = WaitOf(r, now);
                var needs = string.Join(" / ", new[] { r.Wheelchair ? "כיסא גלגלים" : null, r.Walker ? "הליכון" : null, r.Help ? "צריך עזרה" : null }.OfType<string>());
                var mode = r.TimeMode switch { TimeMode.Between => "בין שעות", TimeMode.LeaveBy => "לצאת עד", _ => "עכשיו" };
                sb.AppendLine(string.Join(",",
                    r.Number.ToString(CultureInfo.InvariantCulture), local.ToString("dd.MM.yyyy"), local.ToString("HH:mm"), StatusNames[r.Status],
                    r.Passengers.ToString(CultureInfo.InvariantCulture), wait is null ? "" : Math.Round(wait.Value).ToString(CultureInfo.InvariantCulture),
                    Cell(staff.GetValueOrDefault(r.TakenById)), r.IntakeSeconds?.ToString(CultureInfo.InvariantCulture) ?? "",
                    Cell(driverOf.FirstOrDefault(x => x.RequestId == r.Id)?.name),
                    Cell(string.Join(" / ", r.Clusters.Distinct().Select(c => clusters.GetValueOrDefault(c) ?? ""))),
                    Cell(r.CommunityId is { } cid ? communities.GetValueOrDefault(cid) : ""), Cell(needs), mode, Cell(r.CancelReason)));
            }
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            var name = $"analytics-{(string.IsNullOrEmpty(day) ? "operation" : day)}.csv";
            return Results.File(bytes, "text/csv; charset=utf-8", name);
        }).RequireAuthorization(Policies.CityAdmin);
    }
}
