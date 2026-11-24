using NetTopologySuite.Geometries;

namespace ElectionRides.Api.Domain;

public enum Gender { Male = 1, Female = 2 }

/// <summary>
/// A person who travelled or called. Identified by ID number; someone who did not give one is
/// marked NoId and matched by phone and name (spec 4). Kept between elections (spec 12).
/// </summary>
public class Person
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CityId { get; set; }
    public string? IdNumber { get; set; }
    public required string FullName { get; set; }
    public Gender Gender { get; set; }
    /// <summary>The cluster chosen last time, offered again on the next call.</summary>
    public Guid? LastClusterId { get; set; }
    public List<PersonPhone> Phones { get; set; } = [];
}

/// <summary>A phone shared by several people (a home phone) links to each of them.</summary>
public class PersonPhone
{
    public Guid PersonId { get; set; }
    public required string Phone { get; set; }
}

public enum RequestStatus { Queued = 1, Assigned = 2, PickedUp = 3, Arrived = 4, Done = 5, Cancelled = 9 }
public enum TimeMode { Now = 1, Between = 2, LeaveBy = 3 }
public enum ReturnMode { DriverWaits = 1, CallsSeparately = 2, NoReturn = 3 }

/// <summary>When a request starts waiting (spec 6): when it was saved, or for a pickup window
/// ("between 14:00 and 15:00") the start of the window if that is later. Before that it is not waiting.</summary>
public static class Waiting
{
    static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");

    public static DateTimeOffset Since(DateTimeOffset created, TimeMode mode, TimeOnly? windowFrom)
    {
        if (mode != TimeMode.Between || windowFrom is not { } from) return created;
        var day = TimeZoneInfo.ConvertTime(created, Israel).Date;
        var local = day + from.ToTimeSpan();
        var start = new DateTimeOffset(local, Israel.GetUtcOffset(local));
        return start > created ? start : created;
    }

    /// <summary>Whole minutes waited so far, never below zero.</summary>
    public static double Minutes(DateTimeOffset now, DateTimeOffset created, TimeMode mode, TimeOnly? windowFrom) =>
        Math.Max(0, (now - Since(created, mode, windowFrom)).TotalMinutes);
}

/// <summary>A ride request taken by an operator (spec 5.1).</summary>
public class RideRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CityId { get; set; }
    public Guid OperationId { get; set; }
    /// <summary>"פנייה מס'": a running number per city and operation.</summary>
    public int Number { get; set; }
    public required string CallerPhone { get; set; }
    public Guid TakenById { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Pickup address
    public Guid StreetId { get; set; }
    public Street? Street { get; set; }
    public required string BuildingNumber { get; set; }
    public string? Entrance { get; set; }
    public string? DriverNotes { get; set; }
    public Point? Location { get; set; }
    /// <summary>Google did not find the building; the location is the street's center (spec 5.1).</summary>
    public bool ApproximateLocation { get; set; }

    public Guid? CommunityId { get; set; }
    public bool NeedsWheelchair { get; set; }
    public bool NeedsWalker { get; set; }
    public bool NeedsHelp { get; set; }

    public TimeMode TimeMode { get; set; }
    public TimeOnly? WindowFrom { get; set; }
    public TimeOnly? WindowTo { get; set; }
    /// <summary>Reason for a "must leave by" limit, from the closed list (spec 6).</summary>
    public string? UrgencyReason { get; set; }
    public ReturnMode ReturnMode { get; set; } = ReturnMode.DriverWaits;

    public RequestStatus Status { get; set; } = RequestStatus.Queued;
    /// <summary>When a driver was given the request; the wait for reports is from CreatedAt to here.</summary>
    public DateTimeOffset? AssignedAt { get; set; }
    public string? CancelReason { get; set; }
    public List<RequestPassenger> Passengers { get; set; } = [];
}

public class RequestPassenger
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RequestId { get; set; }
    public Guid PersonId { get; set; }
    public Person? Person { get; set; }
    public Guid ClusterId { get; set; }
    /// <summary>The voter's real ballot box, when the voter base is loaded (stage 7).</summary>
    public string? BallotBox { get; set; }
    /// <summary>Needs manual verification: no ID given, or no voter base (spec 8).</summary>
    public bool PendingVerification { get; set; }
}

/// <summary>A geocoded address, reused next time without asking Google (spec 5.1).</summary>
public class AddressLocation
{
    public Guid StreetId { get; set; }
    public required string BuildingNumber { get; set; }
    public Point? Location { get; set; }
    public bool Approximate { get; set; }
    public DateTimeOffset CheckedAt { get; set; } = DateTimeOffset.UtcNow;
}
