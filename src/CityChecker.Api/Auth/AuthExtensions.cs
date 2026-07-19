using System.Security.Claims;

namespace CityChecker.Api.Auth;

public static class AuthExtensions
{
    public static string? GetGoogleUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub")
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.Claims.FirstOrDefault(c => c.Type.EndsWith("/sub", StringComparison.Ordinal))?.Value;

    public static IResult? EnsureOwner(this ClaimsPrincipal user, IConfiguration config)
    {
        var allowed = config["Google:AllowedUserId"]?.Trim();
        var sub = user.GetGoogleUserId();
        if (sub is null)
            return Results.Unauthorized();

        // Placeholder / unset — tell the user exactly what to put in .env
        if (string.IsNullOrWhiteSpace(allowed) || IsPlaceholder(allowed))
        {
            return Results.Json(new
            {
                error = "Google:AllowedUserId is not set. Put this value in GOOGLE_ALLOWED_USER_ID (or Google:AllowedUserId), then restart.",
                yourGoogleSub = sub
            }, statusCode: 403);
        }

        if (!string.Equals(sub, allowed, StringComparison.Ordinal))
        {
            return Results.Json(new
            {
                error = "This Google account is not the configured owner. Update GOOGLE_ALLOWED_USER_ID to yourGoogleSub and restart.",
                yourGoogleSub = sub
            }, statusCode: 403);
        }

        return null;
    }

    static bool IsPlaceholder(string value) =>
        value is "YOUR_GOOGLE_SUB" or "your-google-sub" or "YOUR_GOOGLE_USER_ID";
}
