using System.Text.Json;
using CityChecker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Data;

public static class SeedData
{
    public static readonly Guid LodzId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid KrakowId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid WarszawaId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static async Task EnsureSeededAsync(AppDbContext db)
    {
        if (await db.Cities.AnyAsync())
            return;

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
}
