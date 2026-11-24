using NetTopologySuite.Geometries;

namespace ElectionRides.Api.Maps;

public record GeoResult(Point? Location, bool Approximate);

/// <summary>Finds a street address on the map (spec 5.1). Google Geocoding is connected with the maps setup.</summary>
public interface IGeocoder
{
    Task<GeoResult> FindAsync(string city, string street, string building, CancellationToken ct = default);
}

/// <summary>
/// Used until a Google key is configured: every address is saved as "approximate location",
/// which never blocks saving (spec 5.1), and the dispatcher sees the mark.
/// </summary>
public class NoGeocoder : IGeocoder
{
    public Task<GeoResult> FindAsync(string city, string street, string building, CancellationToken ct = default) =>
        Task.FromResult(new GeoResult(null, true));
}

/// <summary>
/// Development only: the free OpenStreetMap lookup (Nominatim). Its rules allow light use, one request a second,
/// with an identifying client name; each address is looked up once and saved. When the building is not found,
/// the street alone gives an approximate location (spec 5.1). Replaced by Google before going live, if chosen.
/// </summary>
public class NominatimGeocoder(HttpClient http) : IGeocoder
{
    static readonly SemaphoreSlim OneAtATime = new(1, 1);
    static DateTimeOffset _last = DateTimeOffset.MinValue;
    /// <summary>Each city's area (south, north, west, east), looked up once.</summary>
    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, double[]> Areas = new();

    public async Task<GeoResult> FindAsync(string city, string street, string building, CancellationToken ct = default)
    {
        try
        {
            // Every search stays inside the city's area: without that, "6 <street>" can come back as
            // Highway 6 in another city. A result outside the area is dropped.
            var area = await Area(city, ct);
            if (area is null) return new GeoResult(null, true);
            var box = FormattableString.Invariant($"&viewbox={area[2]},{area[1]},{area[3]},{area[0]}&bounded=1");
            var exact = await Search($"street={Uri.EscapeDataString(building + " " + street)}&city={Uri.EscapeDataString(city)}{box}", area, ct);
            if (exact is not null) return new GeoResult(exact, false);
            var streetOnly = await Search($"street={Uri.EscapeDataString(street)}&city={Uri.EscapeDataString(city)}{box}", area, ct);
            return new GeoResult(streetOnly, true);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The lookup never blocks saving a request.
            return new GeoResult(null, true);
        }
    }

    async Task<double[]?> Area(string city, CancellationToken ct)
    {
        if (Areas.TryGetValue(city, out var known)) return known;
        using var doc = System.Text.Json.JsonDocument.Parse(await Get($"search?city={Uri.EscapeDataString(city)}&country=Israel&format=jsonv2&limit=1", ct));
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == System.Text.Json.JsonValueKind.Undefined) return null;
        var area = first.GetProperty("boundingbox").EnumerateArray()
            .Select(v => double.Parse(v.GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return Areas[city] = area;
    }

    async Task<string> Get(string path, CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            var wait = _last.AddSeconds(1.1) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTimeOffset.UtcNow;
            return await http.GetStringAsync(path, ct);
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    async Task<Point?> Search(string query, double[] area, CancellationToken ct)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(await Get($"search?{query}&country=Israel&format=jsonv2&limit=1", ct));
        var first = doc.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == System.Text.Json.JsonValueKind.Undefined) return null;
        var lat = double.Parse(first.GetProperty("lat").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        var lon = double.Parse(first.GetProperty("lon").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        if (lat < area[0] || lat > area[1] || lon < area[2] || lon > area[3]) return null;
        return new Point(lon, lat) { SRID = 4326 };
    }
}
