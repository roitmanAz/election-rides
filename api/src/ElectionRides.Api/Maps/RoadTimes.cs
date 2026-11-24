using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using ElectionRides.Api.Dispatch;

namespace ElectionRides.Api.Maps;

/// <summary>
/// Driving times between many points at once, for "regroup by proximity" (spec 7): one request to the OSRM
/// table service (Routing:BaseUrl) for all the points, kept for an hour per pair of points rounded to about
/// 10 meters. Any pair it cannot give falls back to the straight-line estimate.
/// </summary>
public static class RoadTimes
{
    record Cached(DateTimeOffset At, double Minutes);
    static readonly ConcurrentDictionary<string, Cached> Cache = new();
    const int MaxPoints = 100;

    static string Key(GeoPoint p) => FormattableString.Invariant($"{Math.Round(p.Lon, 4)},{Math.Round(p.Lat, 4)}");

    public static async Task<ProposalEngine.TravelMinutes> For(IEnumerable<GeoPoint> points, IConfiguration config,
        IHttpClientFactory http, ILogger log, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var keys = points.Select(Key).Distinct().ToList();
        bool Fresh(string a, string b) => Cache.TryGetValue(a + ";" + b, out var c) && now - c.At < TimeSpan.FromHours(1);
        var baseUrl = config["Routing:BaseUrl"]?.TrimEnd('/');
        var missing = keys.Any(a => keys.Any(b => a != b && !Fresh(a, b)));
        if (!string.IsNullOrEmpty(baseUrl) && missing && keys.Count is > 1 and <= MaxPoints)
        {
            try
            {
                using var res = await http.CreateClient("routing").GetAsync($"{baseUrl}/table/v1/driving/{string.Join(';', keys)}?annotations=duration", ct);
                if (res.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                    var rows = doc.RootElement.GetProperty("durations").EnumerateArray().ToList();
                    if (Cache.Count > 200_000) Cache.Clear();
                    for (var i = 0; i < rows.Count; i++)
                    {
                        var cols = rows[i].EnumerateArray().ToList();
                        for (var j = 0; j < cols.Count; j++)
                            if (cols[j].ValueKind == JsonValueKind.Number)
                                Cache[keys[i] + ";" + keys[j]] = new Cached(now, cols[j].GetDouble() / 60);
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
            {
                log.LogWarning("Road times not available: {Message}", e.Message);
            }
        }
        return (a, b) => Cache.TryGetValue(Key(a) + ";" + Key(b), out var c) ? c.Minutes : ProposalEngine.Estimate(a, b);
    }
}
