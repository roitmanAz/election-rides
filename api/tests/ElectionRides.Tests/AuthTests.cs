using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using ElectionRides.Api.Auth;
using ElectionRides.Api.Domain;
using ElectionRides.Api.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ElectionRides.Tests;

[Collection("api")]
public class AuthTests(ApiFactory api)
{
    const string Password = "סיסמה-חזקה-123";

    async Task<(AppUser user, City city)> NewInvitedUser(UserRole role = UserRole.Operator)
    {
        await using var db = api.NewDb();
        var city = new City { Name = "עיר " + Guid.NewGuid().ToString("N")[..8] };
        db.Cities.Add(city);
        var user = new AppUser { Email = $"u{Guid.NewGuid():N}@test.local", FullName = "משתמש בדיקה", Role = role, CityId = city.Id };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        using var scope = api.Services.CreateScope();
        var appDb = scope.ServiceProvider.GetRequiredService<ElectionRides.Api.Data.AppDbContext>();
        appDb.Attach(user);
        await scope.ServiceProvider.GetRequiredService<Invitations>().SendAsync(user);
        await appDb.SaveChangesAsync();
        return (user, city);
    }

    string LastMailTo(string email, string pattern) =>
        Regex.Match(api.Services.GetRequiredService<DevEmailSender>().Sent.Last(m => m.To == email).Body, pattern).Groups[1].Value;

    async Task SetPassword(HttpClient c, string email)
    {
        var token = LastMailTo(email, @"token=([A-Za-z0-9_\-]+)");
        var res = await c.PostAsJsonAsync("/api/auth/set-password", new { token, password = Password });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    async Task<HttpClient> SignedInClient(string email)
    {
        var c = api.CreateClient();
        var login = await Json(await c.PostAsJsonAsync("/api/auth/login", new { email, password = Password }));
        Assert.Equal("code-sent", login.GetProperty("status").GetString());
        var code = LastMailTo(email, @"קוד הכניסה שלך: (\d{6})");
        var verify = await c.PostAsJsonAsync("/api/auth/verify", new { challengeId = login.GetProperty("challengeId").GetGuid(), code });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
        return c;
    }

    [Fact]
    public async Task Invited_user_sets_password_signs_in_with_email_code_and_sees_own_details()
    {
        var (user, city) = await NewInvitedUser();
        await SetPassword(api.CreateClient(), user.Email);

        var c = await SignedInClient(user.Email);
        var me = await Json(await c.GetAsync("/api/auth/me"));
        Assert.Equal(user.Email, me.GetProperty("email").GetString());
        Assert.Equal("Operator", me.GetProperty("role").GetString());
        Assert.Equal(city.Name, me.GetProperty("cityName").GetString());
    }

    [Fact]
    public async Task Remembered_device_skips_the_code_on_the_next_sign_in()
    {
        var (user, _) = await NewInvitedUser();
        await SetPassword(api.CreateClient(), user.Email);
        var c = await SignedInClient(user.Email);

        await c.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/auth/me")).StatusCode);

        var again = await Json(await c.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password }));
        Assert.Equal("signed-in", again.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        var (user, _) = await NewInvitedUser();
        var c = api.CreateClient();
        await SetPassword(c, user.Email);

        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await c.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = "wrong-password" })).StatusCode);

        var locked = await c.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password });
        Assert.Equal((HttpStatusCode)423, locked.StatusCode);
    }

    [Fact]
    public async Task Wrong_code_is_refused_and_five_wrong_codes_end_the_challenge()
    {
        var (user, _) = await NewInvitedUser();
        var c = api.CreateClient();
        await SetPassword(c, user.Email);
        var login = await Json(await c.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password }));
        var id = login.GetProperty("challengeId").GetGuid();
        var real = LastMailTo(user.Email, @"קוד הכניסה שלך: (\d{6})");
        var wrong = real == "000000" ? "111111" : "000000";

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/auth/verify", new { challengeId = id, code = wrong })).StatusCode);
        for (var i = 0; i < 4; i++) await c.PostAsJsonAsync("/api/auth/verify", new { challengeId = id, code = wrong });
        Assert.Equal(HttpStatusCode.Gone, (await c.PostAsJsonAsync("/api/auth/verify", new { challengeId = id, code = real })).StatusCode);
    }

    [Fact]
    public async Task Short_password_is_refused()
    {
        var (user, _) = await NewInvitedUser();
        var token = LastMailTo(user.Email, @"token=([A-Za-z0-9_\-]+)");
        var res = await api.CreateClient().PostAsJsonAsync("/api/auth/set-password", new { token, password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Users_of_a_disabled_city_cannot_sign_in()
    {
        var (user, city) = await NewInvitedUser();
        await SetPassword(api.CreateClient(), user.Email);
        await using (var db = api.NewDb())
            await db.Cities.Where(x => x.Id == city.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));

        var res = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_email_gets_the_same_answer_as_a_wrong_password()
    {
        var res = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = "nobody@test.local", password = Password });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("האימייל או הסיסמה שגויים", (await Json(res)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Audit_log_rows_cannot_be_deleted()
    {
        await using var db = api.NewDb();
        db.Audit.Add(new AuditEntry { Action = "test.entry" });
        await db.SaveChangesAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => db.Audit.Where(a => a.Action == "test.entry").ExecuteDeleteAsync());
    }

    [Fact]
    public void Email_hint_hides_most_of_the_address() =>
        Assert.Equal("ra***@gmail.com", AuthEndpoints.MaskEmail("rachel@gmail.com"));
}
