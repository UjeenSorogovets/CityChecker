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
        var byCity = app.MapGroup("/api/cities/{cityId:guid}/districts").RequireAuthorization();

        byCity.MapGet("/", async (Guid cityId, ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            var districts = await db.Districts.AsNoTracking()
                .Where(d => d.CityId == cityId)
                .OrderBy(d => d.Name)
                .Select(d => new DistrictListDto(
                    d.DistrictId, d.CityId, d.Name, d.OfficialCode, d.SourceName, d.AreaKm2))
                .ToListAsync();
            return Results.Ok(districts);
        });

        byCity.MapGet("/geojson", async (Guid cityId, ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;

            // PostGIS ST_AsGeoJSON — Leaflet-ready FeatureCollection
            await using var conn = db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT COALESCE(json_build_object(
                    'type', 'FeatureCollection',
                    'features', COALESCE((
                        SELECT json_agg(
                            json_build_object(
                                'type', 'Feature',
                                'geometry', ST_AsGeoJSON(d."Geom")::json,
                                'properties', json_build_object(
                                    'id', d."DistrictId",
                                    'name', d."Name",
                                    'officialCode', d."OfficialCode",
                                    'sourceName', d."SourceName",
                                    'areaKm2', d."AreaKm2",
                                    'cityId', d."CityId"
                                )
                            )
                            ORDER BY d."Name"
                        )
                        FROM "Districts" d
                        WHERE d."CityId" = @cityId
                    ), '[]'::json)
                ), '{"type":"FeatureCollection","features":[]}'::json)::text
                """;
            var p = cmd.CreateParameter();
            p.ParameterName = "cityId";
            p.Value = cityId;
            cmd.Parameters.Add(p);

            var json = (string?)(await cmd.ExecuteScalarAsync()) ?? """{"type":"FeatureCollection","features":[]}""";
            return Results.Content(json, "application/geo+json");
        });

        app.MapGet("/api/districts/{districtId:guid}", async (
            Guid districtId, ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            var d = await db.Districts.AsNoTracking()
                .Where(x => x.DistrictId == districtId)
                .Select(x => new DistrictDetailDto(
                    x.DistrictId, x.CityId, x.Name, x.OfficialCode, x.SourceName, x.AreaKm2, x.CreatedAt, x.UpdatedAt))
                .FirstOrDefaultAsync();
            return d is null ? Results.NotFound() : Results.Ok(d);
        }).RequireAuthorization();
    }
}
