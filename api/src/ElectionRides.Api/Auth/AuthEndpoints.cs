using ElectionRides.Api.Common;
using System.Security.Claims;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Email;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.Auth;

public record LoginRequest(string Email, string Password);
public record VerifyRequest(Guid ChallengeId, string Code);
public record ResendRequest(Guid ChallengeId);
public record SetPasswordRequest(string Token, string Password);
public record MeResponse(Guid Id, string FullName, string Email, string Role, List<string> Roles, Guid? CityId, string? CityName);

public static class AuthEndpoints
{
    public const string Scheme = "hq";
    public const string DeviceCookie = "er_device";
    public const int MinPasswordLength = 8;
    const int CodeMinutes = 10;
    const int MaxCodeAttempts = 5;

    static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
    static readonly PasswordHasher<AppUser> Hasher = new();
    // Used to spend the same time when the email is unknown, so timing does not reveal which emails exist.
    static readonly string DummyHash = Hasher.HashPassword(null!, "not-a-real-password");

    static IResult Error(string message, int status) => Results.Json(new { error = message }, statusCode: status);
    const string WrongCredentials = "האימייל או הסיסמה שגויים";

    public static void MapAuth(this WebApplication app)
    {
        var g = app.MapGroup("/api/auth").RequireRateLimiting("auth");

        g.MapPost("/login", async (LoginRequest req, AppDbContext db, IEmailSender mail, HttpContext http, CancellationToken ct) =>
        {
            var email = Rules.NormalizeEmail(req.Email);
            var user = await db.Users.Include(u => u.City).FirstOrDefaultAsync(u => u.Email == email && u.IsActive, ct);
            if (user?.PasswordHash is null)
            {
                Hasher.VerifyHashedPassword(null!, DummyHash, req.Password ?? "");
                return Error(WrongCredentials, 401);
            }

            var now = DateTimeOffset.UtcNow;
            if (user.LockedUntil > now)
                return Error($"החשבון נעול עד {TimeZoneInfo.ConvertTime(user.LockedUntil.Value, Israel):HH:mm} בגלל ניסיונות כניסה שגויים. מנהל העיר יכול לשחרר מוקדם.", 423);
            if (user.City is { IsActive: false })
                return Error("העיר מושבתת. אפשר לפנות למנהל המערכת.", 403);

            var policy = await db.PartySettings.SingleAsync(ct);
            if (Hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password ?? "") == PasswordVerificationResult.Failed)
            {
                user.FailedLogins++;
                if (user.FailedLogins >= policy.MaxFailedLogins)
                {
                    user.LockedUntil = now.AddMinutes(policy.LockMinutes);
                    user.FailedLogins = 0;
                    Audit.Add(db, user, "user.locked", $"נעילה ל-{policy.LockMinutes} דקות אחרי {policy.MaxFailedLogins} ניסיונות שגויים");
                }
                await db.SaveChangesAsync(ct);
                return Error(WrongCredentials, 401);
            }
            user.FailedLogins = 0;
            user.LockedUntil = null;

            if (!policy.RequireEmailCode || await IsTrustedDevice(db, user, http, ct))
            {
                await SignIn(http, db, user, policy, ct);
                return Results.Ok(new { status = "signed-in", user = Me(user) });
            }

            var challenge = await SendCode(db, mail, user, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { status = "code-sent", challengeId = challenge.Id, email = MaskEmail(user.Email) });
        });

        g.MapPost("/verify", async (VerifyRequest req, AppDbContext db, HttpContext http, CancellationToken ct) =>
        {
            var ch = await db.LoginChallenges.FirstOrDefaultAsync(c => c.Id == req.ChallengeId, ct);
            var now = DateTimeOffset.UtcNow;
            if (ch is null || ch.Used || ch.ExpiresAt < now || ch.Attempts >= MaxCodeAttempts)
                return Error("הקוד פג תוקף. אפשר לבקש קוד חדש.", 410);

            if (!Secrets.FixedTimeEquals(ch.CodeHash, Secrets.HashCode(ch.Id, (req.Code ?? "").Trim())))
            {
                ch.Attempts++;
                await db.SaveChangesAsync(ct);
                return ch.Attempts >= MaxCodeAttempts
                    ? Error("הקוד הוזן שגוי יותר מדי פעמים. אפשר לבקש קוד חדש.", 410)
                    : Error("הקוד שגוי", 400);
            }

            var user = await db.Users.Include(u => u.City).FirstAsync(u => u.Id == ch.UserId, ct);
            if (!user.IsActive || user.City is { IsActive: false }) return Error(WrongCredentials, 401);
            ch.Used = true;

            var policy = await db.PartySettings.SingleAsync(ct);
            var token = Secrets.NewToken();
            var expires = now.AddHours(policy.RememberDeviceHours);
            db.TrustedDevices.Add(new TrustedDevice { UserId = user.Id, TokenHash = Secrets.Hash(token), ExpiresAt = expires });
            http.Response.Cookies.Append(DeviceCookie, token, new CookieOptions
            {
                HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Strict, Expires = expires, Path = "/api/auth",
            });

            await SignIn(http, db, user, policy, ct);
            return Results.Ok(new { status = "signed-in", user = Me(user) });
        });

        g.MapPost("/resend", async (ResendRequest req, AppDbContext db, IEmailSender mail, CancellationToken ct) =>
        {
            var old = await db.LoginChallenges.FirstOrDefaultAsync(c => c.Id == req.ChallengeId && !c.Used, ct);
            if (old is null || old.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(-30))
                return Error("צריך להתחבר מחדש עם אימייל וסיסמה.", 410);
            old.Used = true;
            var user = await db.Users.FirstAsync(u => u.Id == old.UserId, ct);
            var ch = await SendCode(db, mail, user, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { status = "code-sent", challengeId = ch.Id, email = MaskEmail(user.Email) });
        });

        g.MapPost("/set-password", async (SetPasswordRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var hash = Secrets.Hash(req.Token ?? "");
            var t = await db.PasswordTokens.FirstOrDefaultAsync(x => x.TokenHash == hash, ct);
            if (t is null || t.Used || t.ExpiresAt < DateTimeOffset.UtcNow)
                return Error("הקישור לא בתוקף. אפשר לבקש ממנהל העיר הזמנה חדשה.", 410);
            if ((req.Password ?? "").Length < MinPasswordLength)
                return Error($"הסיסמה צריכה להיות באורך {MinPasswordLength} תווים לפחות", 400);

            var user = await db.Users.FirstAsync(u => u.Id == t.UserId, ct);
            user.PasswordHash = Hasher.HashPassword(user, req.Password!);
            user.FailedLogins = 0;
            user.LockedUntil = null;
            t.Used = true;
            Audit.Add(db, user, "user.password-set");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { email = user.Email });
        });

        g.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(Scheme);
            return Results.NoContent();
        });

        app.MapGet("/api/auth/me", async (ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var id = Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await db.Users.Include(u => u.City).FirstAsync(u => u.Id == id, ct);
            return Results.Ok(Me(user));
        }).RequireAuthorization();
    }

    static MeResponse Me(AppUser u) => new(u.Id, u.FullName, u.Email, u.Role.ToString(), u.AllRoles().Select(r => r.ToString()).ToList(), u.CityId, u.City?.Name);

    static async Task<bool> IsTrustedDevice(AppDbContext db, AppUser user, HttpContext http, CancellationToken ct)
    {
        if (!http.Request.Cookies.TryGetValue(DeviceCookie, out var token) || string.IsNullOrEmpty(token)) return false;
        var hash = Secrets.Hash(token);
        return await db.TrustedDevices.AnyAsync(d => d.TokenHash == hash && d.UserId == user.Id && d.ExpiresAt > DateTimeOffset.UtcNow, ct);
    }

    static async Task<LoginChallenge> SendCode(AppDbContext db, IEmailSender mail, AppUser user, CancellationToken ct)
    {
        var ch = new LoginChallenge { UserId = user.Id, CodeHash = "", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(CodeMinutes) };
        var code = Secrets.NewCode();
        ch.CodeHash = Secrets.HashCode(ch.Id, code);
        db.LoginChallenges.Add(ch);
        await mail.SendAsync(new EmailMessage(user.Email, $"קוד כניסה למטה רכב: {code}",
            $"שלום {user.FullName},\n\nקוד הכניסה שלך: {code}\nהקוד תקף ל-{CodeMinutes} דקות.\n\nאם לא ניסית להתחבר, אפשר להתעלם מההודעה."), ct);
        return ch;
    }

    static async Task SignIn(HttpContext http, AppDbContext db, AppUser user, PartySettings policy, CancellationToken ct)
    {
        user.LastLoginAt = DateTimeOffset.UtcNow;
        Audit.Add(db, user, "user.login");
        await db.SaveChangesAsync(ct);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.FullName),
            new(ClaimTypes.Email, user.Email),
            // Idle timeout is fixed at sign-in: a policy change applies from the next sign-in (spec 3.2).
            new(SessionPolicy.IdleClaim, policy.IdleTimeoutMinutes.ToString()),
        };
        claims.AddRange(user.AllRoles().Select(r => new Claim(ClaimTypes.Role, r.ToString())));
        if (user.CityId is { } city) claims.Add(new(SessionPolicy.CityClaim, city.ToString()));
        var props = new AuthenticationProperties();
        props.Items[SessionPolicy.LastSeenItem] = DateTimeOffset.UtcNow.ToString("O");
        await http.SignInAsync(Scheme, new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme)), props);
    }

    /// <summary>Shows a hint of the address the code went to, e.g. "ra***@gmail.com".</summary>
    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return email;
        var name = email[..at];
        return (name.Length <= 2 ? name[..1] : name[..2]) + "***" + email[at..];
    }
}
