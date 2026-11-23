namespace ElectionRides.Api.Domain;

/// <summary>
/// A voter from the city's voter base (spec 8). Only the mapped fields are kept; the rest of the file is never
/// stored. Deleted at the end of the election period, with a deletion certificate (spec 12).
/// </summary>
public class Voter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CityId { get; set; }
    public required string IdNumber { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public required string BallotBox { get; set; }
    /// <summary>From the file's cluster column, or from the ballot box; null when the box is in no cluster yet.</summary>
    public Guid? ClusterId { get; set; }
    public int? Age { get; set; }
    public string? Street { get; set; }
    public string? BuildingNumber { get; set; }
    public List<VoterPhone> Phones { get; set; } = [];
}

public class VoterPhone
{
    public Guid VoterId { get; set; }
    public required string Phone { get; set; }
}

/// <summary>Which base is loaded in the city (one at a time).</summary>
public class VoterBaseInfo
{
    public Guid CityId { get; set; }
    public required string FileName { get; set; }
    public int VoterCount { get; set; }
    public DateTimeOffset LoadedAt { get; set; } = DateTimeOffset.UtcNow;
    public required string LoadedBy { get; set; }
}

/// <summary>
/// Deletion certificate (spec 3.1, 12): made on every deletion of a base, manual or automatic, kept in the
/// city and at the system admin, and emailed to the party's privacy officer and the city admins.
/// </summary>
public class DeletionCertificate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Number { get; set; }
    public Guid CityId { get; set; }
    public required string CityName { get; set; }
    public string? OperationText { get; set; }
    public required string FileName { get; set; }
    public int VoterCount { get; set; }
    public DateTimeOffset DeletedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>A user's name, or "מחיקה אוטומטית".</summary>
    public required string DeletedBy { get; set; }
    public required string WhatWasDeleted { get; set; }
    public required string SentTo { get; set; }
}
