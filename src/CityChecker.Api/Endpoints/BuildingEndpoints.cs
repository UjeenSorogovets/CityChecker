using System.Security.Claims;
using CityChecker.Api.Auth;
using CityChecker.Api.Data;
using CityChecker.Api.Dtos;
using CityChecker.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Endpoints;

public static class BuildingEndpoints
{
    public static void MapBuildingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cities/{cityId:guid}/buildings", async (
            Guid cityId,
            ClaimsPrincipal user,
            IConfiguration config,
            AppDbContext db,
            double? minLat,
            double? minLon,
            double? maxLat,
            double? maxLon) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;

            var q = db.Buildings.AsNoTracking().Where(b => b.CityId == cityId);
            if (minLat is not null && minLon is not null && maxLat is not null && maxLon is not null)
            {
                q = q.Where(b => b.Lat >= minLat && b.Lat <= maxLat && b.Lon >= minLon && b.Lon <= maxLon);
            }

            var list = await q
                .OrderBy(b => b.AddressLine)
                .Select(b => new BuildingDto(b.BuildingId, b.CityId, b.DistrictId, b.AddressLine, b.Lat, b.Lon))
                .Take(500)
                .ToListAsync();
            return Results.Ok(list);
        }).RequireAuthorization();

        app.MapPost("/api/buildings/reverse-geocode", async (
            ReverseGeocodeRequest body,
            ClaimsPrincipal user,
            IConfiguration config,
            BuildingService buildings) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            if (body.Lat is < 49 or > 55 || body.Lon is < 14 or > 25)
                return Results.BadRequest(new { error = "Coordinates must be within Poland bounds." });

            var (building, error) = await buildings.ReverseGeocodeAsync(body.Lat, body.Lon);
            return building is null
                ? Results.NotFound(new { error = error ?? "Could not create building at this location." })
                : Results.Ok(building);
        }).RequireAuthorization();
    }
}
