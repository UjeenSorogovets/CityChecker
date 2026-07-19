using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;

namespace CityChecker.Api.Auth;

// ponytail: email/password accounts in DB + HMAC JWT. No ASP.NET Identity stack.
// Ceiling: no email verify / reset. Upgrade: Identity or external IdP when you have a domain.
public static class PasswordAuth
{
    public const string Scheme = "Local";
    public const string Issuer = "citychecker-local";

    static string? _ephemeralSecret;

    public static bool LooksLikeEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email)
        && email.Length <= 320
        && Regex.IsMatch(email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts is not ["pbkdf2", var iterStr, var saltB64, var hashB64])
            return false;
        if (!int.TryParse(iterStr, out var iterations))
            return false;

        var salt = Convert.FromBase64String(saltB64);
        var expected = Convert.FromBase64String(hashB64);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string IssueToken(IConfiguration config, Guid userId, TimeSpan? lifetime = null)
    {
        var key = new SymmetricSecurityKey(SigningKeyBytes(config));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Issuer,
            claims: [new Claim("sub", userId.ToString("D"))],
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromDays(30)),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public static TokenValidationParameters ValidationParameters(IConfiguration config) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer,
        ValidateAudience = true,
        ValidAudience = Issuer,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(SigningKeyBytes(config)),
        NameClaimType = "sub",
    };

    public static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    public static void SelfCheck()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:JwtSecret"] = "unit-test-secret-at-least-32-chars!!",
            })
            .Build();

        var hash = HashPassword("test-pass-123");
        if (!VerifyPassword("test-pass-123", hash))
            throw new InvalidOperationException("PasswordAuth SelfCheck: hash verify failed");
        if (VerifyPassword("wrong", hash))
            throw new InvalidOperationException("PasswordAuth SelfCheck: bad password accepted");

        var userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var jwt = IssueToken(config, userId, TimeSpan.FromMinutes(5));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var principal = handler.ValidateToken(jwt, ValidationParameters(config), out _);
        var sub = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sub != userId.ToString("D"))
            throw new InvalidOperationException($"PasswordAuth SelfCheck: unexpected sub '{sub}'");
    }

    static byte[] SigningKeyBytes(IConfiguration config)
    {
        var secret = config["Auth:JwtSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            // Tokens die on restart if AUTH_JWT_SECRET unset — fine for a personal VPS until you set one.
            _ephemeralSecret ??= Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            secret = _ephemeralSecret;
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }
}
