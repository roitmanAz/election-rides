namespace ElectionRides.Api.Dispatch;

public enum RideKind { Men = 1, Women = 2, Family = 3 }

public record GeoPoint(double Lat, double Lon);

/// <summary>A waiting request as the dispatcher's rules see it.</summary>
public record QueueItem(
    Guid Id, RideKind Kind, int Passengers, bool Wheelchair, GeoPoint? Pickup,
    IReadOnlyList<Guid> Clusters, GeoPoint? Destination, double WaitMinutes, double? MinutesToDeadline, bool NotYet,
    bool Suspended = false);

/// <summary>Location: where the driver starts the next ride (see <see cref="ProposalEngine.StartPoint"/>).</summary>
public record DriverItem(Guid Id, string Name, int Seats, bool WheelchairCar, bool Soon, double? MinutesToFree, GeoPoint? Location);

public record Proposal(Guid DriverId, List<Guid> RequestIds);

/// <summary>
/// Version 1 dispatch rules (spec 5.3, 6, 7): no pre-assignment; every free or soon-free driver gets a proposal.
/// Queue order: requests within 30 minutes of their "leave by" time first (nearest first), then the longest wait.
/// Each request goes to the driver who reaches it first; others join only to exactly the same cluster and on the way.
/// </summary>
public static class ProposalEngine
{
    public const double DeadlineMinutes = 30;

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

    /// <summary>Travel time in minutes between two points (on the roads when known, otherwise an estimate).</summary>
    public delegate double TravelMinutes(GeoPoint a, GeoPoint b);

    /// <summary>Most extra driving one more request may add to the ride (minutes).</summary>
    public const double DetourMinutes = 5;
    /// <summary>"Regroup": a group may go before a request that has waited at most this much longer (minutes).</summary>
    public const double JumpMinutes = 10;
    /// <summary>A suspended request goes only to a driver who starts within this many minutes of it.</summary>
    public const double SuspendedNearMinutes = 8;
    /// <summary>Driving time assumed when the driver's starting point is not known.</summary>
    const double UnknownTravel = 10;

    /// <summary>Straight-line estimate: roads are about 1.3 times longer, at about 25 km/h in town.</summary>
    public static double Estimate(GeoPoint a, GeoPoint b) => Km(a, b) * 1.3 / 25 * 60;

    /// <summary>Minutes until the driver can be at the pickup: minutes until free, plus driving from the starting point.</summary>
    static double Reach(DriverItem d, QueueItem r, TravelMinutes t) =>
        (d.Soon ? d.MinutesToFree ?? 0 : 0) + (d.Location is { } l && r.Pickup is { } p ? t(l, p) : UnknownTravel);

    /// <summary>A suspended request (far area) may go only to a driver who starts close to it.</summary>
    static bool MayTake(DriverItem d, QueueItem r, TravelMinutes t) =>
        !r.Suspended || (d.Location is { } l && r.Pickup is { } p && t(l, p) <= SuspendedNearMinutes);

    /// <summary>
    /// Proposals for every free or soon-free driver (spec 5.3, 6, 7).
    /// 1. Requests near their "leave by" time come first, each to the driver who reaches it soonest.
    /// 2. Otherwise each step takes the driver and request with the best score: minutes waited minus minutes until the
    ///    driver can be there (1:1). A nearer request can go first, but only by as many minutes as it saves in driving.
    /// 3. Other requests join only to exactly the same cluster(s), fitting the car, adding at most 5 minutes each.
    /// Suspended requests (far areas) go only to a driver who starts within 8 minutes of them.
    /// With <paramref name="regroup"/>, a larger group may go first if the chosen request waited at most 10 minutes longer.
    /// </summary>
    public static List<Proposal> Build(IEnumerable<QueueItem> queue, IEnumerable<DriverItem> drivers,
        TravelMinutes? minutes = null, bool regroup = false)
    {
        var t = minutes ?? Estimate;
        var ordered = Order(queue.Where(q => !q.NotYet));
        var all = drivers.Where(d => !d.Soon).Concat(drivers.Where(d => d.Soon).OrderBy(d => d.MinutesToFree ?? 0)).ToList();
        var left = all.ToList();
        var taken = new HashSet<Guid>();
        var cars = new Dictionary<Guid, List<QueueItem>>();
        while (left.Count > 0)
        {
            var open = ordered.Where(r => !taken.Contains(r.Id)).ToList();
            bool Fits(DriverItem d, QueueItem r) => WhyNot(d, [], r) == "" && MayTake(d, r, t);
            var pairs = (from r in open from d in left where Fits(d, r) select (d, r)).ToList();
            if (pairs.Count == 0) break;

            (DriverItem d, QueueItem r) pick;
            var urgent = pairs.Where(x => DeadlineSoon(x.r)).ToList();
            if (urgent.Count > 0)
            {
                var first = urgent.Select(x => x.r).OrderBy(r => r.MinutesToDeadline).First();
                pick = urgent.Where(x => x.r == first).MinBy(x => Reach(x.d, x.r, t));
            }
            else
                pick = pairs.MaxBy(x => x.r.WaitMinutes - Reach(x.d, x.r, t));

            var best = (driver: pick.d, car: Group(pick.r, pick.d, open, t));
            if (regroup && !DeadlineSoon(pick.r))
                foreach (var c in open.Where(c => c != pick.r && !c.Suspended).Take(30))
                {
                    var d = left.Where(x => Fits(x, c)).MinBy(x => Reach(x, c, t));
                    if (d is null) continue;
                    var alt = Group(c, d, open, t);
                    if (alt.Sum(x => x.Passengers) > best.car.Sum(x => x.Passengers)
                        && pick.r.WaitMinutes - alt.Max(x => x.WaitMinutes) <= JumpMinutes)
                        best = (d, alt);
                }
            cars[best.driver.Id] = best.car;
            left.Remove(best.driver);
            foreach (var r in best.car) taken.Add(r.Id);
        }
        return all.Select(d => new Proposal(d.Id, cars.GetValueOrDefault(d.Id)?.Select(r => r.Id).ToList() ?? [])).ToList();
    }

    /// <summary>The seed request and the requests that join it on the way, for this driver.</summary>
    static List<QueueItem> Group(QueueItem seed, DriverItem driver, List<QueueItem> open, TravelMinutes t)
    {
        var car = new List<QueueItem> { seed };
        if (seed.Kind == RideKind.Family || seed.Pickup is null) return car;
        var clusters = seed.Clusters.ToHashSet();
        while (true)
        {
            var now = RideMinutes(driver.Location, car, t);
            var next = open
                .Where(r => !car.Contains(r) && r.Pickup is not null && clusters.SetEquals(r.Clusters) && WhyNot(driver, car, r) == "")
                .Select(r => (r, extra: RideMinutes(driver.Location, [.. car, r], t) - now))
                .Where(x => x.extra <= DetourMinutes)
                .OrderBy(x => x.extra)
                .FirstOrDefault();
            if (next.r is null) return car;
            car.Add(next.r);
        }
    }

    /// <summary>
    /// Where a driver starts the next ride (spec 7): where the last passenger got off (home, when the driver brought
    /// people back; otherwise the cluster), then the app's location if at most 5 minutes old, then the area written
    /// for the driver. Null when none is known.
    /// </summary>
    public static GeoPoint? StartPoint(GeoPoint? lastDropOff, GeoPoint? appLocation, DateTimeOffset? appAt, GeoPoint? area, DateTimeOffset now) =>
        lastDropOff ?? (appLocation is not null && appAt is { } at && now - at <= TimeSpan.FromMinutes(5) ? appLocation : null) ?? area;

    /// <summary>Driving minutes for a car: from the driver (or the first pickup), pickups nearest-next, then the cluster.</summary>
    static double RideMinutes(GeoPoint? start, List<QueueItem> car, TravelMinutes t)
    {
        var left = car.Where(r => r.Pickup is not null).ToList();
        var cur = start ?? left[0].Pickup!;
        double total = 0;
        while (left.Count > 0)
        {
            var next = left.MinBy(r => t(cur, r.Pickup!))!;
            total += t(cur, next.Pickup!);
            cur = next.Pickup!;
            left.Remove(next);
        }
        if (car[0].Destination is { } dest) total += t(cur, dest);
        return total;
    }

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
