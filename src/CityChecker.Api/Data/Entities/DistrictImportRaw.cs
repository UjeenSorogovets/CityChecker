namespace CityChecker.Api.Data.Entities;

/// <summary>Staging row: all CSV fields stored as text before spatial transform.</summary>
public class DistrictImportRaw
{
    public long Id { get; set; }
    public string? Osiedla { get; set; }
    public string? PunktyGraniczne { get; set; }
    public string? ExtraJson { get; set; }
    public DateTime ImportedAt { get; set; }
}
