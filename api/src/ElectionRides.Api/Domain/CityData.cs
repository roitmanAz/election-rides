using NetTopologySuite.Geometries;

namespace ElectionRides.Api.Domain;

/// <summary>A community in the city's own list (spec 3.1). Optional on a request.</summary>
public class Community
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CityId { get; set; }
    public required string Name { get; set; }
}

public enum StreetSource { Official = 1, Manual = 2 }

/// <summary>A street of the city. Official streets come from the government list; manual ones survive a re-import.</summary>
public class Street
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CityId { get; set; }
    public required string Name { get; set; }
    public StreetSource Source { get; set; }
    /// <summary>Street code in the official list, for official streets.</summary>
    public int? OfficialCode { get; set; }
}

/// <summary>A building with several ballot boxes. Rides go to the cluster (spec 4).</summary>
public class Cluster
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CityId { get; set; }
    /// <summary>Optional, chosen by the city admin.</summary>
    public int? Number { get; set; }
    public required string Name { get; set; }
    /// <summary>Shown on the plate when there is no number. Required.</summary>
    public required string ShortName { get; set; }
    public Guid StreetId { get; set; }
    public Street? Street { get; set; }
    public required string BuildingNumber { get; set; }
    /// <summary>From Google, when found. Filled in with the maps stage.</summary>
    public Point? Location { get; set; }
    public bool Accessible { get; set; }
    /// <summary>Drop-off point and notes, shown to the driver at the destination.</summary>
    public string? DropoffNote { get; set; }
    public List<BallotBox> Boxes { get; set; } = [];
}

public class BallotBox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ClusterId { get; set; }
    public Guid CityId { get; set; }
    /// <summary>Ballot box number as published (can contain a dot, e.g. "12.1").</summary>
    public required string Number { get; set; }
}
