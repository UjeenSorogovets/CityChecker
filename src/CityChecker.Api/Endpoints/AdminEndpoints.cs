using System.Security.Claims;
using CityChecker.Api.Auth;
using CityChecker.Api.Services;

namespace CityChecker.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        app.MapPost("/api/admin/import/lodz-districts", async (
            ClaimsPrincipal user,
            IConfiguration config,
            LodzDistrictImportService importer,
            CancellationToken ct) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            try
            {
                var result = await importer.ImportAsync(ct);
                return Results.Ok(result);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 500);
            }
        }).RequireAuthorization();

        app.MapPost("/api/admin/refresh-environment/{cityId:guid}", async (
            Guid cityId,
            ClaimsPrincipal user,
            IConfiguration config,
            EnvironmentService env,
            CancellationToken ct) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            try
            {
                var dto = await env.GetOrComputeAsync(cityId, forceRefresh: true, ct);
                return Results.Ok(new { dto.ComputedAt, districtCount = dto.Districts.Count });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 500);
            }
        }).RequireAuthorization();

        app.MapPost("/api/admin/refresh-building-footprints/{cityId:guid}", async (
            Guid cityId,
            ClaimsPrincipal user,
            IConfiguration config,
            BuildingFootprintImportService importer,
            CancellationToken ct) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            try
            {
                var (districtId, count) = await importer.ImportForCityAsync(cityId, ct);
                return Results.Ok(new { districtId, count });
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message, statusCode: 500);
            }
        }).RequireAuthorization();
    }
}
