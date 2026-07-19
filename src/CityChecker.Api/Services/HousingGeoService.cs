using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CityChecker.Api.Services;

// ponytail: public OSRM demo + Overpass; fine for personal use, not for heavy traffic.
public class HousingGeoService(HttpClient http, ILogger<HousingGeoService> log)
{
    public async Task<(double? Minutes, double? Km)> DriveAsync(double fromLat, double fromLon, double toLat, double toLon, CancellationToken ct = default)
    {
        try
        {
            var url =
                $"https://router.project-osrm.org/route/v1/driving/{fromLon.ToString(CultureInfo.InvariantCulture)},{fromLat.ToString(CultureInfo.InvariantCulture)};{toLon.ToString(CultureInfo.InvariantCulture)},{toLat.ToString(CultureInfo.InvariantCulture)}?overview=false";
            using var res = await http.GetAsync(url, ct);
            if (!res.IsSuccessStatusCode) return (null, null);
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var route = doc.RootElement.GetProperty("routes")[0];
            var seconds = route.GetProperty("duration").GetDouble();
            var meters = route.GetProperty("distance").GetDouble();
            return (Math.Round(seconds / 60.0, 1), Math.Round(meters / 1000.0, 2));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "OSRM drive failed");
            return (null, null);
        }
    }

    public async Task<AmenityProbeResult> ProbeAroundAsync(double lat, double lon, CancellationToken ct = default)
    {
        // ~1.2 km radius around district centroid
        var query = $"""
            [out:json][timeout:25];
            (
              node["leisure"="park"](around:1200,{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});
              way["leisure"="park"](around:1200,{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});
              node["shop"~"supermarket|convenience|greengrocer"](around:800,{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});
              node["amenity"="pharmacy"](around:800,{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});
              node["amenity"~"school|kindergarten"](around:1200,{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});
              node["highway"="bus_stop"](around:600,{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});
              node["railway"="tram_stop"](around:800,{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});
              way["highway"~"motorway|trunk|primary"](around:2000,{lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)});
            );
            out center;
            """;

        try
        {
            using var content = new StringContent(query);
            using var res = await http.PostAsync("https://overpass-api.de/api/interpreter", content, ct);
            if (!res.IsSuccessStatusCode)
                return AmenityProbeResult.Empty;
            await using var stream = await res.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var elements = doc.RootElement.GetProperty("elements");

            int parks = 0, shops = 0, pharm = 0, schools = 0, transit = 0;
            double? nearestHwyKm = null;

            foreach (var el in elements.EnumerateArray())
            {
                var tags = el.TryGetProperty("tags", out var t) ? t : default;
                if (tags.ValueKind != JsonValueKind.Object) continue;

                if (tags.TryGetProperty("leisure", out var leisure) && leisure.GetString() == "park")
                    parks++;
                else if (tags.TryGetProperty("shop", out _))
                    shops++;
                else if (tags.TryGetProperty("amenity", out var am))
                {
                    var a = am.GetString();
                    if (a == "pharmacy") pharm++;
                    else if (a is "school" or "kindergarten") schools++;
                }
                else if (tags.TryGetProperty("highway", out var hw))
                {
                    var h = hw.GetString();
                    if (h == "bus_stop") transit++;
                    else if (h is "motorway" or "trunk" or "primary")
                    {
                        var (elat, elon) = CenterOf(el);
                        if (elat is not null)
                        {
                            var km = HaversineKm(lat, lon, elat.Value, elon!.Value);
                            if (nearestHwyKm is null || km < nearestHwyKm) nearestHwyKm = km;
                        }
                    }
                }
                else if (tags.TryGetProperty("railway", out var rw) && rw.GetString() == "tram_stop")
                    transit++;
            }

            var quiet = nearestHwyKm is null ? 8
                : nearestHwyKm < 0.15 ? 2
                : nearestHwyKm < 0.4 ? 4
                : nearestHwyKm < 0.8 ? 6
                : 8;

            return new AmenityProbeResult(parks, shops, pharm, schools, transit, nearestHwyKm is null ? null : Math.Round(nearestHwyKm.Value, 2), quiet);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Overpass probe failed");
            return AmenityProbeResult.Empty;
        }
    }

    static (double? Lat, double? Lon) CenterOf(JsonElement el)
    {
        if (el.TryGetProperty("lat", out var lat) && el.TryGetProperty("lon", out var lon))
            return (lat.GetDouble(), lon.GetDouble());
        if (el.TryGetProperty("center", out var c))
            return (c.GetProperty("lat").GetDouble(), c.GetProperty("lon").GetDouble());
        return (null, null);
    }

    static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

public record AmenityProbeResult(
    int ParkCount,
    int ShopCount,
    int PharmacyCount,
    int SchoolCount,
    int TransitStopCount,
    double? NearestHighwayKm,
    int? QuietScore)
{
    public static AmenityProbeResult Empty { get; } = new(0, 0, 0, 0, 0, null, null);
}
