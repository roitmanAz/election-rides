using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Email;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace ElectionRides.Tests;

/// <summary>Creates a city and a signed-in user of a given role, going through the real sign-in with the email code.</summary>
public static class TestUsers
{
    public const string Password = "pass-1234";

    public static async Task<(HttpClient client, City city, AppUser user)> SignedIn(ApiFactory api, UserRole role, City? city = null)
    {
        await using var db = api.NewDb();
        if (city is null)
        {
            city = new City { Name = "עיר " + Guid.NewGuid().ToString("N")[..8] };
            db.Cities.Add(city);
        }
        var user = new AppUser
        {
            Email = $"u{Guid.NewGuid():N}@test.local", FullName = "משתמש בדיקה", Role = role,
            CityId = role == UserRole.SystemAdmin ? null : city.Id,
        };
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(user, Password);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var c = api.CreateClient();
        var login = await (await c.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var mail = api.Services.GetRequiredService<DevEmailSender>().Sent.Last(m => m.To == user.Email);
        var code = Regex.Match(mail.Body, @"(\d{6})").Groups[1].Value;
        (await c.PostAsJsonAsync("/api/auth/verify", new { challengeId = login.GetProperty("challengeId").GetGuid(), code }))
            .EnsureSuccessStatusCode();
        return (c, city, user);
    }
}
