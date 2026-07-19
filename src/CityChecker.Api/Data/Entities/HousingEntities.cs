namespace CityChecker.Api.Data.Entities;

public enum DistrictPickStatus
{
    Exploring = 0,
    Shortlist = 1,
    Veto = 2,
}

public enum OfferMode
{
    Rent = 0,
    Buy = 1,
}

public class MapAnchor
{
    public Guid AnchorId { get; set; }
    public string UserId { get; set; } = "";
    public string Label { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class DistrictPick
{
    public Guid PickId { get; set; }
    public string UserId { get; set; } = "";
    public Guid DistrictId { get; set; }
    public DistrictPickStatus Status { get; set; } = DistrictPickStatus.Exploring;
    public string? VetoReason { get; set; }
    public int? QuietScore { get; set; }
    public int? ParkCount { get; set; }
    public int? ShopCount { get; set; }
    public int? PharmacyCount { get; set; }
    public int? SchoolCount { get; set; }
    public int? TransitStopCount { get; set; }
    public double? NearestHighwayKm { get; set; }
    public DateTime? ReminderAt { get; set; }
    public string? ReminderNote { get; set; }
    public string? RiskNotes { get; set; }
    public DateTime UpdatedAt { get; set; }

    public District District { get; set; } = null!;
}

public class DistrictVisit
{
    public Guid VisitId { get; set; }
    public string UserId { get; set; } = "";
    public Guid DistrictId { get; set; }
    public DateTime VisitedAt { get; set; }
    public int? EveningFeel { get; set; }
    public int? Daylight { get; set; }
    public int? DogWalk { get; set; }
    public int? SaturdayLife { get; set; }
    public int? WinterFeel { get; set; }
    public string? Notes { get; set; }

    public District District { get; set; } = null!;
}

public class HousingOffer
{
    public Guid OfferId { get; set; }
    public string UserId { get; set; } = "";
    public Guid? CityId { get; set; }
    public Guid? DistrictId { get; set; }
    public Guid? BuildingId { get; set; }
    public string Title { get; set; } = "";
    public string? Url { get; set; }
    public OfferMode Mode { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public decimal? Price { get; set; }
    public double? Sqm { get; set; }
    public int? Floor { get; set; }
    public int? Rooms { get; set; }
    public int? YearBuilt { get; set; }

    public decimal? RentOrMortgage { get; set; }
    public decimal? Media { get; set; }
    public decimal? Internet { get; set; }
    public decimal? ParkingFee { get; set; }
    public decimal? Czynsz { get; set; }

    public int? ScorePrice { get; set; }
    public int? ScoreLayout { get; set; }
    public int? ScoreLight { get; set; }
    public int? ScoreCondition { get; set; }
    public int? ScoreNeighbors { get; set; }
    public int? ScoreHeating { get; set; }
    public int? ScoreBalcony { get; set; }
    public int? ScoreElevator { get; set; }
    public int? ScoreParking { get; set; }
    public int? ScoreCellar { get; set; }
    public string? KillerFlaw { get; set; }

    public decimal? PricePerSqm { get; set; }
    public decimal? RenovationBudget { get; set; }
    public bool? HasKsiega { get; set; }
    public bool? HasSluzebnosc { get; set; }
    public bool? HasSpoldzielniaDebt { get; set; }

    public decimal? Deposit { get; set; }
    public int? NoticeDays { get; set; }
    public bool? Furnished { get; set; }
    public string? LandlordNotes { get; set; }

    public string? PhotoUrls { get; set; }
    public string? VoiceNoteUrl { get; set; }
    public bool IsFinalist { get; set; }
    public DateTime? ReminderAt { get; set; }
    public string? ReminderNote { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class DecisionProfile
{
    public Guid ProfileId { get; set; }
    public string UserId { get; set; } = "";
    public int WeightCommute { get; set; } = 40;
    public int WeightQuiet { get; set; } = 25;
    public int WeightPrice { get; set; } = 20;
    public int WeightGreen { get; set; } = 15;
    public int WeightComfort { get; set; } = 0;
}
