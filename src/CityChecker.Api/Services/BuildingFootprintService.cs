using System.Globalization;
using System.Text.Json;
using CityChecker.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CityChecker.Api.Services;

// ponytail: Wołomin-only OSM footprints; viewport Overpass + short memory cache. Expand to other districts later.
public class BuildingFootprintService(
    AppDbContext db,
    HttpClient http,
    IMemoryCache cache,
    ILogger<BuildingFootprintService> log)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
    const int MaxFeatures = 800;
    const string PilotDistrictName = "Wołomin";

    static readonly string[] OverpassUrls =
    [
        "https://overpass.openstreetmap.fr/api/interpreter",
        "https://overpass-api.de/api/interpreter",
    ];

    public async Task<object> GetFootprintsAsync(
        Guid cityId,
        double minLat,
        double minLon,
        double maxLat,
        double maxLon,
        CancellationToken ct = default)
    {
        var empty = FeatureCollection([]);
        if (minLat >= maxLat || minLon >= maxLon) return empty;

        var district = await db.Districts.AsNoTracking()
            .Where(d => d.CityId == cityId && d.Name == PilotDistrictName)
            .Select(d => new { d.Geom })
            .FirstOrDefaultAsync(ct);
        if (district?.Geom is null) return empty;

        var env = district.Geom.EnvelopeInternal;
        var clipMinLat = Math.Max(minLat, env.MinY);
        var clipMinLon = Math.Max(minLon, env.MinX);
        var clipMaxLat = Math.Min(maxLat, env.MaxY);
        var clipMaxLon = Math.Min(maxLon, env.MaxX);
        if (clipMinLat >= clipMaxLat || clipMinLon >= clipMaxLon) return empty;

        var cacheKey =
            $"bf:{cityId}:{Round4(clipMinLat)}:{Round4(clipMinLon)}:{Round4(clipMaxLat)}:{Round4(clipMaxLon)}";
        if (cache.TryGetValue(cacheKey, out object? hit) && hit is not null)
            return hit;

        var elements = await FetchOverpassAsync(clipMinLat, clipMinLon, clipMaxLat, clipMaxLon, ct);
        var features = new List<object>(Math.Min(elements.Count, MaxFeatures));
        foreach (var el in elements)
        {
            if (features.Count >= MaxFeatures) break;
            if (TryFeature(el, out var feature))
                features.Add(feature!);
        }

        var result = FeatureCollection(features);
        cache.Set(cacheKey, result, CacheTtl);
        return result;
    }

    async Task<List<JsonElement>> FetchOverpassAsync(
        double minLat, double minLon, double maxLat, double maxLon, CancellationToken ct)
    {
        var s = CultureInfo.InvariantCulture;
        var bbox = $"{minLat.ToString(s)},{minLon.ToString(s)},{maxLat.ToString(s)},{maxLon.ToString(s)}";
        var query = $"""
            [out:json][timeout:60];
            (
              way["building"]({bbox});
              relation["building"]({bbox});
            );
            out geom;
            """;

        log.LogInformation("Building footprints Overpass bbox {Bbox}", bbox);
        foreach (var url in OverpassUrls)
        {
            try
            {
                using var content = new StringContent(query);
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.TryAddWithoutValidation("User-Agent", "CityChecker/1.0 (personal building footprints)");
                using var res = await http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                {
                    log.LogWarning("Overpass {Url} returned {Status}", url, res.StatusCode);
                    continue;
                }
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("elements", out var els))
                {
                    log.LogWarning("Overpass {Url} missing elements", url);
                    continue;
                }
                var list = els.EnumerateArray().Select(e => e.Clone()).ToList();
                log.LogInformation("Overpass {Url} returned {Count} building elements", url, list.Count);
                return list;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Overpass {Url} failed", url);
            }
        }
        return [];
    }

    static bool TryFeature(JsonElement el, out object? feature)
    {
        feature = null;
        if (!el.TryGetProperty("type", out var typeEl)) return false;
        var osmType = typeEl.GetString();
        if (osmType is not ("way" or "relation")) return false;
        if (!el.TryGetProperty("id", out var idEl)) return false;
        var osmId = idEl.GetInt64();

        List<double[]>? ring = null;
        if (osmType == "way")
            ring = RingFromGeometry(el);
        else if (el.TryGetProperty("members", out var members))
        {
            // ponytail: first outer ring only; ignore holes / multi-outer. Upgrade: full multipolygon.
            foreach (var m in members.EnumerateArray())
            {
                var role = m.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : "";
                if (role is "inner") continue;
                ring = RingFromGeometry(m);
                if (ring is not null) break;
            }
        }
        if (ring is null || ring.Count < 4) return false;

        string? name = null;
        string? addr = null;
        if (el.TryGetProperty("tags", out var tags))
        {
            if (tags.TryGetProperty("name", out var n)) name = n.GetString();
            var street = tags.TryGetProperty("addr:street", out var st) ? st.GetString() : null;
            var housenumber = tags.TryGetProperty("addr:housenumber", out var hn) ? hn.GetString() : null;
            if (!string.IsNullOrWhiteSpace(street) || !string.IsNullOrWhiteSpace(housenumber))
                addr = $"{street} {housenumber}".Trim();
        }

        feature = new
        {
            type = "Feature",
            properties = new { osmType, osmId, name, addr },
            geometry = new
            {
                type = "Polygon",
                coordinates = new[] { ring },
            },
        };
        return true;
    }

    /// <summary>Overpass geometry nodes → closed GeoJSON ring [lon, lat].</summary>
    internal static List<double[]>? RingFromGeometry(JsonElement el)
    {
        if (!el.TryGetProperty("geometry", out var geom) || geom.ValueKind != JsonValueKind.Array)
            return null;
        var ring = new List<double[]>();
        foreach (var pt in geom.EnumerateArray())
        {
            if (!pt.TryGetProperty("lat", out var latEl) || !pt.TryGetProperty("lon", out var lonEl))
                continue;
            ring.Add([lonEl.GetDouble(), latEl.GetDouble()]);
        }
        if (ring.Count < 3) return null;
        var first = ring[0];
        var last = ring[^1];
        if (first[0] != last[0] || first[1] != last[1])
            ring.Add([first[0], first[1]]);
        return ring.Count >= 4 ? ring : null;
    }

    static object FeatureCollection(List<object> features) =>
        new { type = "FeatureCollection", features };

    static string Round4(double v) =>
        Math.Round(v, 4).ToString(CultureInfo.InvariantCulture);
}
