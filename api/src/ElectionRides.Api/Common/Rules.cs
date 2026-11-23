using System.Security.Claims;
using System.Text.RegularExpressions;
using ElectionRides.Api.Auth;

namespace ElectionRides.Api.Common;

public static partial class Rules
{
    /// <summary>
    /// Israeli phone numbers (spec 5.1): mobile and 07x numbers have 10 digits,
    /// landlines with area codes 02, 03, 04, 08 and 09 have 9 digits.
    /// </summary>
    public static bool IsValidPhone(string? phone)
    {
        var d = DigitsOnly(phone);
        if (d.Length == 10 && (d.StartsWith("05") || d.StartsWith("07"))) return true;
        return d.Length == 9 && d[0] == '0' && "23489".Contains(d[1]);
    }

    public static string DigitsOnly(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Israeli ID number: up to 9 digits (padded with leading zeros) with a check digit.</summary>
    public static bool IsValidIdNumber(string? id)
    {
        var d = DigitsOnly(id);
        if (d.Length is 0 or > 9 || d != (id ?? "").Trim()) return false;
        d = d.PadLeft(9, '0');
        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var n = (d[i] - '0') * (i % 2 == 0 ? 1 : 2);
            sum += n > 9 ? n - 9 : n;
        }
        return sum % 10 == 0 && d != "000000000";
    }

    public static string NormalizeId(string id) => DigitsOnly(id).PadLeft(9, '0');

    /// <summary>Closed lists from the operator prototype. "אחר" always needs a short text.</summary>
    public static readonly string[] UrgencyReasons = ["יציאה מהעיר", "טיפול רפואי", "עבודה", "אחר"];
    public static readonly string[] CancelReasons = ["הפונה ביטל", "מצא הסעה אחרת", "פנייה כפולה", "אחר"];

    /// <summary>
    /// Ballot boxes as typed by the city admin: single numbers and ranges, separated by commas,
    /// e.g. "31-35" or "10, 12, 14–16" or "12.1". Returns null when the text cannot be read.
    /// </summary>
    public static List<string>? ParseBoxes(string? text)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = raw.Replace('–', '-').Replace('—', '-').Replace(" ", "");
            var range = RangePattern().Match(part);
            if (range.Success)
            {
                int from = int.Parse(range.Groups[1].Value), to = int.Parse(range.Groups[2].Value);
                if (to < from || to - from > 200) return null;
                for (var n = from; n <= to; n++) result.Add(n.ToString());
            }
            else if (SinglePattern().IsMatch(part)) result.Add(part);
            else return null;
        }
        return result.Count == 0 ? null : result.Distinct().ToList();
    }

    [GeneratedRegex(@"^(\d{1,4})-(\d{1,4})$")]
    private static partial Regex RangePattern();

    [GeneratedRegex(@"^\d{1,4}(\.\d{1,2})?$")]
    private static partial Regex SinglePattern();

    public static string NormalizeName(string? s) => string.Join(' ', (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
}

public static class Me
{
    public static Guid UserId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>The signed-in user's city. Endpoints under /api/city are only open to users who have one.</summary>
    public static Guid CityId(this ClaimsPrincipal p) => Guid.Parse(p.FindFirstValue(SessionPolicy.CityClaim)!);
}

public static class Policies
{
    public const string CityStaff = "city-staff";
    public const string CityAdmin = "city-admin";
    public const string SystemAdmin = "system-admin";
    /// <summary>The dispatcher, and the city admin who handles exceptions.</summary>
    public const string Dispatch = "dispatch";
    /// <summary>A driver signed in with the personal link (driver app).</summary>
    public const string Driver = "driver";
}

public static class Problems
{
    public static IResult Error(string message, int status = 400) => Results.Json(new { error = message }, statusCode: status);
}
