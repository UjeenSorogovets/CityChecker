using CityChecker.Api.Auth;
using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/register", async (AuthBody body, AppDbContext db, IConfiguration config) =>
        {
            if (!PasswordAuth.LooksLikeEmail(body.Email))
                return Results.BadRequest(new { error = "Enter a valid email." });
            if (string.IsNullOrEmpty(body.Password) || body.Password.Length < 8)
                return Results.BadRequest(new { error = "Password must be at least 8 characters." });

            var email = PasswordAuth.NormalizeEmail(body.Email!);
            if (await db.Users.AnyAsync(u => u.Email == email))
                return Results.Conflict(new { error = "An account with this email already exists." });

            var user = new AppUser
            {
                UserId = Guid.NewGuid(),
                Email = email,
                PasswordHash = PasswordAuth.HashPassword(body.Password),
                CreatedAt = DateTime.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Ok(new { token = PasswordAuth.IssueToken(config, user.UserId) });
        });

        app.MapPost("/api/auth/login", async (AuthBody body, AppDbContext db, IConfiguration config) =>
        {
            if (string.IsNullOrWhiteSpace(body.Email) || string.IsNullOrEmpty(body.Password))
                return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);

            var email = PasswordAuth.NormalizeEmail(body.Email);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email);
            if (user is null || !PasswordAuth.VerifyPassword(body.Password, user.PasswordHash))
                return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);

            return Results.Ok(new { token = PasswordAuth.IssueToken(config, user.UserId) });
        });
    }

    public record AuthBody(string? Email, string? Password);
}
