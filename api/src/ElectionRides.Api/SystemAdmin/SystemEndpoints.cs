using System.Security.Claims;
using ClosedXML.Excel;
using ElectionRides.Api.Auth;
using ElectionRides.Api.Common;
using ElectionRides.Api.Data;
using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.SystemAdmin;

public record AdminBody(string Email, string FullName);
public record ActiveBody(bool Active);
public record SecurityBody(bool RequireEmailCode, int RememberDeviceHours, int IdleTimeoutMinutes, int MaxFailedLogins, int LockMinutes);
public record PartyBody(string? PartyName, string? PrivacyOfficerName, string? PrivacyOfficerEmail, string? LegalAdvisorEmail, int? BaseDeletionDays);

/// <summary>
/// The rest of the system admin screen (spec 3.2): live status with alerts, managing a city, closing an operation,
/// reports, deletion certificates of all cities, the audit log, system admins, security policy and party settings.
/// Counts only; never voter or caller details.
/// </summary>
public static class SystemEndpoints
{
    static readonly TimeZoneInfo Israel = TimeZoneInfo.FindSystemTimeZoneById("Asia/Jerusalem");
    static readonly int[] RememberChoices = [8, 24, 168];
    static readonly int[] IdleChoices = [15, 30, 60, 240];
    static readonly int[] AttemptChoices = [3, 5, 10];
    static readonly int[] LockChoices = [5, 15, 30, 60];

    public static void MapSystem(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(Policies.SystemAdmin);

        // ---------- Live status (the first screen) ----------
        g.MapGet("/live", async (AppDbContext db, CancellationToken ct) =>
        {
            var op = await db.Operations.Include(o => o.Cities).FirstOrDefaultAsync(o => o.IsActive, ct);
            if (op is null) return Results.Ok(new { active = false });
            var now = DateTimeOffset.UtcNow;
            var local = TimeZoneInfo.ConvertTime(now, Israel);
            var dayStart = new DateTimeOffset(local.Date, local.Offset).ToUniversalTime();
            var today = DateOnly.FromDateTime(local.DateTime);
            var ids = op.Cities.Select(c => c.Id).ToList();

            var reqs = await db.Requests.Where(r => ids.Contains(r.CityId) && r.OperationId == op.Id && (r.CreatedAt >= dayStart || r.Status == RequestStatus.Queued))
                .Select(r => new { r.CityId, r.CreatedAt, r.Status, r.TimeMode, r.WindowFrom }).ToListAsync(ct);
            var drivers = await db.Drivers.Where(d => ids.Contains(d.CityId) && d.OperationId == op.Id && d.Status != DriverStatus.Finished)
                .Select(d => new { d.CityId, d.Status }).ToListAsync(ct);
            var admins = await db.Users.Where(u => u.CityId != null && ids.Contains(u.CityId.Value) && u.Role == UserRole.CityAdmin && u.IsActive)
                .Select(u => new { u.CityId, u.LastLoginAt }).ToListAsync(ct);
            var setup = await SetupSteps(db, ids, ct);

            var rows = op.Cities.OrderBy(c => c.Name).Select(c =>
            {
                var queued = reqs.Where(r => r.CityId == c.Id && r.Status == RequestStatus.Queued).ToList();
                var longest = queued.Count == 0 ? 0 : (int)Math.Floor(queued.Max(r => Waiting.Minutes(now, r.CreatedAt, r.TimeMode, r.WindowFrom)));
                var cityAdmins = admins.Where(a => a.CityId == c.Id).ToList();
                return new
                {
                    c.Id, c.Name,
                    requestsToday = reqs.Count(r => r.CityId == c.Id && r.CreatedAt >= dayStart),
                    queued = queued.Count, longestWait = longest,
                    freeDrivers = drivers.Count(d => d.CityId == c.Id && d.Status is DriverStatus.Free or DriverStatus.Soon),
                    onTrip = drivers.Count(d => d.CityId == c.Id && d.Status == DriverStatus.OnTrip),
                    hasAdmin = cityAdmins.Count > 0,
                    adminLastLogin = cityAdmins.Max(a => a.LastLoginAt),
                    setupDone = setup.GetValueOrDefault(c.Id),
                    electionDay = c.ElectionDayOverride ?? op.ElectionDay,
                };
            }).ToList();

            // Alerts (spec 3.2).
            var alerts = new List<object>();
            foreach (var r in rows)
            {
                if (!r.hasAdmin) alerts.Add(new { level = "bad", city = r.Name, text = "אין מנהל עיר" });
                if (r.longestWait >= 30) alerts.Add(new { level = "bad", city = r.Name, text = $"המתנה של {r.longestWait} דקות" });
                if (r.freeDrivers == 0 && r.queued >= 10) alerts.Add(new { level = "bad", city = r.Name, text = $"אין נהגים פנויים, ו-{r.queued} ממתינים" });
                var daysLeft = r.electionDay.DayNumber - today.DayNumber;
                if (r.setupDone < 6 && daysLeft is >= 0 and <= 14) alerts.Add(new { level = "warn", city = r.Name, text = $"ההקמה לא הושלמה ({r.setupDone} מתוך 6), ונשארו {daysLeft} ימים לבחירות" });
            }
            var top = rows.OrderByDescending(r => r.longestWait).FirstOrDefault();
            return Results.Ok(new
            {
                active = true,
                operation = new { type = op.Type.ToString(), op.ElectionDay },
                totals = new
                {
                    cities = rows.Count, requestsToday = rows.Sum(r => r.requestsToday), queued = rows.Sum(r => r.queued),
                    longestWait = top?.longestWait ?? 0, longestWaitCity = top is { longestWait: > 0 } ? top.Name : null,
                    freeDrivers = rows.Sum(r => r.freeDrivers), onTrip = rows.Sum(r => r.onTrip),
                },
                alerts, cities = rows,
            });
        });

        // ---------- Managing a city ----------
        g.MapPost("/cities/{id:guid}/admins", async (Guid id, AdminBody b, ClaimsPrincipal me, AppDbContext db, Invitations inv, CancellationToken ct) =>
        {
            var city = await db.Cities.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (city is null) return Results.NotFound();
            var (email, name, error) = await CheckNewUser(db, b, ct);
            if (error is not null) return error;
            var user = new AppUser { Email = email, FullName = name, Role = UserRole.CityAdmin, CityId = id };
            db.Users.Add(user);
            Audit.Add(db, await Actor(db, me, ct), "city.admin-add", $"{city.Name}: {name} ({email})", id);
            await inv.SendAsync(user, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { user.Id });
        });

        g.MapDelete("/cities/{id:guid}/admins/{userId:guid}", async (Guid id, Guid userId, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId && x.CityId == id && x.Role == UserRole.CityAdmin && x.IsActive, ct);
            if (u is null) return Results.NotFound();
            u.IsActive = false;
            Audit.Add(db, await Actor(db, me, ct), "city.admin-remove", $"{u.FullName} ({u.Email})", id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // A new invitation for a city admin who lost the link; the older link stops working.
        g.MapPost("/cities/{id:guid}/admins/{userId:guid}/invite", async (Guid id, Guid userId, ClaimsPrincipal me, AppDbContext db, Invitations inv, CancellationToken ct) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == userId && x.CityId == id && x.Role == UserRole.CityAdmin && x.IsActive, ct);
            if (u is null) return Results.NotFound();
            await inv.SendAsync(u, ct);
            Audit.Add(db, await Actor(db, me, ct), "city.admin-invite", $"{u.FullName} ({u.Email})", id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/cities/{id:guid}/admins", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.Users.Where(u => u.CityId == id && u.Role == UserRole.CityAdmin && u.IsActive)
                .Select(u => new { u.Id, u.FullName, u.Email, u.LastLoginAt, invited = u.PasswordHash == null }).ToListAsync(ct));

        // Disabling keeps the data; the city's users cannot sign in.
        g.MapPost("/cities/{id:guid}/active", async (Guid id, ActiveBody b, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var city = await db.Cities.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (city is null) return Results.NotFound();
            city.IsActive = b.Active;
            Audit.Add(db, await Actor(db, me, ct), b.Active ? "city.enable" : "city.disable", city.Name, id);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---------- Closing an operation ----------
        g.MapPost("/operations/{id:guid}/close", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var op = await db.Operations.FirstOrDefaultAsync(o => o.Id == id && o.IsActive, ct);
            if (op is null) return Problems.Error("המבצע כבר סגור", 409);
            op.IsActive = false;
            op.ClosedAt = DateTimeOffset.UtcNow;
            var party = await db.PartySettings.SingleAsync(ct);
            Audit.Add(db, await Actor(db, me, ct), "operation.close",
                party.BaseDeletionDays is { } d ? $"הבייסים יימחקו {d} ימים אחרי יום הבחירות" : "לא נקבע מספר ימים למחיקת הבייסים");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---------- Reports ----------
        g.MapGet("/reports/{operationId:guid}", async (Guid operationId, AppDbContext db, CancellationToken ct) =>
            await Report(db, operationId, ct) is { } r ? Results.Ok(r) : Results.NotFound());

        g.MapGet("/reports/{operationId:guid}/excel", async (Guid operationId, AppDbContext db, CancellationToken ct) =>
        {
            var r = await Report(db, operationId, ct);
            if (r is null) return Results.NotFound();
            using var book = new XLWorkbook();
            var sheet = book.Worksheets.Add("דוח");
            sheet.RightToLeft = true;
            string[] head = ["עיר", "פניות", "אחוז שהגיעו לקלפי", "המתנה ממוצעת (דקות)", "המתנה ארוכה (דקות)", "ביטולים", "שעת השיא", "שינוי בהמתנה הממוצעת"];
            for (var i = 0; i < head.Length; i++) sheet.Cell(1, i + 1).Value = head[i];
            var row = 2;
            foreach (var c in r.Cities)
            {
                sheet.Cell(row, 1).Value = c.City;
                sheet.Cell(row, 2).Value = c.Requests;
                sheet.Cell(row, 3).Value = c.ArrivedPercent;
                sheet.Cell(row, 4).Value = c.AverageWait;
                sheet.Cell(row, 5).Value = c.LongestWait;
                sheet.Cell(row, 6).Value = c.Cancelled;
                sheet.Cell(row, 7).Value = c.PeakHour ?? "";
                if (c.WaitChange is { } w) sheet.Cell(row, 8).Value = w;
                row++;
            }
            sheet.Row(1).Style.Font.Bold = true;
            sheet.Columns().AdjustToContents();
            using var ms = new MemoryStream();
            book.SaveAs(ms);
            return Results.File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"report-{r.ElectionDay:yyyy-MM-dd}.xlsx");
        });

        // ---------- Deletion certificates of all cities ----------
        g.MapGet("/certificates", async (AppDbContext db, CancellationToken ct) =>
            await db.DeletionCertificates.OrderByDescending(c => c.Number)
                .Select(c => new { c.Id, c.Number, c.CityName, c.FileName, c.VoterCount, c.DeletedAt, c.DeletedBy }).ToListAsync(ct));
        g.MapGet("/certificates/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
            await db.DeletionCertificates.FirstOrDefaultAsync(c => c.Id == id, ct) is { } c ? Results.Ok(c) : Results.NotFound());

        // ---------- Audit log: search and filter by city; nothing can be deleted ----------
        // Search covers what the screen shows: the Hebrew action name, the details, the email and the city.
        // 100 rows at a time, newest first; "before" continues from the last row shown.
        g.MapGet("/audit", async (Guid? cityId, string? q, long? before, AppDbContext db, CancellationToken ct) =>
        {
            const int page = 100;
            var query = db.Audit.AsQueryable();
            if (cityId is { } c) query = query.Where(a => a.CityId == c);
            if (before is { } b) query = query.Where(a => a.Id < b);
            var cities = await db.Cities.ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            var text = Rules.NormalizeName(q);
            if (text.Length > 0)
            {
                var lower = text.ToLowerInvariant();
                var like = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                var actions = ActionNames.Where(n => n.Key.Contains(lower) || n.Value.Contains(text)).Select(n => n.Key).ToList();
                var cityIds = cities.Where(x => x.Value.Contains(text)).Select(x => (Guid?)x.Key).ToList();
                query = query.Where(a => actions.Contains(a.Action) || cityIds.Contains(a.CityId)
                    || (a.Details != null && EF.Functions.ILike(a.Details, like)) || (a.UserEmail != null && EF.Functions.ILike(a.UserEmail, like)));
            }
            var rows = await query.OrderByDescending(a => a.Id).Take(page + 1).ToListAsync(ct);
            return Results.Ok(new
            {
                rows = rows.Take(page).Select(a => new
                {
                    a.Id, a.At, a.UserEmail, city = a.CityId is { } id ? cities.GetValueOrDefault(id) : null,
                    action = ActionNames.GetValueOrDefault(a.Action, a.Action), a.Details,
                }),
                more = rows.Count > page,
            });
        });

        // ---------- System admins: you cannot remove yourself, and one always remains ----------
        g.MapGet("/sysadmins", async (AppDbContext db, CancellationToken ct) =>
            await db.Users.Where(u => u.Role == UserRole.SystemAdmin && u.IsActive).OrderBy(u => u.FullName)
                .Select(u => new { u.Id, u.FullName, u.Email, u.LastLoginAt, invited = u.PasswordHash == null }).ToListAsync(ct));

        g.MapPost("/sysadmins", async (AdminBody b, ClaimsPrincipal me, AppDbContext db, Invitations inv, CancellationToken ct) =>
        {
            var (email, name, error) = await CheckNewUser(db, b, ct);
            if (error is not null) return error;
            var user = new AppUser { Email = email, FullName = name, Role = UserRole.SystemAdmin };
            db.Users.Add(user);
            Audit.Add(db, await Actor(db, me, ct), "sysadmin.add", $"{name} ({email})");
            await inv.SendAsync(user, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { user.Id });
        });

        g.MapDelete("/sysadmins/{id:guid}", async (Guid id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            if (id == me.UserId()) return Problems.Error("אי אפשר להסיר את עצמך");
            if (await db.Users.CountAsync(u => u.Role == UserRole.SystemAdmin && u.IsActive, ct) <= 1)
                return Problems.Error("תמיד נשאר לפחות מנהל מערכת אחד", 409);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id && x.Role == UserRole.SystemAdmin && x.IsActive, ct);
            if (u is null) return Results.NotFound();
            u.IsActive = false;
            Audit.Add(db, await Actor(db, me, ct), "sysadmin.remove", $"{u.FullName} ({u.Email})");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // ---------- Security policy and party settings ----------
        g.MapGet("/security", async (AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.PartySettings.SingleAsync(ct);
            return Results.Ok(new SecurityBody(p.RequireEmailCode, p.RememberDeviceHours, p.IdleTimeoutMinutes, p.MaxFailedLogins, p.LockMinutes));
        });

        g.MapPut("/security", async (SecurityBody b, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            if (!RememberChoices.Contains(b.RememberDeviceHours) || !IdleChoices.Contains(b.IdleTimeoutMinutes)
                || !AttemptChoices.Contains(b.MaxFailedLogins) || !LockChoices.Contains(b.LockMinutes))
                return Problems.Error("צריך לבחור מהאפשרויות שברשימה");
            var p = await db.PartySettings.SingleAsync(ct);
            p.RequireEmailCode = b.RequireEmailCode;
            p.RememberDeviceHours = b.RememberDeviceHours;
            p.IdleTimeoutMinutes = b.IdleTimeoutMinutes;
            p.MaxFailedLogins = b.MaxFailedLogins;
            p.LockMinutes = b.LockMinutes;
            Audit.Add(db, await Actor(db, me, ct), "security.policy",
                $"קוד במייל: {(b.RequireEmailCode ? "כן" : "לא")}, מכשיר נזכר {b.RememberDeviceHours} שעות, ניתוק אחרי {b.IdleTimeoutMinutes} דקות, נעילה אחרי {b.MaxFailedLogins} ניסיונות ל-{b.LockMinutes} דקות");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        g.MapGet("/party", async (AppDbContext db, CancellationToken ct) =>
        {
            var p = await db.PartySettings.SingleAsync(ct);
            var op = await db.Operations.FirstOrDefaultAsync(o => o.IsActive, ct);
            return Results.Ok(new
            {
                p.PartyName, p.PrivacyOfficerName, p.PrivacyOfficerEmail, p.LegalAdvisorEmail, p.BaseDeletionDays,
                deletionDate = op is not null && p.BaseDeletionDays is { } d ? op.ElectionDay.AddDays(d) : (DateOnly?)null,
            });
        });

        g.MapPut("/party", async (PartyBody b, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            static bool BadEmail(string? e) => !string.IsNullOrWhiteSpace(e) && (!System.Net.Mail.MailAddress.TryCreate(e.Trim(), out _) || !e.Contains('.'));
            if (BadEmail(b.PrivacyOfficerEmail) || BadEmail(b.LegalAdvisorEmail)) return Problems.Error("אחד האימיילים לא תקין");
            if (b.BaseDeletionDays is < 1 or > 365) return Problems.Error("מספר הימים צריך להיות בין 1 ל-365");
            var p = await db.PartySettings.SingleAsync(ct);
            p.PartyName = Clean(b.PartyName);
            p.PrivacyOfficerName = Clean(b.PrivacyOfficerName);
            p.PrivacyOfficerEmail = Clean(b.PrivacyOfficerEmail)?.ToLowerInvariant();
            p.LegalAdvisorEmail = Clean(b.LegalAdvisorEmail)?.ToLowerInvariant();
            p.BaseDeletionDays = b.BaseDeletionDays;
            Audit.Add(db, await Actor(db, me, ct), "party.settings",
                $"ממונה פרטיות: {p.PrivacyOfficerEmail ?? "-"}, מחיקת בייסים: {(p.BaseDeletionDays is { } d ? d + " ימים אחרי הבחירות" : "לא נקבע")}");
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>The Hebrew name of each audit action, as shown and searched in the audit log.</summary>
    public static readonly Dictionary<string, string> ActionNames = new()
    {
        ["user.login"] = "כניסה", ["user.locked"] = "נעילה", ["user.password-set"] = "קביעת סיסמה", ["user.add"] = "הוספת משתמש",
        ["user.remove"] = "הסרת משתמש", ["user.unlock"] = "שחרור נעילה", ["user.roles"] = "שינוי תפקידים", ["user.bootstrap"] = "מנהל מערכת ראשון",
        ["city.add"] = "פתיחת עיר", ["city.settings"] = "הגדרות עיר", ["city.admin-add"] = "הוספת מנהל עיר", ["city.admin-remove"] = "הסרת מנהל עיר",
        ["city.admin-invite"] = "הזמנה חוזרת למנהל עיר", ["city.enable"] = "הפעלת עיר", ["city.disable"] = "השבתת עיר",
        ["operation.open"] = "פתיחת מבצע", ["operation.close"] = "סגירת מבצע", ["operation.cities"] = "שינוי ערים במבצע",
        ["streets.import"] = "ייבוא רחובות", ["base.import"] = "טעינת בייס", ["base.delete"] = "מחיקת בייס", ["request.cancel"] = "ביטול פנייה",
        ["sysadmin.add"] = "הוספת מנהל מערכת", ["sysadmin.remove"] = "הסרת מנהל מערכת", ["catalog.add"] = "קהילה חדשה ברשימה הכללית", ["catalog.rename"] = "שינוי שם קהילה", ["catalog.rename-request"] = "בקשה לשינוי שם קהילה",
        ["catalog.rename-reject"] = "דחיית שינוי שם קהילה", ["catalog.delete"] = "מחיקת קהילה מהרשימה הכללית", ["security.policy"] = "מדיניות אבטחה", ["party.settings"] = "פרטיות והגדרות",
    };

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : Rules.NormalizeName(s);

    static async Task<AppUser> Actor(AppDbContext db, ClaimsPrincipal me, CancellationToken ct) => await db.Users.FirstAsync(u => u.Id == me.UserId(), ct);

    static async Task<(string email, string name, IResult? error)> CheckNewUser(AppDbContext db, AdminBody b, CancellationToken ct)
    {
        var email = Rules.NormalizeEmail(b.Email);
        var name = Rules.NormalizeName(b.FullName);
        if (name.Length == 0) return (email, name, Problems.Error("צריך למלא שם"));
        if (!System.Net.Mail.MailAddress.TryCreate(email, out _) || !email.Contains('.')) return (email, name, Problems.Error("האימייל לא תקין"));
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) return (email, name, Problems.Error("האימייל כבר רשום במערכת", 409));
        return (email, name, null);
    }

    /// <summary>How many of the six setup steps each city finished (spec 3.2).</summary>
    public static async Task<Dictionary<Guid, int>> SetupSteps(AppDbContext db, List<Guid> ids, CancellationToken ct)
    {
        var clusters = (await db.Clusters.Where(c => ids.Contains(c.CityId)).Select(c => c.CityId).Distinct().ToListAsync(ct)).ToHashSet();
        var streets = (await db.Streets.Where(c => ids.Contains(c.CityId)).Select(c => c.CityId).Distinct().ToListAsync(ct)).ToHashSet();
        var comms = (await db.Communities.Where(c => ids.Contains(c.CityId)).Select(c => c.CityId).Distinct().ToListAsync(ct)).ToHashSet();
        var bases = (await db.VoterBases.Where(c => ids.Contains(c.CityId)).Select(c => c.CityId).ToListAsync(ct)).ToHashSet();
        var staff = (await db.Users.Where(u => u.CityId != null && ids.Contains(u.CityId.Value) && u.IsActive && (u.Role != UserRole.CityAdmin || u.ExtraRoles.Count > 0))
            .Select(u => u.CityId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var phones = (await db.Cities.Where(c => ids.Contains(c.Id) && c.HqPhone != null).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        return ids.ToDictionary(id => id, id => new[] { clusters, streets, comms, bases, staff, phones }.Count(s => s.Contains(id)));
    }

    public record CityReport(string City, int Requests, int ArrivedPercent, int AverageWait, int LongestWait, int Cancelled, string? PeakHour, int? WaitChange);
    public record OperationReport(Guid Id, string Type, DateOnly ElectionDay, List<CityReport> Cities);

    /// <summary>Per city: requests, arrived %, wait (request to driver), cancellations, peak hour, and the change against the previous operation.</summary>
    static async Task<OperationReport?> Report(AppDbContext db, Guid operationId, CancellationToken ct)
    {
        var op = await db.Operations.Include(o => o.Cities).FirstOrDefaultAsync(o => o.Id == operationId, ct);
        if (op is null) return null;
        var prev = await db.Operations.Where(o => o.ElectionDay < op.ElectionDay).OrderByDescending(o => o.ElectionDay).FirstOrDefaultAsync(ct);

        // "created" here is when the request started waiting (the window start for a pickup window).
        async Task<Dictionary<Guid, List<(DateTimeOffset created, DateTimeOffset? assigned, RequestStatus status)>>> Load(Guid opId) =>
            (await db.Requests.Where(r => r.OperationId == opId).Select(r => new { r.CityId, r.CreatedAt, r.AssignedAt, r.Status, r.TimeMode, r.WindowFrom }).ToListAsync(ct))
                .GroupBy(r => r.CityId).ToDictionary(g => g.Key, g => g.Select(r => (Waiting.Since(r.CreatedAt, r.TimeMode, r.WindowFrom), r.AssignedAt, r.Status)).ToList());

        var now = await Load(op.Id);
        var before = prev is null ? new() : await Load(prev.Id);
        static double? AvgWait(List<(DateTimeOffset created, DateTimeOffset? assigned, RequestStatus status)> rs)
        {
            var waits = rs.Where(r => r.assigned is not null).Select(r => Math.Max(0, (r.assigned!.Value - r.created).TotalMinutes)).ToList();
            return waits.Count == 0 ? null : waits.Average();
        }

        var cities = op.Cities.OrderBy(c => c.Name).Select(c =>
        {
            var rs = now.GetValueOrDefault(c.Id) ?? [];
            var avg = AvgWait(rs);
            var prevAvg = before.TryGetValue(c.Id, out var p) ? AvgWait(p) : null;
            var counted = rs.Count(r => r.status != RequestStatus.Cancelled);
            var arrived = rs.Count(r => r.status is RequestStatus.Arrived or RequestStatus.Done);
            var peak = rs.GroupBy(r => TimeZoneInfo.ConvertTime(r.created, Israel).Hour).OrderByDescending(g => g.Count()).FirstOrDefault();
            return new CityReport(
                c.Name, rs.Count, counted == 0 ? 0 : (int)Math.Round(100.0 * arrived / counted),
                avg is null ? 0 : (int)Math.Round(avg.Value),
                rs.Where(r => r.assigned is not null).Select(r => (int)Math.Max(0, (r.assigned!.Value - r.created).TotalMinutes)).DefaultIfEmpty(0).Max(),
                rs.Count(r => r.status == RequestStatus.Cancelled),
                peak is null ? null : $"{peak.Key:00}:00",
                avg is not null && prevAvg is not null ? (int)Math.Round(avg.Value - prevAvg.Value) : null);
        }).ToList();
        return new OperationReport(op.Id, op.Type.ToString(), op.ElectionDay, cities);
    }
}
