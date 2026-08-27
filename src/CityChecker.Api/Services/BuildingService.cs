using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using CityChecker.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace CityChecker.Api.Services;

public class BuildingService(AppDbContext db, NominatimClient nominatim)
{
    static readonly GeometryFactory Gf = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    public async Task<(BuildingDto? Building, string? Error)> ReverseGeocodeAsync(double lat, double lon, CancellationToken ct = default)
    {
        var cities = await db.Cities.AsNoTracking().ToListAsync(ct);
        var geo = await nominatim.ReverseAsync(lat, lon, ct);

        // Always resolve city by nearest seeded center if Nominatim fails or city name missing
        var city = MatchCity(cities, geo?.CityName, lat, lon);
        if (city is null)
            return (null, "Outside known cities (Łódź / Kraków / Warszawa / Wrocław / Gdańsk area).");

        var addressLine = !string.IsNullOrWhiteSpace(geo?.AddressLine) && geo.AddressLine != "Unknown street"
            ? geo.AddressLine.Trim()
            : $"Point {lat:F5}, {lon:F5}";

        var normalized = NormalizeAddress(addressLine);
        var existing = await db.Buildings
            .FirstOrDefaultAsync(b => b.CityId == city.CityId && b.AddressLine.ToLower() == normalized, ct);
        if (existing is not null)
            return (ToDto(existing), null);

        // Near-duplicate by coordinates (~11m grid)
        var near = await db.Buildings.FirstOrDefaultAsync(b =>
            b.CityId == city.CityId &&
            Math.Abs(b.Lat - lat) < 0.0001 && Math.Abs(b.Lon - lon) < 0.0001, ct);
        if (near is not null)
            return (ToDto(near), null);

        var districtId = await FindDistrictIdAsync(city.CityId, lon, lat, ct);

        var building = new Building
        {
            BuildingId = Guid.NewGuid(),
            CityId = city.CityId,
            DistrictId = districtId,
            AddressLine = addressLine,
            Lat = lat,
            Lon = lon
        };
        db.Buildings.Add(building);
        await db.SaveChangesAsync(ct);
        return (ToDto(building), null);
    }

    async Task<Guid?> FindDistrictIdAsync(Guid cityId, double lon, double lat, CancellationToken ct)
    {
        // ponytail: use EF spatial (ST_Contains) — never dispose GetDbConnection() (kills SaveChanges).
        var point = Gf.CreatePoint(new Coordinate(lon, lat));
        return await db.Districts.AsNoTracking()
            .Where(d => d.CityId == cityId && d.Geom.Contains(point))
            .Select(d => (Guid?)d.DistrictId)
            .FirstOrDefaultAsync(ct);
    }

    static City? MatchCity(List<City> cities, string? name, double lat, double lon)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = cities.FirstOrDefault(c =>
                c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(c.Name, StringComparison.OrdinalIgnoreCase) ||
                c.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
                return byName;
        }

        return cities
            .Select(c => (City: c, Dist: GeoHelper.HaversineKm(lat, lon, c.CenterLat, c.CenterLon)))
            .Where(x => x.Dist < 40)
            .OrderBy(x => x.Dist)
            .Select(x => x.City)
            .FirstOrDefault();
    }

    static string NormalizeAddress(string address) => address.Trim().ToLowerInvariant();

    public static BuildingDto ToDto(Building b) =>
        new(b.BuildingId, b.CityId, b.DistrictId, b.AddressLine, b.Lat, b.Lon);
}
