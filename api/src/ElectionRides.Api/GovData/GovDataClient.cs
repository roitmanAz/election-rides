using System.Text.Json;

namespace ElectionRides.Api.GovData;

public record Locality(int Code, string Name);
public record OfficialStreet(int Code, string Name);

/// <summary>Official lists from data.gov.il: localities (for a new city) and streets (per locality).</summary>
public interface IGovData
{
    Task<IReadOnlyList<Locality>> LocalitiesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<OfficialStreet>> StreetsAsync(int localityCode, CancellationToken ct = default);
}

public class GovDataClient(HttpClient http) : IGovData
{
    // Datasets on data.gov.il (CKAN datastore).
    const string LocalitiesResource = "5c78e9fa-c2e2-4771-93ff-7f400a12f7ba";
    const string StreetsResource = "9ad3862c-8391-4b2f-84a4-2d4c68625f4b";
    // Street code 9000 and above stand for the locality itself, not a street.
    const int FirstNonStreetCode = 9000;

    IReadOnlyList<Locality>? _localities;
    DateTimeOffset _localitiesAt;

    public async Task<IReadOnlyList<Locality>> LocalitiesAsync(CancellationToken ct = default)
    {
        if (_localities is not null && DateTimeOffset.UtcNow - _localitiesAt < TimeSpan.FromDays(1)) return _localities;
        var rows = await FetchAll(LocalitiesResource, null, ct);
        _localities = rows
            .Select(r => new Locality(ReadInt(r, "סמל_ישוב"), Clean(r.GetProperty("שם_ישוב").GetString())))
            .Where(l => l.Code > 0 && l.Name.Length > 0)
            .DistinctBy(l => l.Code).OrderBy(l => l.Name).ToList();
        _localitiesAt = DateTimeOffset.UtcNow;
        return _localities;
    }

    public async Task<IReadOnlyList<OfficialStreet>> StreetsAsync(int localityCode, CancellationToken ct = default)
    {
        var rows = await FetchAll(StreetsResource, JsonSerializer.Serialize(new Dictionary<string, int> { ["סמל_ישוב"] = localityCode }), ct);
        return rows
            .Select(r => new OfficialStreet(ReadInt(r, "סמל_רחוב"), Clean(r.GetProperty("שם_רחוב").GetString())))
            .Where(s => s.Code > 0 && s.Code < FirstNonStreetCode && s.Name.Length > 0)
            .DistinctBy(s => s.Name).ToList();
    }

    async Task<List<JsonElement>> FetchAll(string resource, string? filters, CancellationToken ct)
    {
        const int page = 5000;
        var all = new List<JsonElement>();
        for (var offset = 0; ; offset += page)
        {
            var url = $"api/3/action/datastore_search?resource_id={resource}&limit={page}&offset={offset}";
            if (filters is not null) url += "&filters=" + Uri.EscapeDataString(filters);
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url, ct));
            var records = doc.RootElement.GetProperty("result").GetProperty("records").EnumerateArray().Select(e => e.Clone()).ToList();
            all.AddRange(records);
            if (records.Count < page) return all;
        }
    }

    static int ReadInt(JsonElement r, string field)
    {
        var v = r.GetProperty(field);
        return v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : int.TryParse(v.GetString()?.Trim(), out var n) ? n : 0;
    }

    /// <summary>The lists pad names with spaces and sometimes double spaces.</summary>
    static string Clean(string? s) => string.Join(' ', (s ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
}
