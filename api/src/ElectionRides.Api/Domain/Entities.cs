namespace ElectionRides.Api.Domain;

/// <summary>A city that runs its own ride HQ. All city data hangs off CityId.</summary>
public class City
{
    public Guid Id { get; set; } = Guid.NewGuid();
    /// <summary>Locality name as it appears in the official settlements list.</summary>
    public required string Name { get; set; }
    /// <summary>Official locality code (CBS / Ministry of Interior), used to import streets.</summary>
    public int? LocalityCode { get; set; }
    /// <summary>HQ phone shown to drivers ("call HQ"). Required before the city is ready.</summary>
    public string? HqPhone { get; set; }
    /// <summary>Disabled cities keep their data, but their users cannot sign in.</summary>
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum ElectionType { Knesset = 1, Municipal = 2 }

/// <summary>One election campaign. Only one operation can be active at a time.</summary>
public class ElectionOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ElectionType Type { get; set; }
    public DateOnly ElectionDay { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public List<City> Cities { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public enum UserRole { Operator = 1, Dispatcher = 2, CityAdmin = 3, SystemAdmin = 4 }

/// <summary>HQ staff member. System admins have no city; everyone else belongs to exactly one city.</summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Email { get; set; }
    public required string FullName { get; set; }
    public UserRole Role { get; set; }
    public Guid? CityId { get; set; }
    public City? City { get; set; }
    /// <summary>Null until the user sets a password from the invitation link.</summary>
    public string? PasswordHash { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
