using CityChecker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Data;

public static class SeedData
{
    public static readonly Guid LodzId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid KrakowId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid WarszawaId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid WroclawId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid GdanskId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    static readonly City[] SeededCities =
    [
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
        },
        new City
        {
            CityId = WroclawId,
            Name = "Wrocław",
            Voivodeship = "Dolnośląskie",
            CenterLat = 51.1079,
            CenterLon = 17.0385,
            OfficialCode = "0264"
        },
        new City
        {
            CityId = GdanskId,
            Name = "Gdańsk",
            Voivodeship = "Pomorskie",
            CenterLat = 54.3520,
            CenterLon = 18.6466,
            OfficialCode = "2261"
        }
    ];

    public static async Task EnsureSeededAsync(AppDbContext db)
    {
        // Upsert missing seeded cities (existing DBs already have Łódź/KR/WA).
        var existing = await db.Cities.Select(c => c.CityId).ToListAsync();
        var missing = SeededCities.Where(c => !existing.Contains(c.CityId)).ToList();
        if (missing.Count == 0)
            return;

        db.Cities.AddRange(missing);
        await db.SaveChangesAsync();
    }
}
