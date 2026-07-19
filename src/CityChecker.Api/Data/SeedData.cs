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

        // ponytail: approximate dzielnice boxes — good enough for MVP coloring/PIP; replace with OSM admin boundaries later
        db.Districts.AddRange(
            new District
            {
                DistrictId = BalutyId,
                CityId = LodzId,
                Name = "Bałuty",
                OfficialCode = "106101",
                Geometry = Poly(
                    (19.35, 51.78), (19.52, 51.78), (19.52, 51.86), (19.35, 51.86))
            },
            new District
            {
                DistrictId = GornaId,
                CityId = LodzId,
                Name = "Górna",
                OfficialCode = "106102",
                Geometry = Poly(
                    (19.38, 51.68), (19.52, 51.68), (19.52, 51.75), (19.38, 51.75))
            },
            new District
            {
                DistrictId = PolesieId,
                CityId = LodzId,
                Name = "Polesie",
                OfficialCode = "106103",
                Geometry = Poly(
                    (19.32, 51.74), (19.42, 51.74), (19.42, 51.80), (19.32, 51.80))
            },
            new District
            {
                DistrictId = SrodmiescieId,
                CityId = LodzId,
                Name = "Śródmieście",
                OfficialCode = "106104",
                Geometry = Poly(
                    (19.42, 51.74), (19.48, 51.74), (19.48, 51.78), (19.42, 51.78))
            },
            new District
            {
                DistrictId = WidzewId,
                CityId = LodzId,
                Name = "Widzew",
                OfficialCode = "106105",
                Geometry = Poly(
                    (19.48, 51.72), (19.62, 51.72), (19.62, 51.80), (19.48, 51.80))
            });

        await db.SaveChangesAsync();
    }

    static string Poly(params (double Lon, double Lat)[] ring)
    {
        var coords = ring.Select(p => new[] { p.Lon, p.Lat }).ToList();
        coords.Add([ring[0].Lon, ring[0].Lat]);
        return JsonSerializer.Serialize(new
        {
            type = "Polygon",
            coordinates = new[] { coords }
        });
    }
}
