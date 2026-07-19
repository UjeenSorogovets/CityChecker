using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace CityChecker.Api.Services;

public class NominatimOptions
{
    public const string Section = "Nominatim";
    public string BaseUrl { get; set; } = "https://nominatim.openstreetmap.org";
    public string UserAgent { get; set; } = "CityChecker/1.0 (personal; contact@example.com)";
}

public record GeocodeResult(string AddressLine, string? CityName, double Lat, double Lon);

public class NominatimClient(HttpClient http, IOptionsSnapshot<NominatimOptions> options, ILogger<NominatimClient> logger)
{
    // ponytail: in-memory cache keyed by ~11m grid; ceiling = multi-instance drift — use Redis if scaling
    static readonly ConcurrentDictionary<string, (GeocodeResult? Result, DateTime Expires)> Cache = new();
    static readonly SemaphoreSlim Gate = new(1, 1);
    static DateTime _lastCall = DateTime.MinValue;

    public async Task<GeocodeResult?> ReverseAsync(double lat, double lon, CancellationToken ct = default)
    {
        var key = $"{lat:F4},{lon:F4}";
        if (Cache.TryGetValue(key, out var hit) && hit.Expires > DateTime.UtcNow)
            return hit.Result;

        await Gate.WaitAsync(ct);
        try
        {
            if (Cache.TryGetValue(key, out hit) && hit.Expires > DateTime.UtcNow)
                return hit.Result;

            var elapsed = DateTime.UtcNow - _lastCall;
            if (elapsed < TimeSpan.FromSeconds(1))
                await Task.Delay(TimeSpan.FromSeconds(1) - elapsed, ct);

            var url = $"{options.Value.BaseUrl.TrimEnd('/')}/reverse?lat={lat:F6}&lon={lon:F6}&format=json&addressdetails=1&countrycodes=pl&zoom=18";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", options.Value.UserAgent);
            req.Headers.TryAddWithoutValidation("Accept-Language", "pl");

            _lastCall = DateTime.UtcNow;
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Nominatim HTTP {Status}", resp.StatusCode);
                Cache[key] = (null, DateTime.UtcNow.AddMinutes(5));
                return null;
            }

            var json = await resp.Content.ReadFromJsonAsync<NominatimResponse>(cancellationToken: ct);
            if (json?.Address is null ||
                !string.Equals(json.Address.CountryCode, "pl", StringComparison.OrdinalIgnoreCase))
            {
                Cache[key] = (null, DateTime.UtcNow.AddMinutes(30));
                return null;
            }

            var street = json.Address.Road ?? json.Address.Pedestrian ?? json.Address.Path ?? "Unknown street";
            var number = json.Address.HouseNumber;
            var addressLine = string.IsNullOrWhiteSpace(number) ? street : $"{street} {number}";
            var cityName = json.Address.City ?? json.Address.Town ?? json.Address.Village ?? json.Address.Municipality;

            var result = new GeocodeResult(addressLine, cityName, lat, lon);
            Cache[key] = (result, DateTime.UtcNow.AddHours(24));
            return result;
        }
        finally
        {
            Gate.Release();
        }
    }

    sealed class NominatimResponse
    {
        [JsonPropertyName("address")]
        public NominatimAddress? Address { get; set; }
    }

    sealed class NominatimAddress
    {
        [JsonPropertyName("road")] public string? Road { get; set; }
        [JsonPropertyName("pedestrian")] public string? Pedestrian { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("house_number")] public string? HouseNumber { get; set; }
        [JsonPropertyName("city")] public string? City { get; set; }
        [JsonPropertyName("town")] public string? Town { get; set; }
        [JsonPropertyName("village")] public string? Village { get; set; }
        [JsonPropertyName("municipality")] public string? Municipality { get; set; }
        [JsonPropertyName("country_code")] public string? CountryCode { get; set; }
    }
}
