namespace CityChecker.Api.Data.Entities;

public class DistrictEnvironment
{
    public Guid DistrictId { get; set; }
    public int EnvRiskOverall { get; set; }
    public double? NearestLandfillKm { get; set; }
    public double? NearestRailKm { get; set; }
    public double? NearestAirportKm { get; set; }
    public double? NearestIndustrialKm { get; set; }
    public double? NearestHighwayKm { get; set; }
    public bool LandfillDownwind { get; set; }
    public DateTime ComputedAt { get; set; }

    public District District { get; set; } = null!;
}

public class CityEnvironmentSources
{
    public Guid CityId { get; set; }
    public string SourcesGeoJson { get; set; } = """{"type":"FeatureCollection","features":[]}""";
    public DateTime ComputedAt { get; set; }

    public City City { get; set; } = null!;
}
