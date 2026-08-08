namespace CityChecker.Api.Services;

public static class GeoHelper
{
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

    /// <summary>Initial bearing from (lat1,lon1) to (lat2,lon2) in degrees [0, 360).</summary>
    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var φ1 = Deg2Rad(lat1);
        var φ2 = Deg2Rad(lat2);
        var Δλ = Deg2Rad(lon2 - lon1);
        var y = Math.Sin(Δλ) * Math.Cos(φ2);
        var x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ);
        var θ = Math.Atan2(y, x);
        return (θ * 180 / Math.PI + 360) % 360;
    }

    /// <summary>8-point compass sector for a bearing in degrees.</summary>
    public static string Sector8(double bearingDegrees)
    {
        var idx = (int)Math.Floor((bearingDegrees + 22.5) / 45.0) % 8;
        return idx switch
        {
            0 => "N",
            1 => "NE",
            2 => "E",
            3 => "SE",
            4 => "S",
            5 => "SW",
            6 => "W",
            _ => "NW",
        };
    }

    static double Deg2Rad(double d) => d * Math.PI / 180;

    public static void SelfCheck()
    {
        if (HaversineKm(51.75, 19.45, 51.75, 19.45) > 0.001)
            throw new InvalidOperationException("GeoHelper SelfCheck failed");
        if (Sector8(0) != "N" || Sector8(90) != "E" || Sector8(225) != "SW")
            throw new InvalidOperationException("GeoHelper Sector8 SelfCheck failed");
        var b = BearingDegrees(51.75, 19.45, 51.75, 20.45);
        if (b is < 80 or > 100)
            throw new InvalidOperationException("GeoHelper Bearing SelfCheck failed");
    }
}
