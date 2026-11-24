using System.Security.Claims;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Intake;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.CityScreens;

/// <summary>
/// City admin dashboard (spec 3.1): today's numbers, requests per half hour, drivers now,
/// cancel reasons and the load per operator. Counts only; no voter details.
/// </summary>
public static class DashboardEndpoints
{
    static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");

    public record WaitItem(DateTimeOffset CreatedAt, TimeMode Mode, TimeOnly? WindowFrom, DateTimeOffset? AssignedAt, RequestStatus Status);

    /// <summary>
    /// Average wait of the day's requests, in whole minutes: from when the wait starts (saving, or the start of a
    /// pickup window) until a driver was assigned; requests still in the queue count until now. Cancelled requests,
    /// and windows that have not started yet, are left out.
    /// </summary>
    public static int AverageWait(DateTimeOffset now, IEnumerable<WaitItem> items)
    {
        var waits = items.Where(r => r.Status != RequestStatus.Cancelled)
            .Select(r => (since: Waiting.Since(r.CreatedAt, r.Mode, r.WindowFrom),
                          end: r.AssignedAt ?? (r.Status == RequestStatus.Queued ? now : (DateTimeOffset?)null)))
            .Where(x => x.end is { } e && x.since <= now)
            .Select(x => Math.Max(0, (x.end!.Value - x.since).TotalMinutes))
            .ToList();
        return waits.Count == 0 ? 0 : (int)Math.Round(waits.Average());
    }

    public static void MapDashboard(this WebApplication app) =>
        app.MapGet("/api/city/dashboard", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var city = me.CityId();
            var op = await IntakeEndpoints.ActiveOperation(db, city, ct);
            if (op is null) return Results.Ok(new { active = false });
            var now = DateTimeOffset.UtcNow;
            var local = TimeZoneInfo.ConvertTime(now, Israel);
            var dayStart = new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();

            var today = await db.Requests.Where(r => r.CityId == city && r.OperationId == op.Id && r.CreatedAt >= dayStart)
                .Select(r => new { r.CreatedAt, r.Status, r.CancelReason, r.TakenById, r.TimeMode, r.WindowFrom, r.AssignedAt }).ToListAsync(ct);
            var waiting = await db.Requests.Where(r => r.CityId == city && r.OperationId == op.Id && r.Status == RequestStatus.Queued)
                .Select(r => new { r.CreatedAt, r.TimeMode, r.WindowFrom }).ToListAsync(ct);
            var waits = waiting.Select(c => Waiting.Minutes(now, c.CreatedAt, c.TimeMode, c.WindowFrom)).ToList();

            // Half-hour buckets from the first request of the day to now, oldest first (time runs left to right).
            var buckets = today.GroupBy(r =>
                {
                    var t = TimeZoneInfo.ConvertTime(r.CreatedAt, Israel);
                    return new TimeOnly(t.Hour, t.Minute < 30 ? 0 : 30);
                })
                .ToDictionary(g => g.Key, g => g.Count());
            var perHalfHour = new List<object>();
            if (buckets.Count > 0)
            {
                var start = buckets.Keys.Min();
                var end = new TimeOnly(local.Hour, local.Minute < 30 ? 0 : 30);
                for (var t = start; t <= end; t = t.AddMinutes(30))
                {
                    perHalfHour.Add(new { from = t.ToString("HH:mm"), count = buckets.GetValueOrDefault(t) });
                    if (t == new TimeOnly(23, 30)) break;
                }
            }

            var drivers = await db.Drivers.Where(d => d.CityId == city && d.OperationId == op.Id && d.Status != DriverStatus.Finished)
                .GroupBy(d => d.Status).Select(g => new { status = g.Key, n = g.Count() }).ToListAsync(ct);
            int Count(DriverStatus s) => drivers.FirstOrDefault(x => x.status == s)?.n ?? 0;

            var operatorIds = today.Select(r => r.TakenById).Distinct().ToList();
            var names = await db.Users.Where(u => operatorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);

            return Results.Ok(new
            {
                active = true,
                requestsToday = today.Count,
                queued = waiting.Count,
                averageWait = AverageWait(now, today.Select(r => new WaitItem(r.CreatedAt, r.TimeMode, r.WindowFrom, r.AssignedAt, r.Status))),
                longestWait = waits.Count == 0 ? 0 : (int)Math.Floor(waits.Max()),
                arrived = today.Count(r => r.Status is RequestStatus.Arrived or RequestStatus.Done),
                cancelled = today.Count(r => r.Status == RequestStatus.Cancelled),
                perHalfHour,
                drivers = new
                {
                    active = drivers.Sum(x => x.n),
                    free = Count(DriverStatus.Free),
                    soon = Count(DriverStatus.Soon),
                    onTrip = Count(DriverStatus.OnTrip),
                    onBreak = Count(DriverStatus.Break),
                },
                cancelReasons = today.Where(r => r.Status == RequestStatus.Cancelled)
                    .GroupBy(r => (r.CancelReason ?? "").StartsWith("אחר") ? "אחר" : r.CancelReason ?? "")
                    .Select(g => new { reason = g.Key, count = g.Count() }).OrderByDescending(x => x.count),
                operators = today.GroupBy(r => r.TakenById)
                    .Select(g => new { name = names.GetValueOrDefault(g.Key) ?? "", count = g.Count() }).OrderByDescending(x => x.count),
            });
        }).RequireAuthorization(Policies.CityAdmin);
}
