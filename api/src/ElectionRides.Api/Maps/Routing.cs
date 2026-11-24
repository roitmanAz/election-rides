using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ElectionRides.Api.Common;

namespace ElectionRides.Api.Maps;

/// <summary>
/// Driving routes along the roads, for drawing the selected proposal on the dispatcher's map (spec 10).
/// Uses an OSRM server whose address is in Routing:BaseUrl: the public demo server in development,
/// our own server for election day. Points are rounded to about 10 meters before they leave our server,
/// routes are kept for an hour, and when no server is set or it does not answer the map draws a straight line.
/// </summary>
public static class Routing
{
    record Cached(DateTimeOffset At, object Result);
    static readonly ConcurrentDictionary<string, Cached> Cache = new();

    public static void MapRouting(this WebApplication app)
    {
        app.MapGet("/api/route", async (string points, IConfiguration config, IHttpClientFactory http, ILoggerFactory logs, CancellationToken ct) =>
        {
            var baseUrl = config["Routing:BaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl)) return Results.NoContent();

            var pts = new List<(double lat, double lon)>();
            foreach (var p in points.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var xy = p.Split(',');
                if (xy.Length != 2 || !double.TryParse(xy[0], CultureInfo.InvariantCulture, out var lat) || !double.TryParse(xy[1], CultureInfo.InvariantCulture, out var lon)
                    || lat is < 29 or > 34 || lon is < 34 or > 36)
                    return Results.BadRequest();
                pts.Add((Math.Round(lat, 4), Math.Round(lon, 4)));
            }
            if (pts.Count is < 2 or > 12) return Results.BadRequest();

            var key = string.Join(';', pts.Select(p => FormattableString.Invariant($"{p.lon},{p.lat}")));
            if (Cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromHours(1)) return Results.Ok(hit.Result);

            try
            {
                using var res = await http.CreateClient("routing").GetAsync($"{baseUrl}/route/v1/driving/{key}?overview=full&geometries=geojson", ct);
                if (!res.IsSuccessStatusCode) return Results.NoContent();
                using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var route = doc.RootElement.GetProperty("routes")[0];
                var line = route.GetProperty("geometry").GetProperty("coordinates").EnumerateArray()
                    .Select(c => new { lat = c[1].GetDouble(), lon = c[0].GetDouble() }).ToList();
                var result = new
                {
                    points = line,
                    km = Math.Round(route.GetProperty("distance").GetDouble() / 1000, 1),
                    minutes = (int)Math.Ceiling(route.GetProperty("duration").GetDouble() / 60),
                };
                if (Cache.Count > 5000) Cache.Clear();
                Cache[key] = new Cached(DateTimeOffset.UtcNow, result);
                return Results.Ok(result);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
            {
                logs.CreateLogger("Routing").LogWarning("Route not available: {Message}", e.Message);
                return Results.NoContent();
            }
        }).RequireAuthorization(Policies.Dispatch);
    }
}
