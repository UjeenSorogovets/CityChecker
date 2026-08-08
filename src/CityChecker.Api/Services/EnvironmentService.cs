using System.Globalization;
using System.Text;
using System.Text.Json;
using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using CityChecker.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Services;

// ponytail: one Overpass bbox query per city + optional curated JSON, cached 7 days.
public class EnvironmentService(
    AppDbContext db,
    HttpClient http,
    ILogger<EnvironmentService> log)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromDays(7);
    const double BboxPadDeg = 0.15;
    const double DownwindFreqThreshold = 0.14;
    static readonly string WindRosePath = Path.Combine("DataImports", "wind-rose.json");
    static readonly string LodzPollutionPath = Path.Combine("DataImports", "lodz-pollution-sources.json");

    static readonly string[] OverpassUrls =
    [
        "https://overpass.openstreetmap.fr/api/interpreter",
        "https://overpass-api.de/api/interpreter",
    ];

    public async Task<CityEnvironmentDto> GetOrComputeAsync(Guid cityId, bool forceRefresh = false, CancellationToken ct = default)
    {
        if (!forceRefresh && await TryReadCacheAsync(cityId, ct) is { } cached)
            return cached;

        try
        {
            return await ComputeAndStoreAsync(cityId, ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Environment compute failed for {CityId}", cityId);
            if (await TryReadCacheAsync(cityId, ct, ignoreTtl: true) is { } stale)
                return stale;
            return EmptyDto();
        }
    }

    async Task<CityEnvironmentDto?> TryReadCacheAsync(Guid cityId, CancellationToken ct, bool ignoreTtl = false)
    {
        var districtIds = await db.Districts.AsNoTracking()
            .Where(d => d.CityId == cityId)
            .Select(d => d.DistrictId)
            .ToListAsync(ct);
        if (districtIds.Count == 0) return EmptyDto();

        var envs = await db.DistrictEnvironments.AsNoTracking()
            .Where(e => districtIds.Contains(e.DistrictId))
            .ToListAsync(ct);
        if (envs.Count != districtIds.Count) return null;

        var sources = await db.CityEnvironmentSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.CityId == cityId, ct);
        if (sources is null) return null;

        var computedAt = envs.Min(e => e.ComputedAt);
        if (sources.ComputedAt < computedAt) computedAt = sources.ComputedAt;
        if (!ignoreTtl && DateTime.UtcNow - computedAt > CacheTtl) return null;

        return ToDto(computedAt, envs, sources.SourcesGeoJson);
    }

    async Task<CityEnvironmentDto> ComputeAndStoreAsync(Guid cityId, CancellationToken ct)
    {
        var districts = await db.Districts.AsNoTracking()
            .Where(d => d.CityId == cityId)
            .Select(d => new { d.DistrictId, Centroid = d.Geom.Centroid })
            .ToListAsync(ct);
        if (districts.Count == 0) return EmptyDto();

        var minLat = districts.Min(d => d.Centroid.Y) - BboxPadDeg;
        var maxLat = districts.Max(d => d.Centroid.Y) + BboxPadDeg;
        var minLon = districts.Min(d => d.Centroid.X) - BboxPadDeg;
        var maxLon = districts.Max(d => d.Centroid.X) + BboxPadDeg;

        var elements = await FetchOverpassAsync(minLat, minLon, maxLat, maxLon, ct);
        var wind = LoadWindRose(cityId);
        var windFromBearing = PrevailingWindFromBearing(wind);

        var landfills = new List<GeoPoint>();
        var incinerators = new List<GeoPoint>();
        var rails = new List<GeoPoint>();
        var airports = new List<GeoPoint>();
        var industrials = new List<GeoPoint>();
        var highways = new List<GeoPoint>();
        var features = new List<Dictionary<string, object?>>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddFeature(string type, string name, double lat, double lon, double weight, double influenceKm, string? id = null, string? notes = null, bool curated = false)
        {
            var key = id ?? $"{type}:{lat:F4},{lon:F4}";
            if (!seenKeys.Add(key)) return;
            features.Add(SourceFeature(type, name, lat, lon, weight, influenceKm, windFromBearing, notes, curated));
        }

        foreach (var el in elements)
        {
            var tags = el.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
            if (tags.ValueKind != JsonValueKind.Object) continue;

            var (lat, lon) = CenterOf(el);
            if (lat is null || lon is null) continue;
            var name = tags.TryGetProperty("name", out var n) ? n.GetString() : null;
            var pt = new GeoPoint(lat.Value, lon.Value);

            if (IsIncinerator(tags))
            {
                incinerators.Add(pt);
                landfills.Add(pt); // odor family
                AddFeature("waste_incinerator", name ?? "Waste incinerator", lat.Value, lon.Value, 1.0, 8);
            }
            else if (IsWasteTransfer(tags))
            {
                landfills.Add(pt);
                AddFeature("waste_transfer", name ?? "Waste transfer", lat.Value, lon.Value, 0.7, 3.5);
            }
            else if (IsLandfillOnly(tags))
            {
                landfills.Add(pt);
                // Generic OSM "Landfill" points are noisy — marker only, no ring
                var named = !string.IsNullOrWhiteSpace(name);
                AddFeature("landfill", name ?? "Landfill", lat.Value, lon.Value, named ? 0.85 : 0.5,
                    influenceKm: named ? 3.5 : 0);
            }
            else if (IsAirport(tags))
            {
                airports.Add(pt);
                AddFeature("airport", name ?? "Airport", lat.Value, lon.Value, 0.5, 0); // no odor ring
            }
            else if (IsHeavyIndustry(tags))
            {
                industrials.Add(pt);
                var itype = IsPowerPlant(tags) ? "power_plant" : "factory";
                // Markers only for OSM factories — rings reserved for waste + curated (too many otherwise)
                AddFeature(itype, name ?? (itype == "power_plant" ? "Power plant" : "Factory"), lat.Value, lon.Value,
                    itype == "power_plant" ? 0.75 : 0.6,
                    influenceKm: 0);
            }
            else if (IsIndustrial(tags))
            {
                industrials.Add(pt);
                // generic industrial zone — distance only, no ring (too dense)
            }
            else if (IsRail(tags))
            {
                rails.Add(pt);
            }
            else if (IsHighway(tags))
            {
                highways.Add(pt);
            }
        }

        foreach (var r in DedupPoints(rails, 0.025))
            AddFeature("rail", "Railway", r.Lat, r.Lon, 0.3, 0);

        // Curated Łódź sources (rings always drawn for these)
        foreach (var c in LoadCuratedSources(cityId))
        {
            var pt = new GeoPoint(c.Lat, c.Lon);
            if (c.Type is "landfill" or "waste_transfer" or "waste_incinerator")
            {
                landfills.Add(pt);
                if (c.Type == "waste_incinerator") incinerators.Add(pt);
            }
            else
                industrials.Add(pt);
            AddFeature(c.Type, c.Name, c.Lat, c.Lon, c.Weight, c.InfluenceKm, c.Id, c.Notes, curated: true);
        }

        var now = DateTime.UtcNow;
        var envRows = new List<DistrictEnvironment>();
        foreach (var d in districts)
        {
            var clat = d.Centroid.Y;
            var clon = d.Centroid.X;
            var landfillKm = NearestKm(clat, clon, landfills);
            var incineratorKm = NearestKm(clat, clon, incinerators);
            var railKm = NearestKm(clat, clon, rails);
            var airportKm = NearestKm(clat, clon, airports);
            var industrialKm = NearestKm(clat, clon, industrials);
            var highwayKm = NearestKm(clat, clon, highways);

            var downwind = false;
            if (landfillKm is not null && landfills.Count > 0)
            {
                var nearest = landfills.OrderBy(p => GeoHelper.HaversineKm(clat, clon, p.Lat, p.Lon)).First();
                // District is downwind when wind often comes FROM the opposite of source→district
                var toDistrict = GeoHelper.BearingDegrees(nearest.Lat, nearest.Lon, clat, clon);
                var windFromNeeded = (toDistrict + 180.0) % 360.0;
                var sector = GeoHelper.Sector8(windFromNeeded);
                if (wind.TryGetValue(sector, out var freq) && freq >= DownwindFreqThreshold)
                    downwind = true;
            }

            var odorRisk = Math.Max(
                LandfillRisk(landfillKm, downwind),
                IncineratorRisk(incineratorKm, downwind));
            var industrialRisk = IndustrialRisk(industrialKm);
            var risk = Math.Max(
                Math.Max(odorRisk, RailRisk(railKm)),
                Math.Max(Math.Max(AirportRisk(airportKm), industrialRisk), HighwayRisk(highwayKm)));

            envRows.Add(new DistrictEnvironment
            {
                DistrictId = d.DistrictId,
                EnvRiskOverall = risk,
                NearestLandfillKm = RoundKm(landfillKm),
                NearestRailKm = RoundKm(railKm),
                NearestAirportKm = RoundKm(airportKm),
                NearestIndustrialKm = RoundKm(industrialKm),
                NearestHighwayKm = RoundKm(highwayKm),
                LandfillDownwind = downwind,
                ComputedAt = now,
            });
        }

        var sourcesJson = JsonSerializer.Serialize(new { type = "FeatureCollection", features });

        var ids = envRows.Select(e => e.DistrictId).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var oldEnv = await db.DistrictEnvironments.Where(e => ids.Contains(e.DistrictId)).ToListAsync(ct);
        db.DistrictEnvironments.RemoveRange(oldEnv);
        db.DistrictEnvironments.AddRange(envRows);

        var oldSources = await db.CityEnvironmentSources.FirstOrDefaultAsync(s => s.CityId == cityId, ct);
        if (oldSources is not null) db.CityEnvironmentSources.Remove(oldSources);
        db.CityEnvironmentSources.Add(new CityEnvironmentSources
        {
            CityId = cityId,
            SourcesGeoJson = sourcesJson,
            ComputedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        log.LogInformation(
            "Environment computed for {CityId}: {Districts} districts, {Sources} sources",
            cityId, envRows.Count, features.Count);

        return ToDto(now, envRows, sourcesJson);
    }

    async Task<List<JsonElement>> FetchOverpassAsync(double minLat, double minLon, double maxLat, double maxLon, CancellationToken ct)
    {
        var s = CultureInfo.InvariantCulture;
        var bbox = $"{minLat.ToString(s)},{minLon.ToString(s)},{maxLat.ToString(s)},{maxLon.ToString(s)}";
        var query = $"""
            [out:json][timeout:60];
            (
              way["landuse"="landfill"]({bbox});
              node["landuse"="landfill"]({bbox});
              node["amenity"="waste_transfer_station"]({bbox});
              way["amenity"="waste_transfer_station"]({bbox});
              way["plant:source"="waste"]({bbox});
              node["plant:source"="waste"]({bbox});
              way["power"="plant"]({bbox});
              node["power"="plant"]({bbox});
              way["man_made"="works"]({bbox});
              node["man_made"="works"]({bbox});
              way["industrial"="factory"]({bbox});
              node["industrial"="factory"]({bbox});
              way["railway"="rail"]({bbox});
              way["aeroway"="aerodrome"]({bbox});
              node["aeroway"="aerodrome"]({bbox});
              way["landuse"="industrial"]({bbox});
              way["highway"~"motorway|trunk|primary"]({bbox});
            );
            out center;
            """;

        log.LogInformation("Overpass bbox {Bbox}", bbox);
        foreach (var url in OverpassUrls)
        {
            try
            {
                using var content = new StringContent(query);
                using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                req.Headers.TryAddWithoutValidation("User-Agent", "CityChecker/1.0 (personal environment probe)");
                using var res = await http.SendAsync(req, ct);
                var body = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                {
                    log.LogWarning("Overpass {Url} returned {Status}", url, res.StatusCode);
                    continue;
                }
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("remark", out var remark))
                    log.LogWarning("Overpass remark: {Remark}", remark.GetString());
                if (!doc.RootElement.TryGetProperty("elements", out var els))
                {
                    log.LogWarning("Overpass {Url} missing elements", url);
                    continue;
                }
                var list = els.EnumerateArray().Select(e => e.Clone()).ToList();
                log.LogInformation("Overpass {Url} returned {Count} elements", url, list.Count);
                return list;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Overpass {Url} failed", url);
            }
        }
        return [];
    }

    List<CuratedSource> LoadCuratedSources(Guid cityId)
    {
        if (cityId != SeedData.LodzId) return [];
        var path = ResolveDataPath(LodzPollutionPath);
        if (path is null)
        {
            log.LogWarning("lodz-pollution-sources.json not found");
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("sources", out var arr)) return [];
            var list = new List<CuratedSource>();
            foreach (var s in arr.EnumerateArray())
            {
                list.Add(new CuratedSource(
                    s.GetProperty("id").GetString() ?? Guid.NewGuid().ToString(),
                    s.GetProperty("type").GetString() ?? "factory",
                    s.GetProperty("name").GetString() ?? "Source",
                    s.GetProperty("lat").GetDouble(),
                    s.GetProperty("lon").GetDouble(),
                    s.TryGetProperty("weight", out var w) ? w.GetDouble() : 0.7,
                    s.TryGetProperty("influenceKm", out var ik) ? ik.GetDouble() : 3,
                    s.TryGetProperty("notes", out var n) ? n.GetString() : null));
            }
            log.LogInformation("Loaded {Count} curated Łódź pollution sources", list.Count);
            return list;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to load lodz-pollution-sources.json");
            return [];
        }
    }

    Dictionary<string, double> LoadWindRose(Guid cityId)
    {
        var path = ResolveDataPath(WindRosePath);
        if (path is null)
        {
            log.LogWarning("wind-rose.json not found");
            return DefaultWind();
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var key = cityId.ToString();
            if (!doc.RootElement.TryGetProperty(key, out var city))
                return DefaultWind();
            var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in city.EnumerateObject())
                dict[p.Name] = p.Value.GetDouble();
            return dict;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to load wind-rose.json");
            return DefaultWind();
        }
    }

    static string? ResolveDataPath(string relative)
    {
        foreach (var candidate in new[]
                 {
                     relative,
                     Path.Combine(AppContext.BaseDirectory, relative),
                     Path.Combine(Directory.GetCurrentDirectory(), relative),
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    static Dictionary<string, double> DefaultWind() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["N"] = 0.08, ["NE"] = 0.08, ["E"] = 0.10, ["SE"] = 0.09,
        ["S"] = 0.11, ["SW"] = 0.18, ["W"] = 0.22, ["NW"] = 0.14,
    };

    /** Compass bearing wind usually comes FROM (meteorological convention). */
    static double PrevailingWindFromBearing(Dictionary<string, double> wind)
    {
        var best = "W";
        var bestF = 0.0;
        foreach (var (k, v) in wind)
        {
            if (v > bestF) { bestF = v; best = k; }
        }
        return best.ToUpperInvariant() switch
        {
            "N" => 0, "NE" => 45, "E" => 90, "SE" => 135,
            "S" => 180, "SW" => 225, "W" => 270, "NW" => 315,
            _ => 270,
        };
    }

    static string CompassLabel(double bearingDeg)
    {
        var x = ((bearingDeg % 360) + 360) % 360;
        return x switch
        {
            < 22.5 or >= 337.5 => "N",
            < 67.5 => "NE",
            < 112.5 => "E",
            < 157.5 => "SE",
            < 202.5 => "S",
            < 247.5 => "SW",
            < 292.5 => "W",
            _ => "NW",
        };
    }

    static bool IsIncinerator(JsonElement tags) =>
        tags.TryGetProperty("plant:source", out var ps) && ps.GetString() == "waste";

    static bool IsWasteTransfer(JsonElement tags) =>
        tags.TryGetProperty("amenity", out var am) && am.GetString() == "waste_transfer_station";

    static bool IsLandfillOnly(JsonElement tags) =>
        tags.TryGetProperty("landuse", out var lu) && lu.GetString() == "landfill";

    static bool IsAirport(JsonElement tags) =>
        tags.TryGetProperty("aeroway", out var a) && a.GetString() == "aerodrome";

    static bool IsPowerPlant(JsonElement tags) =>
        tags.TryGetProperty("power", out var p) && p.GetString() == "plant";

    static bool IsHeavyIndustry(JsonElement tags) =>
        IsPowerPlant(tags)
        || (tags.TryGetProperty("man_made", out var mm) && mm.GetString() == "works")
        || (tags.TryGetProperty("industrial", out var ind) && ind.GetString() == "factory");

    static bool IsIndustrial(JsonElement tags) =>
        tags.TryGetProperty("landuse", out var lu) && lu.GetString() == "industrial";

    static bool IsRail(JsonElement tags) =>
        tags.TryGetProperty("railway", out var rw) && rw.GetString() == "rail";

    static bool IsHighway(JsonElement tags) =>
        tags.TryGetProperty("highway", out var hw) && hw.GetString() is "motorway" or "trunk" or "primary";

    static (double? Lat, double? Lon) CenterOf(JsonElement el)
    {
        if (el.TryGetProperty("lat", out var lat) && el.TryGetProperty("lon", out var lon))
            return (lat.GetDouble(), lon.GetDouble());
        if (el.TryGetProperty("center", out var c))
            return (c.GetProperty("lat").GetDouble(), c.GetProperty("lon").GetDouble());
        return (null, null);
    }

    static double? NearestKm(double lat, double lon, List<GeoPoint> points)
    {
        if (points.Count == 0) return null;
        double best = double.MaxValue;
        foreach (var p in points)
        {
            var km = GeoHelper.HaversineKm(lat, lon, p.Lat, p.Lon);
            if (km < best) best = km;
        }
        return best;
    }

    static List<GeoPoint> DedupPoints(List<GeoPoint> points, double cellDeg)
    {
        var seen = new HashSet<(int, int)>();
        var result = new List<GeoPoint>();
        foreach (var p in points)
        {
            var key = ((int)Math.Floor(p.Lat / cellDeg), (int)Math.Floor(p.Lon / cellDeg));
            if (seen.Add(key)) result.Add(p);
        }
        return result;
    }

    static Dictionary<string, object?> SourceFeature(
        string type, string name, double lat, double lon, double weight, double influenceKm,
        double windFromBearing, string? notes, bool curated)
    {
        var windTo = (windFromBearing + 180.0) % 360.0;
        return new()
        {
            ["type"] = "Feature",
            ["geometry"] = new Dictionary<string, object?>
            {
                ["type"] = "Point",
                ["coordinates"] = new[] { lon, lat },
            },
            ["properties"] = new Dictionary<string, object?>
            {
                ["type"] = type,
                ["name"] = name,
                ["weight"] = weight,
                ["influenceKm"] = influenceKm,
                // windBearing = plume / downwind direction (where odor usually goes)
                ["windBearing"] = windTo,
                ["windFromBearing"] = windFromBearing,
                ["windFrom"] = CompassLabel(windFromBearing),
                ["windTo"] = CompassLabel(windTo),
                ["showRing"] = influenceKm > 0,
                ["curated"] = curated,
                ["notes"] = notes,
            },
        };
    }

    static int LandfillRisk(double? km, bool downwind)
    {
        if (km is null) return 1;
        var baseRisk = km < 2 ? 9 : km < 5 ? 7 : km < 10 ? 5 : 2;
        return Math.Min(10, baseRisk + (downwind ? 2 : 0));
    }

    static int IncineratorRisk(double? km, bool downwind)
    {
        if (km is null) return 1;
        var baseRisk = km < 3 ? 9 : km < 6 ? 7 : km < 10 ? 5 : 2;
        return Math.Min(10, baseRisk + (downwind ? 2 : 0));
    }

    static int RailRisk(double? km)
    {
        if (km is null) return 1;
        return km < 0.3 ? 9 : km < 0.8 ? 6 : km < 2 ? 4 : 2;
    }

    static int AirportRisk(double? km)
    {
        if (km is null) return 1;
        return km < 3 ? 8 : km < 8 ? 5 : km < 15 ? 3 : 1;
    }

    static int IndustrialRisk(double? km)
    {
        if (km is null) return 1;
        return km < 0.5 ? 7 : km < 1.5 ? 5 : 2;
    }

    static int HighwayRisk(double? km)
    {
        if (km is null) return 2;
        return km < 0.15 ? 9 : km < 0.4 ? 7 : km < 0.8 ? 5 : 3;
    }

    static double? RoundKm(double? km) => km is null ? null : Math.Round(km.Value, 2);

    static CityEnvironmentDto EmptyDto() => new(
        DateTime.UtcNow,
        [],
        JsonDocument.Parse("""{"type":"FeatureCollection","features":[]}""").RootElement.Clone());

    static CityEnvironmentDto ToDto(DateTime computedAt, List<DistrictEnvironment> envs, string sourcesJson)
    {
        JsonElement sources;
        try
        {
            sources = JsonDocument.Parse(sourcesJson).RootElement.Clone();
        }
        catch
        {
            sources = JsonDocument.Parse("""{"type":"FeatureCollection","features":[]}""").RootElement.Clone();
        }
        return new CityEnvironmentDto(
            computedAt,
            envs.Select(e => new DistrictEnvironmentDto(
                e.DistrictId,
                e.EnvRiskOverall,
                e.NearestLandfillKm,
                e.LandfillDownwind,
                e.NearestRailKm,
                e.NearestAirportKm,
                e.NearestIndustrialKm,
                e.NearestHighwayKm)).ToList(),
            sources);
    }

    readonly record struct GeoPoint(double Lat, double Lon);
    readonly record struct CuratedSource(string Id, string Type, string Name, double Lat, double Lon, double Weight, double InfluenceKm, string? Notes);
}
