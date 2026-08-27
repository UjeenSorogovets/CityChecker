using NetTopologySuite.Geometries;

namespace CityChecker.Api.Data.Entities;

/// <summary>OSM building footprint cache (map click layer). Separate from user <see cref="Building"/> notes.</summary>
public class OsmBuildingFootprint
{
    public Guid OsmBuildingFootprintId { get; set; }
    public Guid CityId { get; set; }
    public Guid DistrictId { get; set; }
    public string OsmType { get; set; } = "";
    public long OsmId { get; set; }
    public string? Name { get; set; }
    public string? Addr { get; set; }
    public MultiPolygon Geom { get; set; } = null!;
    public DateTime ImportedAt { get; set; }

    public City City { get; set; } = null!;
    public District District { get; set; } = null!;
}
