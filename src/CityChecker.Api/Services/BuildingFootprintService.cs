using System.Text.Json;
using CityChecker.Api.Data;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace CityChecker.Api.Services;

// ponytail: serve Wołomin footprints from PostGIS; Overpass only via import/seed.
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

        var district = await db.Districts.AsNoTracking()
            .Where(d => d.CityId == cityId && d.Name == BuildingFootprintImportService.PilotDistrictName)
            .Select(d => new { d.DistrictId, d.Geom })
            .FirstOrDefaultAsync(ct);
        if (district is null) return empty;

        var env = district.Geom.EnvelopeInternal;
        var clipMinLat = Math.Max(minLat, env.MinY);
        var clipMinLon = Math.Max(minLon, env.MinX);
        var clipMaxLat = Math.Min(maxLat, env.MaxY);
        var clipMaxLon = Math.Min(maxLon, env.MaxX);
        if (clipMinLat >= clipMaxLat || clipMinLon >= clipMaxLon) return empty;

        await EnsureImportedAsync(cityId, district.DistrictId, ct);

        var box = Gf.CreatePolygon([
            new Coordinate(clipMinLon, clipMinLat),
            new Coordinate(clipMaxLon, clipMinLat),
            new Coordinate(clipMaxLon, clipMaxLat),
            new Coordinate(clipMinLon, clipMaxLat),
            new Coordinate(clipMinLon, clipMinLat),
        ]);

        var rows = await db.OsmBuildingFootprints.AsNoTracking()
            .Where(f => f.DistrictId == district.DistrictId && f.Geom.Intersects(box))
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

    async Task EnsureImportedAsync(Guid cityId, Guid districtId, CancellationToken ct)
    {
        var any = await db.OsmBuildingFootprints.AsNoTracking()
            .AnyAsync(f => f.DistrictId == districtId, ct);
        if (any) return;

        await SeedLock.WaitAsync(ct);
        try
        {
            any = await db.OsmBuildingFootprints.AsNoTracking()
                .AnyAsync(f => f.DistrictId == districtId, ct);
            if (any) return;
            log.LogInformation("OsmBuildingFootprints empty for Wołomin — importing once");
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
