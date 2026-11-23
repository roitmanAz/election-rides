namespace ElectionRides.Api.Domain;

/// <summary>
/// Party-wide settings, a single row (Id = 1). The system admin edits it (spec 3.2).
/// Defaults follow the sysadmin prototype: email code on, device remembered for a day,
/// 30 minutes idle timeout, lock after 5 wrong passwords for 15 minutes.
/// </summary>
public class PartySettings
{
    public int Id { get; set; } = 1;
    public string? PartyName { get; set; }
    public string? PrivacyOfficerName { get; set; }
    public string? PrivacyOfficerEmail { get; set; }
    public string? LegalAdvisorEmail { get; set; }
    /// <summary>Days after election day when voter bases are deleted. Set per the legal advisor.</summary>
    public int? BaseDeletionDays { get; set; }

    public bool RequireEmailCode { get; set; } = true;
    public int RememberDeviceHours { get; set; } = 24;
    public int IdleTimeoutMinutes { get; set; } = 30;
    public int MaxFailedLogins { get; set; } = 5;
    public int LockMinutes { get; set; } = 15;
}

/// <summary>A one-time sign-in code sent by email when signing in from a new device.</summary>
public class LoginChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string CodeHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int Attempts { get; set; }
    public bool Used { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A device that passed the email code; it skips the code until it expires.</summary>
public class TrustedDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Invitation or reset link for setting a password.</summary>
public class PasswordToken
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Used { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Audit log entry. Rows are only ever added, never changed or deleted.</summary>
public class AuditEntry
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public Guid? UserId { get; set; }
    public string? UserEmail { get; set; }
    public Guid? CityId { get; set; }
    public required string Action { get; set; }
    public string? Details { get; set; }
}
