namespace CityChecker.Api.Auth;

/// <summary>Email allowlist for flat info / saved offers. Empty list = deny everyone.</summary>
public static class OffersAccess
{
    public static HashSet<string> ParseAllowedEmails(IConfiguration config)
    {
        var raw = config["Offers:AllowedEmails"] ?? "";
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PasswordAuth.NormalizeEmail)
            .Where(e => e.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAllowed(IConfiguration config, string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var allowed = ParseAllowedEmails(config);
        if (allowed.Count == 0) return false;
        return allowed.Contains(PasswordAuth.NormalizeEmail(email));
    }

    /// <summary>Live listing refresh is permanently disabled; pins come from DB only.</summary>
    public static bool IsUpdateOffersEnabled(IConfiguration config) => false;
}
