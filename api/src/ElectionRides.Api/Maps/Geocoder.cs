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
