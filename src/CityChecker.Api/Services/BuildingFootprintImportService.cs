using System.Globalization;
using System.Text.Json;
using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace CityChecker.Api.Services;

// ponytail: one Overpass pull per Wołomin refresh → PostGIS; pan path never hits Overpass.
public class BuildingFootprintImportService(
    AppDbContext db,
    HttpClient http,
    ILogger<BuildingFootprintImportService> log)
{
    public const string PilotDistrictName = "Wołomin";
    const int MaxImportFeatures = 50_000;

    static readonly GeometryFactory Gf =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    static readonly string[] OverpassUrls =
    [
        "https://overpass.openstreetmap.fr/api/interpreter",
        "https://overpass-api.de/api/interpreter",
    ];

    public async Task<(Guid DistrictId, int Count)> ImportForCityAsync(Guid cityId, CancellationToken ct = default)
    {
        var district = await db.Districts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.CityId == cityId && d.Name == PilotDistrictName, ct)
            ?? throw new InvalidOperationException($"District '{PilotDistrictName}' not found for city {cityId}.");

        var env = district.Geom.EnvelopeInternal;
        var elements = await FetchOverpassAsync(env.MinY, env.MinX, env.MaxY, env.MaxX, ct);
        log.LogInformation("Wołomin footprint import: {Count} Overpass elements", elements.Count);

        var now = DateTime.UtcNow;
        var rows = new List<OsmBuildingFootprint>();
        foreach (var el in elements)
        {
            if (rows.Count >= MaxImportFeatures) break;
            if (!TryParseBuilding(el, out var osmType, out var osmId, out var name, out var addr, out var geom)
                || geom is null)
                continue;
            if (!district.Geom.Intersects(geom))
                continue;
            rows.Add(new OsmBuildingFootprint
            {
                OsmBuildingFootprintId = Guid.NewGuid(),
                CityId = cityId,
                DistrictId = district.DistrictId,
                OsmType = osmType!,
                OsmId = osmId,
                Name = name,
                Addr = addr,
                Geom = geom,
                ImportedAt = now,
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.OsmBuildingFootprints.Where(f => f.DistrictId == district.DistrictId).ExecuteDeleteAsync(ct);
        db.OsmBuildingFootprints.AddRange(rows);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation("Wołomin footprint import saved {Count} polygons for district {DistrictId}",
            rows.Count, district.DistrictId);
        return (district.DistrictId, rows.Count);
    }

    async Task<List<JsonElement>> FetchOverpassAsync(
        double minLat, double minLon, double maxLat, double maxLon, CancellationToken ct)
    {
        var s = CultureInfo.InvariantCulture;
        var bbox = $"{minLat.ToString(s)},{minLon.ToString(s)},{maxLat.ToString(s)},{maxLon.ToString(s)}";
        var query = $"""
            [out:json][timeout:180];
            (
              way["building"]({bbox});
              relation["building"]({bbox});
            );
            out geom;
            """;

        log.LogInformation("Building footprint import Overpass bbox {Bbox}", bbox);
        foreach (var url in OverpassUrls)
        {
            try
            {
                using var content = new StringContent(query);
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.TryAddWithoutValidation("User-Agent", "CityChecker/1.0 (personal building footprint import)");
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
                return els.EnumerateArray().Select(e => e.Clone()).ToList();
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Overpass {Url} failed", url);
            }
        }
        return [];
    }

    static bool TryParseBuilding(
        JsonElement el,
        out string? osmType,
        out long osmId,
        out string? name,
        out string? addr,
        out MultiPolygon? geom)
    {
        osmType = null;
        osmId = 0;
        name = null;
        addr = null;
        geom = null;

        if (!el.TryGetProperty("type", out var typeEl)) return false;
        osmType = typeEl.GetString();
        if (osmType is not ("way" or "relation")) return false;
        if (!el.TryGetProperty("id", out var idEl)) return false;
        osmId = idEl.GetInt64();

        List<Coordinate[]>? rings = null;
        if (osmType == "way")
        {
            var ring = RingFromGeometry(el);
            if (ring is not null) rings = [ring];
        }
        else if (el.TryGetProperty("members", out var members))
        {
            // ponytail: outer rings only; skip inners. Upgrade: full multipolygon with holes.
            rings = [];
            foreach (var m in members.EnumerateArray())
            {
                var role = m.TryGetProperty("role", out var roleEl) ? roleEl.GetString() : "";
                if (role is "inner") continue;
                var ring = RingFromGeometry(m);
                if (ring is not null) rings.Add(ring);
            }
        }
        if (rings is null || rings.Count == 0) return false;

        var polygons = new List<Polygon>();
        foreach (var coords in rings)
        {
            if (coords.Length < 4) continue;
            try
            {
                var shell = Gf.CreateLinearRing(coords);
                polygons.Add(Gf.CreatePolygon(shell));
            }
            catch
            {
                /* skip invalid rings */
            }
        }
        if (polygons.Count == 0) return false;
        geom = Gf.CreateMultiPolygon(polygons.ToArray());

        if (el.TryGetProperty("tags", out var tags))
        {
            if (tags.TryGetProperty("name", out var n)) name = Truncate(n.GetString(), 200);
            var street = tags.TryGetProperty("addr:street", out var st) ? st.GetString() : null;
            var housenumber = tags.TryGetProperty("addr:housenumber", out var hn) ? hn.GetString() : null;
            if (!string.IsNullOrWhiteSpace(street) || !string.IsNullOrWhiteSpace(housenumber))
                addr = Truncate($"{street} {housenumber}".Trim(), 300);
        }
        return true;
    }

    static Coordinate[]? RingFromGeometry(JsonElement el)
    {
        if (!el.TryGetProperty("geometry", out var geom) || geom.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<Coordinate>();
        foreach (var pt in geom.EnumerateArray())
        {
            if (!pt.TryGetProperty("lat", out var latEl) || !pt.TryGetProperty("lon", out var lonEl))
                continue;
            list.Add(new Coordinate(lonEl.GetDouble(), latEl.GetDouble()));
        }
        if (list.Count < 3) return null;
        if (!list[0].Equals2D(list[^1]))
            list.Add(new Coordinate(list[0].X, list[0].Y));
        return list.Count >= 4 ? list.ToArray() : null;
    }

    static string? Truncate(string? s, int max) =>
        string.IsNullOrWhiteSpace(s) ? null : (s.Length <= max ? s : s[..max]);
}
