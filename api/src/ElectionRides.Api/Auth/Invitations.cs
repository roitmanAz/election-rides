using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Email;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.Auth;

/// <summary>Creates set-password links and emails them. Used for new users and for the first system admin.</summary>
public class Invitations(AppDbContext db, IEmailSender mail, IConfiguration config)
{
    public const int ValidHours = 72;

    public async Task SendAsync(AppUser user, CancellationToken ct = default)
    {
        // An older link stops working once a new one is sent.
        await db.PasswordTokens.Where(t => t.UserId == user.Id && !t.Used)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Used, true), ct);

        var token = Secrets.NewToken();
        db.PasswordTokens.Add(new PasswordToken
        {
            UserId = user.Id,
            TokenHash = Secrets.Hash(token),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(ValidHours),
        });
        var baseUrl = (config["App:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
        var link = $"{baseUrl}/set-password?token={token}";
        await mail.SendAsync(new EmailMessage(user.Email, "הזמנה למערכת מטה רכב",
            $"שלום {user.FullName},\n\nהוזמנת למערכת מטה רכב. כדי לקבוע סיסמה, יש להיכנס לקישור:\n{link}\n\nהקישור תקף ל-{ValidHours} שעות."), ct);
    }

    /// <summary>
    /// First run only: when there is no system admin and Bootstrap:AdminEmail is set,
    /// creates one and sends the invitation (in development the link appears in the log).
    /// </summary>
    public static async Task BootstrapAsync(IServiceProvider sp)
    {
        using var scope = sp.CreateScope();
        var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var email = config["Bootstrap:AdminEmail"]?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email)) return;

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (await db.Users.AnyAsync(u => u.Role == UserRole.SystemAdmin)) return;

        var admin = new AppUser { Email = email, FullName = config["Bootstrap:AdminName"] ?? "מנהל מערכת", Role = UserRole.SystemAdmin };
        db.Users.Add(admin);
        Audit.Add(db, null, "user.bootstrap", $"מנהל מערכת ראשון: {email}");
        await scope.ServiceProvider.GetRequiredService<Invitations>().SendAsync(admin);
        await db.SaveChangesAsync();
    }
}
