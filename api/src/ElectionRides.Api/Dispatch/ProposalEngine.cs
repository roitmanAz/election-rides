namespace ElectionRides.Api.Dispatch;

public enum RideKind { Men = 1, Women = 2, Family = 3 }

public record GeoPoint(double Lat, double Lon);

/// <summary>A waiting request as the dispatcher's rules see it.</summary>
public record QueueItem(
    Guid Id, RideKind Kind, int Passengers, bool Wheelchair, GeoPoint? Pickup,
    IReadOnlyList<Guid> Clusters, GeoPoint? Destination, double WaitMinutes, double? MinutesToDeadline, bool NotYet);

public record DriverItem(Guid Id, string Name, int Seats, bool WheelchairCar, bool Soon, double? MinutesToFree, GeoPoint? Location);

public record Proposal(Guid DriverId, List<Guid> RequestIds);

/// <summary>
/// Version 1 dispatch rules (spec 5.3, 6, 7): no pre-assignment; every free or soon-free driver gets a proposal.
/// Queue order: requests within 30 minutes of their "leave by" time first (nearest first), then the longest wait.
/// The proposal starts with the first request that fits the car, then adds nearby requests that fit too.
/// Distances are straight lines in version 1 (OSRM after version 1).
/// </summary>
public static class ProposalEngine
{
    public const double DeadlineMinutes = 30;
    /// <summary>"Nearby" for joining a ride: pickups and destinations within this distance.</summary>
    public const double NearbyKm = 1.5;

    public static bool DeadlineSoon(QueueItem r) => r.MinutesToDeadline is <= DeadlineMinutes;

    public static List<QueueItem> Order(IEnumerable<QueueItem> queue) =>
        queue.OrderByDescending(DeadlineSoon)
             .ThenBy(r => DeadlineSoon(r) ? r.MinutesToDeadline : 0)
             .ThenByDescending(r => r.WaitMinutes)
             .ToList();

    /// <summary>Why a request cannot join this car, in the short words of spec 5.3; empty when it can.</summary>
    public static string WhyNot(DriverItem d, IReadOnlyList<QueueItem> inCar, QueueItem r)
    {
        if (inCar.Any(x => x.Id == r.Id)) return "כבר בהצעה";
        if (r.Wheelchair && !d.WheelchairCar) return "הרכב לא נגיש";
        if (inCar.Sum(x => x.Passengers) + r.Passengers > d.Seats) return "אין מקום ברכב";
        if (inCar.Count > 0)
        {
            if (r.Kind == RideKind.Family || inCar[0].Kind == RideKind.Family) return "משפחה נוסעת לבד";
            if (r.Kind != inCar[0].Kind) return inCar[0].Kind == RideKind.Men ? "ברכב גברים" : "ברכב נשים";
        }
        return "";
    }

    /// <summary>Free drivers first, then soon-free drivers by how soon they are free.</summary>
    public static List<Proposal> Build(IEnumerable<QueueItem> queue, IEnumerable<DriverItem> drivers)
    {
        var ordered = Order(queue.Where(q => !q.NotYet));
        var taken = new HashSet<Guid>();
        var result = new List<Proposal>();
        var list = drivers.Where(d => !d.Soon).Concat(drivers.Where(d => d.Soon).OrderBy(d => d.MinutesToFree ?? 0));
        foreach (var d in list)
        {
            var car = new List<QueueItem>();
            var first = ordered.FirstOrDefault(r => !taken.Contains(r.Id) && WhyNot(d, car, r) == "");
            if (first is not null)
            {
                car.Add(first);
                taken.Add(first.Id);
                if (first.Kind != RideKind.Family)
                    foreach (var r in ordered.Where(r => !taken.Contains(r.Id) && Near(r.Pickup, first.Pickup) && Near(r.Destination, first.Destination)))
                        if (WhyNot(d, car, r) == "")
                        {
                            car.Add(r);
                            taken.Add(r.Id);
                        }
            }
            result.Add(new Proposal(d.Id, car.Select(c => c.Id).ToList()));
        }
        return result;
    }

    static bool Near(GeoPoint? a, GeoPoint? b) => a is not null && b is not null && Km(a, b) <= NearbyKm;

    /// <summary>Straight-line distance in kilometres (haversine).</summary>
    public static double Km(GeoPoint a, GeoPoint b)
    {
        const double R = 6371;
        double dLat = Rad(b.Lat - a.Lat), dLon = Rad(b.Lon - a.Lon);
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(a.Lat)) * Math.Cos(Rad(b.Lat)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Asin(Math.Sqrt(h));
    }

    static double Rad(double d) => d * Math.PI / 180;

    /// <summary>
    /// Stop order: pickups by nearest next (from the driver, or from the first pickup), then each cluster
    /// once, nearest next from the last pickup. Requests without a location keep their queue order.
    /// </summary>
    public static (List<Guid> pickups, List<Guid> dropoffs) OrderStops(
        GeoPoint? start, IReadOnlyList<QueueItem> car, IReadOnlyDictionary<Guid, GeoPoint?> clusterPoints)
    {
        var left = car.ToList();
        var pickups = new List<Guid>();
        var cur = start ?? left.FirstOrDefault(x => x.Pickup is not null)?.Pickup;
        while (left.Count > 0)
        {
            var next = cur is null ? left[0] : left.OrderBy(x => x.Pickup is null ? double.MaxValue : Km(cur, x.Pickup)).First();
            pickups.Add(next.Id);
            left.Remove(next);
            cur = next.Pickup ?? cur;
        }
        var clusters = car.SelectMany(c => c.Clusters).Distinct().ToList();
        var drops = new List<Guid>();
        while (clusters.Count > 0)
        {
            var next = cur is null ? clusters[0]
                : clusters.OrderBy(c => clusterPoints.GetValueOrDefault(c) is { } p ? Km(cur, p) : double.MaxValue).First();
            drops.Add(next);
            clusters.Remove(next);
            cur = clusterPoints.GetValueOrDefault(next) ?? cur;
        }
        return (pickups, drops);
    }
}
