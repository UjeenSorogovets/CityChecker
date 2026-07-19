namespace CityChecker.Api.Data.Entities;

public class District
{
    public Guid DistrictId { get; set; }
    public Guid CityId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>GeoJSON Polygon or MultiPolygon string.</summary>
    public string Geometry { get; set; } = "";
    public string? OfficialCode { get; set; }

    public City City { get; set; } = null!;
    public List<Building> Buildings { get; set; } = [];
}
