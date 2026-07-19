namespace CityChecker.Api.Data.Entities;

public class Building
{
    public Guid BuildingId { get; set; }
    public Guid CityId { get; set; }
    public Guid? DistrictId { get; set; }
    public string AddressLine { get; set; } = "";
    public double Lat { get; set; }
    public double Lon { get; set; }

    public City City { get; set; } = null!;
    public District? District { get; set; }
}
