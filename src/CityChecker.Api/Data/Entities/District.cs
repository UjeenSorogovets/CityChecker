using NetTopologySuite.Geometries;

namespace CityChecker.Api.Data.Entities;

public class District
{
    public Guid DistrictId { get; set; }
    public Guid CityId { get; set; }
    public string Name { get; set; } = "";
    public string? OfficialCode { get; set; }
    public string? SourceName { get; set; }
    public MultiPolygon Geom { get; set; } = null!;
    public double? AreaKm2 { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public City City { get; set; } = null!;
    public List<Building> Buildings { get; set; } = [];
}
