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

    static double Deg2Rad(double d) => d * Math.PI / 180;

    public static void SelfCheck()
    {
        if (HaversineKm(51.75, 19.45, 51.75, 19.45) > 0.001)
            throw new InvalidOperationException("GeoHelper SelfCheck failed");
    }
}
