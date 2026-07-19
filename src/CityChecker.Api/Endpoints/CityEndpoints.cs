using System.Security.Claims;
using System.Text.Json;
using CityChecker.Api.Auth;
using CityChecker.Api.Data;
using CityChecker.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Endpoints;

public static class CityEndpoints
{
    public static void MapCityEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/cities").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            var cities = await db.Cities.AsNoTracking()
                .OrderBy(c => c.Name)
                .Select(c => new CityDto(c.CityId, c.Name, c.Voivodeship, c.CenterLat, c.CenterLon, c.OfficialCode))
                .ToListAsync();
            return Results.Ok(cities);
        });

        group.MapGet("/{cityId:guid}", async (Guid cityId, ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            var city = await db.Cities.AsNoTracking()
                .Where(c => c.CityId == cityId)
                .Select(c => new CityDto(c.CityId, c.Name, c.Voivodeship, c.CenterLat, c.CenterLon, c.OfficialCode))
                .FirstOrDefaultAsync();
            return city is null ? Results.NotFound() : Results.Ok(city);
        });
    }
}

public static class DistrictEndpoints
{
    public static void MapDistrictEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/cities/{cityId:guid}/districts").RequireAuthorization();

        group.MapGet("/", async (Guid cityId, ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            var districts = await db.Districts.AsNoTracking()
                .Where(d => d.CityId == cityId)
                .OrderBy(d => d.Name)
                .ToListAsync();

            var dtos = districts.Select(d =>
            {
                object geometry;
                try { geometry = JsonSerializer.Deserialize<object>(d.Geometry) ?? d.Geometry; }
                catch { geometry = d.Geometry; }
                return new DistrictDto(d.DistrictId, d.CityId, d.Name, geometry, d.OfficialCode);
            });
            return Results.Ok(dtos);
        });
    }
}
