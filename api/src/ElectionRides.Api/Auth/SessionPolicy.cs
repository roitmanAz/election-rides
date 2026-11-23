using System.Security.Claims;
using ElectionRides.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.Auth;

/// <summary>
/// Checks every request of a signed-in user: signs out after the idle timeout,
/// and when the user was removed or the city was disabled.
/// </summary>
public static class SessionPolicy
{
    public const string IdleClaim = "idle";
    public const string CityClaim = "city";
    public const string LastSeenItem = "last";

    public static async Task Validate(CookieValidatePrincipalContext ctx)
    {
        var now = DateTimeOffset.UtcNow;
        var idle = int.TryParse(ctx.Principal?.FindFirstValue(IdleClaim), out var m) ? m : 30;
        var last = ctx.Properties.Items.TryGetValue(LastSeenItem, out var s) && DateTimeOffset.TryParse(s, out var t) ? t : DateTimeOffset.MinValue;
        if (now - last > TimeSpan.FromMinutes(idle))
        {
            await Reject(ctx);
            return;
        }

        var id = Guid.Parse(ctx.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var db = ctx.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        var ok = await db.Users.AnyAsync(u => u.Id == id && u.IsActive && (u.City == null || u.City.IsActive));
        if (!ok)
        {
            await Reject(ctx);
            return;
        }

        // Refresh "last seen" at most once a minute, so the cookie is not rewritten on every request.
        if (now - last > TimeSpan.FromMinutes(1))
        {
            ctx.Properties.Items[LastSeenItem] = now.ToString("O");
            ctx.ShouldRenew = true;
        }
    }

    static async Task Reject(CookieValidatePrincipalContext ctx)
    {
        ctx.RejectPrincipal();
        await ctx.HttpContext.SignOutAsync(AuthEndpoints.Scheme);
    }
}
