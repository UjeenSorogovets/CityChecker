using System.Text.Json;

namespace CityChecker.Api.Services;

/// <summary>
/// ponytail: ray-cast PIP on GeoJSON Polygon/MultiPolygon; ceiling = complex holes/multipolys edge cases — upgrade to NetTopologySuite if needed.
/// </summary>
public static class GeoHelper
{
    public static bool Contains(string geoJson, double lon, double lat)
    {
        using var doc = JsonDocument.Parse(geoJson);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        var coords = root.GetProperty("coordinates");

        return type switch
        {
            "Polygon" => PointInPolygon(coords[0], lon, lat),
            "MultiPolygon" => coords.EnumerateArray().Any(poly => PointInPolygon(poly[0], lon, lat)),
            _ => false
        };
    }

    static bool PointInPolygon(JsonElement ring, double x, double y)
    {
        var pts = ring.EnumerateArray()
            .Select(p => (X: p[0].GetDouble(), Y: p[1].GetDouble()))
            .ToArray();

        var inside = false;
        for (int i = 0, j = pts.Length - 1; i < pts.Length; j = i++)
        {
            var yi = pts[i].Y;
            var yj = pts[j].Y;
            var xi = pts[i].X;
            var xj = pts[j].X;
            if (((yi > y) != (yj > y)) &&
                (x < (xj - xi) * (y - yi) / (yj - yi + double.Epsilon) + xi))
                inside = !inside;
        }
        return inside;
    }

    public static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = Deg2Rad(lat2 - lat1);
        var dLon = Deg2Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(Deg2Rad(lat1)) * Math.Cos(Deg2Rad(lat2)) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    static double Deg2Rad(double d) => d * Math.PI / 180;

    public static void SelfCheck()
    {
        var square = """{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}""";
        if (!Contains(square, 1, 1) || Contains(square, 3, 1))
            throw new InvalidOperationException("GeoHelper SelfCheck failed");
    }
}
