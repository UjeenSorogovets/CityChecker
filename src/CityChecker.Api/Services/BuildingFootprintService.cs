using System.Text.Json;
using CityChecker.Api.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace CityChecker.Api.Services;

// ponytail: serve footprint pilots from PostGIS; Overpass only via import/seed.
public class BuildingFootprintService(
    AppDbContext db,
    BuildingFootprintImportService importer,
    ILogger<BuildingFootprintService> log)
{
    const int MaxFeatures = 800;
    static readonly SemaphoreSlim SeedLock = new(1, 1);
    static readonly GeometryFactory Gf =
        NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
    static readonly GeoJsonWriter GeoJsonWriter = new();

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
        if (!BuildingFootprintImportService.SupportsCity(cityId)) return empty;

        Envelope? cityEnv = null;
        Guid? seedDistrictId = null;

        if (cityId == SeedData.WarszawaId)
        {
            var district = await db.Districts.AsNoTracking()
                .Where(d => d.CityId == cityId && d.Name == BuildingFootprintImportService.WarszawaPilotDistrictName)
                .Select(d => new { d.DistrictId, d.Geom })
                .FirstOrDefaultAsync(ct);
            if (district is null) return empty;
            cityEnv = district.Geom.EnvelopeInternal;
            seedDistrictId = district.DistrictId;
        }
        else
        {
            // Wrocław: clip to union of district envelopes
            var envelopes = await db.Districts.AsNoTracking()
                .Where(d => d.CityId == cityId)
                .Select(d => d.Geom)
                .ToListAsync(ct);
            if (envelopes.Count == 0) return empty;
            cityEnv = envelopes[0].EnvelopeInternal;
            for (var i = 1; i < envelopes.Count; i++)
                cityEnv.ExpandToInclude(envelopes[i].EnvelopeInternal);
        }

        var clipMinLat = Math.Max(minLat, cityEnv.MinY);
        var clipMinLon = Math.Max(minLon, cityEnv.MinX);
        var clipMaxLat = Math.Min(maxLat, cityEnv.MaxY);
        var clipMaxLon = Math.Min(maxLon, cityEnv.MaxX);
        if (clipMinLat >= clipMaxLat || clipMinLon >= clipMaxLon) return empty;

        await EnsureImportedAsync(cityId, seedDistrictId, ct);

        var box = Gf.CreatePolygon([
            new Coordinate(clipMinLon, clipMinLat),
            new Coordinate(clipMaxLon, clipMinLat),
            new Coordinate(clipMaxLon, clipMaxLat),
            new Coordinate(clipMinLon, clipMaxLat),
            new Coordinate(clipMinLon, clipMinLat),
        ]);

        var q = db.OsmBuildingFootprints.AsNoTracking()
            .Where(f => f.CityId == cityId && f.Geom.Intersects(box));
        if (seedDistrictId is { } did)
            q = q.Where(f => f.DistrictId == did);

        var rows = await q
            .OrderBy(f => f.OsmId)
            .Take(MaxFeatures)
            .Select(f => new { f.OsmType, f.OsmId, f.Name, f.Addr, f.Geom })
            .ToListAsync(ct);

        var features = new List<object>(rows.Count);
        foreach (var r in rows)
        {
            features.Add(new
            {
                type = "Feature",
                properties = new { osmType = r.OsmType, osmId = r.OsmId, name = r.Name, addr = r.Addr },
                geometry = JsonSerializer.Deserialize<JsonElement>(GeoJsonWriter.Write(r.Geom)),
            });
        }
        return FeatureCollection(features);
    }

    async Task EnsureImportedAsync(Guid cityId, Guid? districtId, CancellationToken ct)
    {
        var any = districtId is { } did
            ? await db.OsmBuildingFootprints.AsNoTracking().AnyAsync(f => f.DistrictId == did, ct)
            : await db.OsmBuildingFootprints.AsNoTracking().AnyAsync(f => f.CityId == cityId, ct);
        if (any) return;

        await SeedLock.WaitAsync(ct);
        try
        {
            any = districtId is { } did2
                ? await db.OsmBuildingFootprints.AsNoTracking().AnyAsync(f => f.DistrictId == did2, ct)
                : await db.OsmBuildingFootprints.AsNoTracking().AnyAsync(f => f.CityId == cityId, ct);
            if (any) return;
            log.LogInformation("OsmBuildingFootprints empty for city {CityId} — importing once", cityId);
            await importer.ImportForCityAsync(cityId, ct);
        }
        finally
        {
            SeedLock.Release();
        }
    }

    static object FeatureCollection(List<object> features) =>
        new { type = "FeatureCollection", features };
}
