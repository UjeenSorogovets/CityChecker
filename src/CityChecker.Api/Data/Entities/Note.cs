namespace CityChecker.Api.Data.Entities;

public class Note
{
    public Guid NoteId { get; set; }
    public string AuthorGoogleId { get; set; } = "";
    public NoteLevel Level { get; set; }
    public Guid TargetCityId { get; set; }
    public Guid? TargetDistrictId { get; set; }
    public Guid? TargetBuildingId { get; set; }
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public int? RadiusMeters { get; set; }
    public string Text { get; set; } = "";
    public int ScoreOverall { get; set; }
    public int? ScoreNature { get; set; }
    public int? ScoreShops { get; set; }
    public int? ScoreTransport { get; set; }
    public int? ScoreSafety { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public City TargetCity { get; set; } = null!;
    public District? TargetDistrict { get; set; }
    public Building? TargetBuilding { get; set; }
}
