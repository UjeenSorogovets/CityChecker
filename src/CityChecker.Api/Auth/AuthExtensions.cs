using System.Security.Claims;

using CityChecker.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Auth;

public static class AuthExtensions
{
    public static string? GetUserId(this ClaimsPrincipal user) =>
        user.FindFirstValue("sub")
        ?? user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.Claims.FirstOrDefault(c => c.Type.EndsWith("/sub", StringComparison.Ordinal))?.Value;

    public static string? GetUserEmail(this ClaimsPrincipal user) =>
        user.FindFirstValue("email");

    public static async Task<string?> ResolveUserEmailAsync(this ClaimsPrincipal user, AppDbContext db)
    {
        var fromClaim = user.GetUserEmail();
        if (!string.IsNullOrWhiteSpace(fromClaim))
            return PasswordAuth.NormalizeEmail(fromClaim);

        var sub = user.GetUserId();
        if (sub is null || !Guid.TryParse(sub, out var userId))
            return null;

        return await db.Users.AsNoTracking()
            .Where(u => u.UserId == userId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync();
    }

    // Legacy name — note authoring stores this claim in AuthorGoogleId.
    public static string? GetGoogleUserId(this ClaimsPrincipal user) => user.GetUserId();

    // Any signed-in account (email/password or Google). Name kept so existing endpoints stay untouched.
    public static IResult? EnsureOwner(this ClaimsPrincipal user, IConfiguration config)
    {
        _ = config;
        return user.GetUserId() is null ? Results.Unauthorized() : null;
    }

    public static async Task<IResult?> EnsureOffersAccessAsync(
        this ClaimsPrincipal user, AppDbContext db, IConfiguration config)
    {
        if (user.GetUserId() is null) return Results.Unauthorized();
        var email = await user.ResolveUserEmailAsync(db);
        if (!OffersAccess.IsAllowed(config, email))
            return Results.Json(new { error = "Offers are not available for this account." }, statusCode: 403);
        return null;
    }
}
