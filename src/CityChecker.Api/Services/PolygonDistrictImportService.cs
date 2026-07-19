using System.Text.Json;
using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace CityChecker.Api.Services;

/// <summary>Import district polygons from a GeoJSON cache file for any seeded city.</summary>
public class PolygonDistrictImportService(AppDbContext db, ILogger<PolygonDistrictImportService> logger)
{
    static readonly GeometryFactory Gf = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
    static readonly GeoJsonReader GeoJsonReader = new();

    public async Task<int> ImportForCityAsync(Guid cityId, string polygonsJsonPath, string sourceName, CancellationToken ct = default)
    {
        var path = ResolvePath(polygonsJsonPath);
        if (!File.Exists(path))
        {
            logger.LogWarning("Polygon cache missing: {Path}", path);
            return 0;
        }

        if (!await db.Cities.AnyAsync(c => c.CityId == cityId, ct))
            throw new InvalidOperationException($"City {cityId} not found");

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        var districts = doc.RootElement.GetProperty("districts");

        var existingCount = await db.Districts.CountAsync(d => d.CityId == cityId, ct);
        if (existingCount > 0)
        {
            logger.LogInformation("City {CityId} already has {Count} districts — skip polygon import", cityId, existingCount);
            return 0;
        }

        var now = DateTime.UtcNow;
        var added = 0;
        foreach (var item in districts.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            var geomJson = item.GetProperty("geometry").GetRawText();
            Geometry geom;
            try { geom = GeoJsonReader.Read<Geometry>(geomJson); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Skip {Name}", name);
                continue;
            }

            var multi = geom switch
            {
                MultiPolygon mp => mp,
                Polygon p => Gf.CreateMultiPolygon([p]),
                _ => null
            };
            if (multi is null) continue;
            multi.SRID = 4326;
            if (!multi.IsValid)
                multi = (multi.Buffer(0) as MultiPolygon) ?? ToMulti(multi.Buffer(0)) ?? multi;

            var areaKm2 = multi.Area * Math.Pow(111.32 * Math.Cos(52.0 * Math.PI / 180), 2);

            db.Districts.Add(new District
            {
                DistrictId = Guid.NewGuid(),
                CityId = cityId,
                Name = name,
                SourceName = sourceName,
                Geom = multi,
                AreaKm2 = Math.Round(areaKm2, 3),
                CreatedAt = now,
                UpdatedAt = now
            });
            added++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Imported {Added} districts for city {CityId} from {Path}", added, cityId, path);
        return added;
    }

    static MultiPolygon? ToMulti(Geometry g) => g switch
    {
        MultiPolygon mp => mp,
        Polygon p => Gf.CreateMultiPolygon([p]),
        _ => null
    };

    static string ResolvePath(string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute) && File.Exists(relativeOrAbsolute))
            return relativeOrAbsolute;
        var candidates = new[]
        {
            Path.GetFullPath(relativeOrAbsolute),
            Path.Combine(AppContext.BaseDirectory, relativeOrAbsolute),
            Path.Combine(Directory.GetCurrentDirectory(), relativeOrAbsolute),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)
               ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativeOrAbsolute));
    }
}
