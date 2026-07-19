using System.Globalization;
using System.Text;
using System.Text.Json;
using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace CityChecker.Api.Services;

public class ImportOptions
{
    public const string Section = "Import";
    public string LodzDistrictsCsvPath { get; set; } = "DataImports/Granice osiedli.csv";
    /// <summary>
    /// ponytail: Granice osiedli.csv is address membership lists, not WKT — polygons come from this OSM-matched GeoJSON cache.
    /// </summary>
    public string LodzDistrictsPolygonsPath { get; set; } = "DataImports/lodz-osiedla-polygons.json";
}

public record ImportResult(
    int StagingRows,
    int UniqueNames,
    int Transformed,
    int Skipped,
    IReadOnlyList<string> SkipReasons,
    IReadOnlyList<string> DetectedColumns,
    string? SampleRow);

public class LodzDistrictImportService(
    AppDbContext db,
    IConfiguration config,
    ILogger<LodzDistrictImportService> logger)
{
    static readonly GeometryFactory Gf = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
    static readonly GeoJsonReader GeoJsonReader = new();

    public async Task<ImportResult> ImportAsync(CancellationToken ct = default)
    {
        var opts = config.GetSection(ImportOptions.Section).Get<ImportOptions>() ?? new ImportOptions();
        var csvPath = ResolvePath(opts.LodzDistrictsCsvPath);
        var polyPath = ResolvePath(opts.LodzDistrictsPolygonsPath);

        if (!File.Exists(csvPath))
            throw new FileNotFoundException($"CSV not found: {csvPath}");
        if (!File.Exists(polyPath))
            throw new FileNotFoundException($"Polygon cache not found: {polyPath}. Run DataImports/_fetch_osiedla_polygons.py first.");

        var city = await db.Cities.FirstOrDefaultAsync(c => c.CityId == SeedData.LodzId, ct)
                   ?? throw new InvalidOperationException("Łódź city seed missing.");

        // --- Step A: staging ---
        await db.DistrictsImportRaw.ExecuteDeleteAsync(ct);

        await using var stream = File.OpenRead(csvPath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var csvConfig = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = ";",
            BadDataFound = null,
            MissingFieldFound = null,
            HeaderValidated = null,
        };
        using var csv = new CsvReader(reader, csvConfig);
        await csv.ReadAsync();
        csv.ReadHeader();
        var columns = csv.HeaderRecord?.ToList() ?? [];
        logger.LogInformation("Detected CSV columns: {Columns}", string.Join(" | ", columns));

        var nameCol = columns.FirstOrDefault(c => c.Contains("Osiedl", StringComparison.OrdinalIgnoreCase))
                      ?? columns.FirstOrDefault();
        var pointCol = columns.FirstOrDefault(c =>
                           c.Contains("punkt", StringComparison.OrdinalIgnoreCase)
                           || c.Contains("granic", StringComparison.OrdinalIgnoreCase)
                           || c.Contains("geom", StringComparison.OrdinalIgnoreCase)
                           || c.Contains("wkt", StringComparison.OrdinalIgnoreCase)
                           || c.Contains("shape", StringComparison.OrdinalIgnoreCase))
                       ?? columns.Skip(1).FirstOrDefault();

        var staging = new List<DistrictImportRaw>();
        string? sample = null;
        while (await csv.ReadAsync())
        {
            var osiedla = nameCol is null ? null : csv.GetField(nameCol);
            var punkty = pointCol is null ? null : csv.GetField(pointCol);
            if (sample is null)
                sample = string.Join("; ", columns.Select(c => $"{c}={csv.GetField(c)}"));

            staging.Add(new DistrictImportRaw
            {
                Osiedla = osiedla?.Trim(),
                PunktyGraniczne = punkty?.Trim(),
                ImportedAt = DateTime.UtcNow
            });
        }

        // Batch save — 63k rows; clear tracker between chunks to avoid huge ChangeTracker
        const int batch = 5000;
        for (var i = 0; i < staging.Count; i += batch)
        {
            db.DistrictsImportRaw.AddRange(staging.Skip(i).Take(batch));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
        logger.LogInformation("Staged {Count} raw rows. Sample: {Sample}", staging.Count, sample);

        // Detect if any staging cell looks like WKT/GeoJSON (this portal CSV does not)
        var geomLike = staging.Take(200).Count(r => LooksLikeGeometry(r.PunktyGraniczne));
        logger.LogInformation("Geometry-like cells in first 200 rows: {N} (0 expected for address-list CSV)", geomLike);

        // --- Step B: transform ---
        var polygonsByName = LoadPolygonCache(polyPath);
        var uniqueNames = staging
            .Select(r => r.Osiedla)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n)
            .ToList();

        // Clear old Łódź districts (notes/buildings FKs set null)
        var oldIds = await db.Districts.Where(d => d.CityId == city.CityId).Select(d => d.DistrictId).ToListAsync(ct);
        if (oldIds.Count > 0)
        {
            await db.Notes.Where(n => n.TargetDistrictId != null && oldIds.Contains(n.TargetDistrictId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.TargetDistrictId, (Guid?)null), ct);
            await db.Buildings.Where(b => b.DistrictId != null && oldIds.Contains(b.DistrictId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.DistrictId, (Guid?)null), ct);
            await db.Districts.Where(d => d.CityId == city.CityId).ExecuteDeleteAsync(ct);
        }

        var skipReasons = new List<string>();
        var transformed = 0;
        var skipped = 0;
        var now = DateTime.UtcNow;

        foreach (var name in uniqueNames!)
        {
            if (!polygonsByName.TryGetValue(name!, out var geomJson))
            {
                skipped++;
                skipReasons.Add($"No polygon cache for '{name}'");
                continue;
            }

            Geometry geom;
            try
            {
                geom = GeoJsonReader.Read<Geometry>(geomJson);
            }
            catch (Exception ex)
            {
                skipped++;
                skipReasons.Add($"GeoJSON parse failed for '{name}': {ex.Message}");
                continue;
            }

            var multi = ToMultiPolygon(geom);
            if (multi is null)
            {
                skipped++;
                skipReasons.Add($"Not a polygon for '{name}' ({geom.GeometryType})");
                continue;
            }

            multi.SRID = 4326;
            if (!multi.IsValid)
            {
                // ponytail: NTS Buffer(0) as MakeValid substitute; PostGIS ST_MakeValid available via raw SQL if needed
                multi = ToMultiPolygon(multi.Buffer(0)) ?? multi;
            }

            var areaKm2 = multi.Area > 0
                ? // approximate km² from deg² near Łódź (~51.75N)
                  multi.Area * Math.Pow(111.32 * Math.Cos(51.75 * Math.PI / 180), 2)
                : (double?)null;

            db.Districts.Add(new District
            {
                DistrictId = Guid.NewGuid(),
                CityId = city.CityId,
                Name = name!,
                SourceName = "Granice osiedli.csv + OSM admin boundary",
                OfficialCode = null,
                Geom = multi,
                AreaKm2 = areaKm2 is null ? null : Math.Round(areaKm2.Value, 3),
                CreatedAt = now,
                UpdatedAt = now
            });
            transformed++;
        }

        await db.SaveChangesAsync(ct);

        // Staging is only for the import run — clear afterward (polygons live in Districts)
        await db.DistrictsImportRaw.ExecuteDeleteAsync(ct);

        logger.LogInformation(
            "Import done. staging={Staging} unique={Unique} transformed={Ok} skipped={Skip}",
            staging.Count, uniqueNames.Count, transformed, skipped);

        return new ImportResult(
            staging.Count,
            uniqueNames.Count,
            transformed,
            skipped,
            skipReasons.Take(50).ToList(),
            columns,
            sample);
    }

    static Dictionary<string, string> LoadPolygonCache(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var d in doc.RootElement.GetProperty("districts").EnumerateArray())
        {
            var name = d.GetProperty("name").GetString()!;
            map[name] = d.GetProperty("geometry").GetRawText();
        }
        return map;
    }

    static MultiPolygon? ToMultiPolygon(Geometry g) => g switch
    {
        MultiPolygon mp => mp,
        Polygon p => Gf.CreateMultiPolygon([p]),
        _ => null
    };

    static bool LooksLikeGeometry(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        var u = v.TrimStart().ToUpperInvariant();
        return u.StartsWith("POLYGON") || u.StartsWith("MULTIPOLYGON") || u.StartsWith("SRID=")
               || u.StartsWith("{") && u.Contains("COORDINATES");
    }

    static string ResolvePath(string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute) && File.Exists(relativeOrAbsolute))
            return relativeOrAbsolute;

        var candidates = new[]
        {
            Path.GetFullPath(relativeOrAbsolute),
            Path.Combine(AppContext.BaseDirectory, relativeOrAbsolute),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativeOrAbsolute),
            Path.Combine(Directory.GetCurrentDirectory(), relativeOrAbsolute),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists)
               ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, relativeOrAbsolute));
    }
}
