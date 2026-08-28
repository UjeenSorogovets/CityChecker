namespace CityChecker.Api.Auth;

/// <summary>Admin emails may edit/delete any note. Empty list = nobody is admin.</summary>
public static class NotesAccess
{
    public static HashSet<string> ParseAdminEmails(IConfiguration config)
    {
        var raw = config["Notes:AdminEmails"] ?? "";
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(PasswordAuth.NormalizeEmail)
            .Where(e => e.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static bool IsAdmin(IConfiguration config, string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var admins = ParseAdminEmails(config);
        if (admins.Count == 0) return false;
        return admins.Contains(PasswordAuth.NormalizeEmail(email));
    }
}
