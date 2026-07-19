using System.Security.Claims;

namespace CityChecker.Api.Auth;

public static class AuthExtensions
{
    public static string? GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub")
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.Claims.FirstOrDefault(c => c.Type.EndsWith("/sub", StringComparison.Ordinal))?.Value;

    // Legacy name — note authoring stores this claim in AuthorGoogleId.
    public static string? GetGoogleUserId(this ClaimsPrincipal user) => user.GetUserId();

    // Any signed-in account (email/password or Google). Name kept so existing endpoints stay untouched.
    public static IResult? EnsureOwner(this ClaimsPrincipal user, IConfiguration config)
    {
        _ = config;
        return user.GetUserId() is null ? Results.Unauthorized() : null;
    }
}
