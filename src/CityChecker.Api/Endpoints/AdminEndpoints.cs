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
    }
}
