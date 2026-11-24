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
        var user = await db.Users.Where(u => u.Id == id && u.IsActive && (u.City == null || u.City.IsActive))
            .Select(u => new { u.Role, u.ExtraRoles }).FirstOrDefaultAsync();
        if (user is null)
        {
            await Reject(ctx);
            return;
        }

        // Roles changed by the city admin apply at once, without signing in again.
        var roles = user.ExtraRoles.Prepend(user.Role).Distinct().Select(r => r.ToString()).Order().ToList();
        var had = ctx.Principal.FindAll(ClaimTypes.Role).Select(c => c.Value).Order().ToList();
        if (!roles.SequenceEqual(had))
        {
            var identity = new ClaimsIdentity(ctx.Principal.Claims.Where(c => c.Type != ClaimTypes.Role), ctx.Principal.Identity!.AuthenticationType);
            identity.AddClaims(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            ctx.ReplacePrincipal(new ClaimsPrincipal(identity));
            ctx.ShouldRenew = true;
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
