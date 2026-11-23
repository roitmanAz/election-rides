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

    public async Task<GeoResult> FindAsync(string city, string street, string building, CancellationToken ct = default)
    {
        try
        {
            var exact = await Search($"street={Uri.EscapeDataString(building + " " + street)}&city={Uri.EscapeDataString(city)}", ct);
            if (exact is not null) return new GeoResult(exact, false);
            var streetOnly = await Search($"street={Uri.EscapeDataString(street)}&city={Uri.EscapeDataString(city)}", ct);
            return new GeoResult(streetOnly, true);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The lookup never blocks saving a request.
            return new GeoResult(null, true);
        }
    }

    async Task<Point?> Search(string query, CancellationToken ct)
    {
        await OneAtATime.WaitAsync(ct);
        try
        {
            var wait = _last.AddSeconds(1.1) - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _last = DateTimeOffset.UtcNow;
            using var doc = System.Text.Json.JsonDocument.Parse(
                await http.GetStringAsync($"search?{query}&country=Israel&format=jsonv2&limit=1", ct));
            var first = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == System.Text.Json.JsonValueKind.Undefined) return null;
            var lat = double.Parse(first.GetProperty("lat").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            var lon = double.Parse(first.GetProperty("lon").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
            return new Point(lon, lat) { SRID = 4326 };
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}
