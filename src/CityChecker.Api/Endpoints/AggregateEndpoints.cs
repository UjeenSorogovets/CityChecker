using System.Security.Claims;
using CityChecker.Api.Auth;
using CityChecker.Api.Services;

namespace CityChecker.Api.Endpoints;

public static class AggregateEndpoints
{
    public static void MapAggregateEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/aggregates").RequireAuthorization();

        group.MapGet("/city/{cityId:guid}", async (Guid cityId, ClaimsPrincipal user, IConfiguration config, AggregateService aggregates) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            return Results.Ok(await aggregates.ForCityAsync(cityId));
        });

        group.MapGet("/district/{districtId:guid}", async (Guid districtId, ClaimsPrincipal user, IConfiguration config, AggregateService aggregates) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            return Results.Ok(await aggregates.ForDistrictAsync(districtId));
        });

        group.MapGet("/building/{buildingId:guid}", async (Guid buildingId, ClaimsPrincipal user, IConfiguration config, AggregateService aggregates) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            return Results.Ok(await aggregates.ForBuildingAsync(buildingId));
        });
    }
}
