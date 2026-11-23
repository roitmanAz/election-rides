using System.Security.Claims;
using ElectionRides.Api.Auth;
using ElectionRides.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ElectionRides.Api.Dispatch;

/// <summary>
/// Live updates (SignalR inside the server, spec 13): every screen of a city joins the city's group and is
/// told "changed" after any change to the queue, drivers or trips; it then reloads what it shows.
/// </summary>
[Authorize(Policy = Policies.CityStaff)]
public class CityHub : Hub
{
    public static string Group(Guid city) => $"city:{city:N}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(Context.User!.CityId()));
        await base.OnConnectedAsync();
    }
}

public class CityEvents(IHubContext<CityHub> hub)
{
    public Task Changed(Guid city) => hub.Clients.Group(CityHub.Group(city)).SendAsync("changed");
}
