namespace CityChecker.Api.Data.Entities;

public class City
{
    public Guid CityId { get; set; }
    public string Name { get; set; } = "";
    public string Voivodeship { get; set; } = "";
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
    public string? OfficialCode { get; set; }

    public List<District> Districts { get; set; } = [];
    public List<Building> Buildings { get; set; } = [];
}
