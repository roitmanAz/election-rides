using NetTopologySuite.Geometries;

namespace ElectionRides.Api.Domain;

/// <summary>Driver status (spec 5.7). "Soon" means "free in X minutes".</summary>
public enum DriverStatus { Free = 1, Soon = 2, OnTrip = 3, Break = 4, Finished = 5 }

/// <summary>
/// A volunteer driver. Kept for good (spec 5.7): a driver who finished, or who drove in an earlier election,
/// is found again by phone. "On today's list" means joined in the active operation and not finished.
/// All cars are private, so the car is part of the driver (spec 4).
/// </summary>
public class Driver
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CityId { get; set; }
    public required string FullName { get; set; }
    public required string Phone { get; set; }
    public bool HasSmartphone { get; set; }
    public int Seats { get; set; }
    public bool WheelchairCar { get; set; }

    /// <summary>The operation the driver joined last; null when not on any list.</summary>
    public Guid? OperationId { get; set; }
    public DriverStatus Status { get; set; } = DriverStatus.Free;
    public DateTimeOffset StatusSince { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>For "free in X minutes": when the driver expects to be free.</summary>
    public DateTimeOffset? FreeAt { get; set; }
    /// <summary>"After this ride I take a break" (spec 5.7).</summary>
    public bool BreakAfterTrip { get; set; }
    /// <summary>Last known location, sent by the driver app with each button press (no background tracking).</summary>
    public Point? Location { get; set; }
    public DateTimeOffset? LocationAt { get; set; }
    /// <summary>
    /// Personal sign-in link for the driver app (spec 12): one use, valid until the end of election day.
    /// Only its hash is stored. The dispatcher copies the link and sends it to the driver.
    /// </summary>
    public string? AccessTokenHash { get; set; }
    public DateTimeOffset? AccessTokenExpires { get; set; }
}

/// <summary>Offered: sent to a driver with the app, waiting for "I take it" (2 minutes, spec 5.4).</summary>
public enum TripStatus { Active = 1, Done = 2, Offered = 3, Cancelled = 9 }

/// <summary>A ride given to one driver: pickups, then drop-offs at the clusters, in order.</summary>
public class Trip
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CityId { get; set; }
    public Guid OperationId { get; set; }
    public Guid DriverId { get; set; }
    public Driver? Driver { get; set; }
    public TripStatus Status { get; set; } = TripStatus.Active;
    public Guid AssignedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AcceptedAt { get; set; }
    /// <summary>Why the driver could not take it (closed list; "other" with a short text).</summary>
    public string? DeclineReason { get; set; }
    public List<TripStop> Stops { get; set; } = [];
}

public enum StopKind { Pickup = 1, DropOff = 2 }

public class TripStop
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public int Order { get; set; }
    public StopKind Kind { get; set; }
    /// <summary>The request picked up here (pickup stops).</summary>
    public Guid? RequestId { get; set; }
    /// <summary>The cluster to drop at (drop-off stops).</summary>
    public Guid? ClusterId { get; set; }
    public DateTimeOffset? DoneAt { get; set; }
}
