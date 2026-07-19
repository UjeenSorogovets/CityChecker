using System.Text.Json;
using CityChecker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Data;

public static class SeedData
{
    public static readonly Guid LodzId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid KrakowId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid WarszawaId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static readonly Guid BalutyId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1");
    public static readonly Guid GornaId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2");
    public static readonly Guid PolesieId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa3");
    public static readonly Guid SrodmiescieId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa4");
    public static readonly Guid WidzewId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa5");

    static readonly Dictionary<string, Guid> LodzDistrictIds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bałuty"] = BalutyId,
        ["Górna"] = GornaId,
        ["Polesie"] = PolesieId,
        ["Śródmieście"] = SrodmiescieId,
        ["Widzew"] = WidzewId,
    };

    public static async Task EnsureSeededAsync(AppDbContext db)
    {
        if (!await db.Cities.AnyAsync())
        {
            db.Cities.AddRange(
                new City
                {
                    CityId = LodzId,
                    Name = "Łódź",
                    Voivodeship = "Łódzkie",
                    CenterLat = 51.7592,
                    CenterLon = 19.4560,
                    OfficialCode = "1061"
                },
                new City
                {
                    CityId = KrakowId,
                    Name = "Kraków",
                    Voivodeship = "Małopolskie",
                    CenterLat = 50.0647,
                    CenterLon = 19.9450,
                    OfficialCode = "1261"
                },
                new City
                {
                    CityId = WarszawaId,
                    Name = "Warszawa",
                    Voivodeship = "Mazowieckie",
                    CenterLat = 52.2297,
                    CenterLon = 21.0122,
                    OfficialCode = "1465"
                });
            await db.SaveChangesAsync();
        }

        await SyncLodzDistrictsAsync(db);
    }

    /// <summary>
    /// Loads official OSM admin polygons for Łódź dzielnice (upsert by fixed IDs).
    /// </summary>
    public static async Task SyncLodzDistrictsAsync(AppDbContext db)
    {
        var path = ResolveSeedPath("lodz-districts.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Missing Łódź district seed GeoJSON.", path);

        using var stream = File.OpenRead(path);
        using var doc = await JsonDocument.ParseAsync(stream);
        var districts = doc.RootElement.GetProperty("districts");

        foreach (var item in districts.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString()!;
            if (!LodzDistrictIds.TryGetValue(name, out var id))
                continue;

            var officialCode = item.TryGetProperty("officialCode", out var codeEl) ? codeEl.GetString() : null;
            var geometry = item.GetProperty("geometry").GetRawText();

            var existing = await db.Districts.FirstOrDefaultAsync(d => d.DistrictId == id);
            if (existing is null)
            {
                db.Districts.Add(new District
                {
                    DistrictId = id,
                    CityId = LodzId,
                    Name = name,
                    OfficialCode = officialCode,
                    Geometry = geometry
                });
            }
            else
            {
                existing.Name = name;
                existing.OfficialCode = officialCode;
                existing.Geometry = geometry;
                existing.CityId = LodzId;
            }
        }

        await db.SaveChangesAsync();
    }

    static string ResolveSeedPath(string fileName)
    {
        // Prefer content next to the assembly (Docker / publish), then project Data/Seed for local runs.
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Data", "Seed", fileName),
            Path.Combine(AppContext.BaseDirectory, "Seed", fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Data", "Seed", fileName))
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}
