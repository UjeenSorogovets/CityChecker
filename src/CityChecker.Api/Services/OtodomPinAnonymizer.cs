namespace CityChecker.Api.Services;

/// <summary>Strip identifying Otodom listing fields before cache/API exposure.</summary>
public static class OtodomPinAnonymizer
{
    public const string GenericTitle = "Offer";

    public static string PlaceholderSlug(long externalId) => $"pin-{externalId}";

    public static string? FormatRooms(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (int.TryParse(s, out var n) && n > 0) return n.ToString();
        return s.ToUpperInvariant() switch
        {
            "ONE" => "1",
            "TWO" => "2",
            "THREE" => "3",
            "FOUR" => "4",
            "FIVE" => "5",
            "SIX" or "SIX_OR_MORE" => "6+",
            _ => s,
        };
    }

    public static OtodomPinDto ToDto(
        long id,
        double lat,
        double lon,
        double? price,
        double? areaM2,
        string? rooms,
        string? transaction) =>
        new(id, lat, lon, price, areaM2, FormatRooms(rooms), transaction);

    public static void SelfCheck()
    {
        if (FormatRooms("TWO") != "2" || FormatRooms("SIX_OR_MORE") != "6+")
            throw new InvalidOperationException("OtodomPinAnonymizer.SelfCheck: room format failed");
        if (PlaceholderSlug(123) != "pin-123")
            throw new InvalidOperationException("OtodomPinAnonymizer.SelfCheck: slug failed");
    }
}
