using System.Globalization;
using System.Text.Json;
using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace CityChecker.Api.Services;

// ponytail: Overpass → PostGIS for footprint pilots; pan path never hits Overpass.
public class BuildingFootprintImportService(
    AppDbContext db,
    HttpClient http,
    ILogger<BuildingFootprintImportService> log)
{
    public const string WarszawaPilotDistrictName = "Wołomin";
    const int MaxImportFeatures = 250_000;

    static readonly GeometryFactory Gf =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    static readonly string[] OverpassUrls =
    [
        "https://overpass.openstreetmap.fr/api/interpreter",
        "https://overpass-api.de/api/interpreter",
    ];

    public static bool SupportsCity(Guid cityId) =>
        cityId == SeedData.WarszawaId || cityId == SeedData.WroclawId;

    public async Task<(int Count, string Scope)> ImportForCityAsync(Guid cityId, CancellationToken ct = default)
    {
        if (cityId == SeedData.WroclawId)
            return await ImportCityWideAsync(cityId, ct);
        if (cityId == SeedData.WarszawaId)
            return await ImportDistrictAsync(cityId, WarszawaPilotDistrictName, ct);
        throw new InvalidOperationException(
            $"Building footprints not configured for city {cityId} (Wołomin / Wrocław only).");
    }

    async Task<(int Count, string Scope)> ImportDistrictAsync(
        Guid cityId, string districtName, CancellationToken ct)
    {
        var district = await db.Districts.AsNoTracking()
            .FirstOrDefaultAsync(d => d.CityId == cityId && d.Name == districtName, ct)
            ?? throw new InvalidOperationException($"District '{districtName}' not found for city {cityId}.");

        var env = district.Geom.EnvelopeInternal;
        var elements = await FetchOverpassAsync(env.MinY, env.MinX, env.MaxY, env.MaxX, ct);
        log.LogInformation("{District} footprint import: {Count} Overpass elements", districtName, elements.Count);

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
            rows.Add(NewRow(cityId, district.DistrictId, osmType!, osmId, name, addr, geom, now));
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.OsmBuildingFootprints.Where(f => f.DistrictId == district.DistrictId).ExecuteDeleteAsync(ct);
        db.OsmBuildingFootprints.AddRange(rows);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation("{District} footprint import saved {Count} polygons", districtName, rows.Count);
        return (rows.Count, districtName);
    }

    // ponytail: per-osiedle Overpass — whole-city bbox often times out. Ceiling: ~48 calls; upgrade: grid tiles.
    async Task<(int Count, string Scope)> ImportCityWideAsync(Guid cityId, CancellationToken ct)
    {
        var districts = await db.Districts.AsNoTracking()
            .Where(d => d.CityId == cityId)
            .ToListAsync(ct);
        if (districts.Count == 0)
            throw new InvalidOperationException($"No districts for city {cityId}.");

        var now = DateTime.UtcNow;
        var byOsm = new Dictionary<(string Type, long Id), OsmBuildingFootprint>();
        foreach (var district in districts)
        {
            ct.ThrowIfCancellationRequested();
            var env = district.Geom.EnvelopeInternal;
            var elements = await FetchOverpassAsync(env.MinY, env.MinX, env.MaxY, env.MaxX, ct);
            log.LogInformation(
                "Wrocław footprint import district {Name}: {Count} Overpass elements (total unique {Unique})",
                district.Name, elements.Count, byOsm.Count);

            foreach (var el in elements)
            {
                if (byOsm.Count >= MaxImportFeatures) break;
                if (!TryParseBuilding(el, out var osmType, out var osmId, out var name, out var addr, out var geom)
                    || geom is null)
                    continue;
                if (!district.Geom.Intersects(geom))
                    continue;
                var key = (osmType!, osmId);
                if (byOsm.ContainsKey(key)) continue;
                byOsm[key] = NewRow(cityId, district.DistrictId, osmType!, osmId, name, addr, geom, now);
            }

            if (byOsm.Count >= MaxImportFeatures)
            {
                log.LogWarning("Wrocław footprint import hit MaxImportFeatures={Max}", MaxImportFeatures);
                break;
            }
        }

        var rows = byOsm.Values.ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.OsmBuildingFootprints.Where(f => f.CityId == cityId).ExecuteDeleteAsync(ct);
        db.OsmBuildingFootprints.AddRange(rows);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation("Wrocław footprint import saved {Count} polygons across {Districts} districts",
            rows.Count, districts.Count);
        return (rows.Count, "Wrocław");
    }

    static OsmBuildingFootprint NewRow(
        Guid cityId, Guid districtId, string osmType, long osmId,
        string? name, string? addr, MultiPolygon geom, DateTime now) =>
        new()
        {
            OsmBuildingFootprintId = Guid.NewGuid(),
            CityId = cityId,
            DistrictId = districtId,
            OsmType = osmType,
            OsmId = osmId,
            Name = name,
            Addr = addr,
            Geom = geom,
            ImportedAt = now,
        };

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
