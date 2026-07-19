using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using CityChecker.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace CityChecker.Api.Services;

public class BuildingService(AppDbContext db, NominatimClient nominatim)
{
    static readonly GeometryFactory Gf = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);

    public async Task<BuildingDto?> ReverseGeocodeAsync(double lat, double lon, CancellationToken ct = default)
    {
        var geo = await nominatim.ReverseAsync(lat, lon, ct);
        if (geo is null)
            return null;

        var cities = await db.Cities.AsNoTracking().ToListAsync(ct);
        var city = MatchCity(cities, geo.CityName, lat, lon);
        if (city is null)
            return null;

        var normalized = NormalizeAddress(geo.AddressLine);
        var existing = await db.Buildings
            .FirstOrDefaultAsync(b => b.CityId == city.CityId && b.AddressLine.ToLower() == normalized, ct);

        if (existing is not null)
            return ToDto(existing);

        var point = Gf.CreatePoint(new Coordinate(lon, lat));
        var districts = await db.Districts.AsNoTracking()
            .Where(d => d.CityId == city.CityId)
            .Select(d => new { d.DistrictId, d.Geom })
            .ToListAsync(ct);

        Guid? districtId = districts.FirstOrDefault(d => d.Geom.Contains(point))?.DistrictId;

        var building = new Building
        {
            BuildingId = Guid.NewGuid(),
            CityId = city.CityId,
            DistrictId = districtId,
            AddressLine = geo.AddressLine.Trim(),
            Lat = lat,
            Lon = lon
        };
        db.Buildings.Add(building);
        await db.SaveChangesAsync(ct);
        return ToDto(building);
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
