namespace CityChecker.Api.Data.Entities;

/// <summary>Shared Otodom filter snapshot (regenerable cache, not personal offers).</summary>
public class OtodomPinSet
{
    public Guid PinSetId { get; set; }
    public Guid CityId { get; set; }
    /// <summary>SELL or RENT.</summary>
    public string Transaction { get; set; } = "SELL";
    public int PriceMax { get; set; }
    public int AreaMin { get; set; }
    /// <summary>Sorted comma-joined Otodom room codes, e.g. FOUR,FIVE,SIX_OR_MORE,THREE,TWO.</summary>
    public string RoomsKey { get; set; } = "";
    public int TotalMatched { get; set; }
    public int Listed { get; set; }
    public DateTime? FetchedAt { get; set; }
    /// <summary>Ready | Refreshing | Failed | Missing (Missing is API-only; not stored).</summary>
    public string Status { get; set; } = "Ready";
    public string? LastError { get; set; }

    public City City { get; set; } = null!;
    public List<OtodomPin> Pins { get; set; } = [];
}

public class OtodomPin
{
    public Guid PinId { get; set; }
    public Guid PinSetId { get; set; }
    public long ExternalId { get; set; }
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double? Price { get; set; }
    public double? AreaM2 { get; set; }
    public string? Rooms { get; set; }
    public string Url { get; set; } = "";

    public OtodomPinSet PinSet { get; set; } = null!;
}
