using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CityChecker.Api.Data;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;

namespace CityChecker.Api.Services;

/// <summary>
/// Personal-use Otodom map pins via Next.js data endpoints (no official API).
/// Builds search from city + filters; paginates list pages; detail JSON for coords.
/// Full pin set is cached without bbox; viewport filter is applied in memory.
/// </summary>
public class OtodomMapService(
    HttpClient http,
    IMemoryCache cache,
    ILogger<OtodomMapService> log)
{
    static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    static readonly TimeSpan BuildIdTtl = TimeSpan.FromHours(6);
    const int PageSize = 36;
    // ponytail: hard cap ~20 pages; raise if city searches grow past this
    const int MaxListAds = 720;
    const int DetailConcurrency = 8;

    static readonly Regex BuildIdRe = new(
        "\"buildId\"\\s*:\\s*\"([^\"]+)\"",
        RegexOptions.Compiled);

    static readonly Dictionary<Guid, string> CityPaths = new()
    {
        [SeedData.LodzId] = "lodzkie/lodz/lodz/lodz",
        [SeedData.KrakowId] = "malopolskie/krakow/krakow/krakow",
        [SeedData.WarszawaId] = "mazowieckie/warszawa/warszawa/warszawa",
    };

    static readonly string[] DefaultRooms =
        ["TWO", "THREE", "FOUR", "FIVE", "SIX_OR_MORE"];

    // ponytail: coalesce concurrent loads for the same filter set (moveend stampede)
    static readonly ConcurrentDictionary<string, Task<CachedOtodomSet>> Inflight = new();

    public async Task<OtodomPinsResult> GetPinsAsync(OtodomPinsQuery q, CancellationToken ct = default)
    {
        string pathAfterWyniki;
        Dictionary<string, string> query;

        if (!string.IsNullOrWhiteSpace(q.SearchUrl) &&
            TryParseSearchUrl(q.SearchUrl, out pathAfterWyniki, out query))
        {
            // optional advanced override
        }
        else if (!TryBuildFromFilters(q, out pathAfterWyniki, out query, out var buildErr))
        {
            return OtodomPinsResult.Fail(buildErr!);
        }

        // ponytail: do NOT set viewType=listing — Otodom returns __N_REDIRECT with no searchAds
        query.Remove("viewType");
        query["limit"] = PageSize.ToString(CultureInfo.InvariantCulture);
        query["by"] = query.GetValueOrDefault("by") ?? "DEFAULT";
        query["direction"] = query.GetValueOrDefault("direction") ?? "DESC";
        // omit mapBounds so Otodom returns full city match; we filter by viewport locally

        var cacheKey =
            $"otodom:v2:{pathAfterWyniki}:{string.Join("&", query.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}";
        if (!cache.TryGetValue(cacheKey, out CachedOtodomSet? set) || set is null)
        {
            try
            {
                var load = Inflight.GetOrAdd(cacheKey, key => LoadAndCacheAsync(key, pathAfterWyniki, query));
                try
                {
                    set = await load.WaitAsync(ct);
                }
                finally
                {
                    Inflight.TryRemove(cacheKey, out _);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Inflight.TryRemove(cacheKey, out _);
                log.LogWarning(ex, "Otodom pins failed");
                var msg = ex.Message switch
                {
                    var m when m.Contains("buildId", StringComparison.OrdinalIgnoreCase) => m,
                    var m when m.Contains("Unexpected", StringComparison.OrdinalIgnoreCase) => m,
                    var m when m.Contains("returned", StringComparison.OrdinalIgnoreCase) => m,
                    _ => "Otodom request failed (timeout or blocked). Try again later.",
                };
                return OtodomPinsResult.Fail(msg);
            }
        }

        var inView = set.Pins
            .Where(p => p.Lat >= q.South && p.Lat <= q.North && p.Lon >= q.West && p.Lon <= q.East)
            .ToList();
        return new OtodomPinsResult(true, null, inView, set.FetchedAt, set.TotalMatched, set.Listed);
    }

    async Task<CachedOtodomSet> LoadAndCacheAsync(
        string cacheKey,
        string pathAfterWyniki,
        Dictionary<string, string> query)
    {
        var buildId = await GetBuildIdAsync(CancellationToken.None)
            ?? throw new InvalidOperationException("Could not read Otodom buildId (site changed or blocked).");
        var (summaries, totalMatched) = await FetchAllListPagesAsync(buildId, pathAfterWyniki, query, CancellationToken.None);
        var allPins = await EnrichAllCoordinatesAsync(buildId, summaries, CancellationToken.None);
        var set = new CachedOtodomSet(allPins, totalMatched, summaries.Count, DateTime.UtcNow);
        cache.Set(cacheKey, set, CacheTtl);
        return set;
    }

    static bool TryBuildFromFilters(
        OtodomPinsQuery q,
        out string pathAfterWyniki,
        out Dictionary<string, string> query,
        out string? error)
    {
        pathAfterWyniki = "";
        query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;

        if (q.CityId is null || !CityPaths.TryGetValue(q.CityId.Value, out var cityPath))
        {
            error = "Pick a seeded city (Łódź / Kraków / Warszawa) or pass a wyniki URL.";
            return false;
        }

        var tx = (q.Transaction ?? "SELL").Equals("RENT", StringComparison.OrdinalIgnoreCase)
            ? "wynajem"
            : "sprzedaz";
        pathAfterWyniki = $"{tx}/mieszkanie/{cityPath}";

        query["ownerTypeSingleSelect"] = "ALL";
        var priceMax = q.PriceMax ?? 650_000;
        var areaMin = q.AreaMin ?? 50;
        query["priceMax"] = ((int)Math.Round(priceMax)).ToString(CultureInfo.InvariantCulture);
        query["areaMin"] = ((int)Math.Round(areaMin)).ToString(CultureInfo.InvariantCulture);

        var rooms = (q.Rooms is { Length: > 0 } ? q.Rooms : DefaultRooms)
            .Select(NormalizeRoom)
            .Where(r => r is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (rooms.Length == 0) rooms = DefaultRooms;
        query["roomsNumber"] = "[" + string.Join(",", rooms) + "]";
        return true;
    }

    static string? NormalizeRoom(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToUpperInvariant();
        return s switch
        {
            "1" or "ONE" => "ONE",
            "2" or "TWO" => "TWO",
            "3" or "THREE" => "THREE",
            "4" or "FOUR" => "FOUR",
            "5" or "FIVE" => "FIVE",
            "6" or "SIX" or "SIX_OR_MORE" or "6+" => "SIX_OR_MORE",
            _ => null,
        };
    }

    async Task<(List<OtodomAdSummary> Summaries, int TotalMatched)> FetchAllListPagesAsync(
        string buildId,
        string pathAfterWyniki,
        Dictionary<string, string> baseQuery,
        CancellationToken ct)
    {
        var summaries = new List<OtodomAdSummary>();
        var seen = new HashSet<long>();
        var totalMatched = 0;
        var totalPages = 1;

        for (var page = 1; page <= totalPages; page++)
        {
            if (summaries.Count >= MaxListAds) break;

            var query = new Dictionary<string, string>(baseQuery, StringComparer.OrdinalIgnoreCase)
            {
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
            };

            var listUrl = BuildNextDataSearchUrl(buildId, pathAfterWyniki, query);
            using var listRes = await http.GetAsync(listUrl, ct);
            if (!listRes.IsSuccessStatusCode)
            {
                log.LogWarning("Otodom list page {Page} {Status}", page, listRes.StatusCode);
                if (page == 1)
                    throw new InvalidOperationException($"Otodom search returned {(int)listRes.StatusCode}.");
                break;
            }

            await using var listStream = await listRes.Content.ReadAsStreamAsync(ct);
            using var listDoc = await JsonDocument.ParseAsync(listStream, cancellationToken: ct);
            if (!listDoc.RootElement.TryGetProperty("pageProps", out var pageProps) ||
                !pageProps.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("searchAds", out var searchAds))
            {
                if (page == 1) throw new InvalidOperationException("Unexpected Otodom search payload.");
                break;
            }

            if (searchAds.TryGetProperty("pagination", out var pag))
            {
                if (TryReadInt32(pag, "totalItems", out var total))
                    totalMatched = total;
                if (TryReadInt32(pag, "totalPages", out var pages))
                    totalPages = Math.Max(1, pages);
            }

            if (!searchAds.TryGetProperty("items", out var itemsEl) || itemsEl.ValueKind != JsonValueKind.Array)
                break;

            foreach (var item in itemsEl.EnumerateArray())
            {
                if (summaries.Count >= MaxListAds) break;
                if (!TryReadInt64(item, "id", out var id) || id == 0) continue;
                var slug = ReadString(item, "slug");
                if (string.IsNullOrWhiteSpace(slug) || !seen.Add(id)) continue;

                var title = ReadString(item, "title") ?? "";
                var tx = ReadString(item, "transaction");
                double? price = null;
                if (item.TryGetProperty("totalPrice", out var tpEl) && tpEl.ValueKind == JsonValueKind.Object)
                    price = ReadDouble(tpEl, "value");
                var area = ReadDouble(item, "areaInSquareMeters");
                var rooms = ReadString(item, "roomsNumber");

                summaries.Add(new OtodomAdSummary(id, slug!, title, tx, price, area, rooms));
            }

            if (itemsEl.GetArrayLength() == 0) break;
        }

        return (summaries, totalMatched > 0 ? totalMatched : summaries.Count);
    }

    async Task<List<OtodomPinDto>> EnrichAllCoordinatesAsync(
        string buildId,
        List<OtodomAdSummary> summaries,
        CancellationToken ct)
    {
        var bag = new ConcurrentDictionary<int, OtodomPinDto>();
        using var gate = new SemaphoreSlim(DetailConcurrency);
        var tasks = summaries.Select(async (ad, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var coords = await GetListingCoordinatesAsync(buildId, ad.Slug, ct);
                if (coords is null) return;
                var (lat, lon) = coords.Value;
                bag[index] = new OtodomPinDto(
                    ad.Id,
                    ad.Slug,
                    ad.Title,
                    lat,
                    lon,
                    ad.Price,
                    ad.AreaM2,
                    ad.Rooms,
                    ad.Transaction,
                    $"https://www.otodom.pl/pl/oferta/{ad.Slug}");
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
        return bag.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    static bool TryReadInt32(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var el) &&
               el.ValueKind == JsonValueKind.Number &&
               el.TryGetInt32(out value);
    }

    static bool TryReadInt64(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var el) &&
               el.ValueKind == JsonValueKind.Number &&
               el.TryGetInt64(out value);
    }

    static double? ReadDouble(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Number)
            return null;
        return el.TryGetDouble(out var d) ? d : null;
    }

    static string? ReadString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetRawText(),
            JsonValueKind.Null => null,
            _ => null,
        };
    }

    async Task<(double Lat, double Lon)?> GetListingCoordinatesAsync(string buildId, string slug, CancellationToken ct)
    {
        var cacheKey = $"otodom-coord:{slug}";
        if (cache.TryGetValue(cacheKey, out (double Lat, double Lon) cached))
            return cached;

        var url = $"https://www.otodom.pl/_next/data/{buildId}/pl/oferta/{Uri.EscapeDataString(slug)}.json";
        using var res = await http.GetAsync(url, ct);
        if (!res.IsSuccessStatusCode) return null;
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("pageProps", out var props) ||
            !props.TryGetProperty("ad", out var ad) ||
            !ad.TryGetProperty("location", out var loc) ||
            !loc.TryGetProperty("coordinates", out var coords))
            return null;

        if (!coords.TryGetProperty("latitude", out var latEl) ||
            !coords.TryGetProperty("longitude", out var lonEl) ||
            !latEl.TryGetDouble(out var lat) ||
            !lonEl.TryGetDouble(out var lon))
            return null;

        var pair = (lat, lon);
        cache.Set(cacheKey, pair, TimeSpan.FromHours(12));
        return pair;
    }

    async Task<string?> GetBuildIdAsync(CancellationToken ct)
    {
        if (cache.TryGetValue("otodom:buildId", out string? cached) && !string.IsNullOrEmpty(cached))
            return cached;

        using var res = await http.GetAsync(
            "https://www.otodom.pl/pl/wyniki/sprzedaz/mieszkanie/cala-polska", ct);
        if (!res.IsSuccessStatusCode) return null;
        var html = await res.Content.ReadAsStringAsync(ct);
        var m = BuildIdRe.Match(html);
        if (!m.Success) return null;
        var id = m.Groups[1].Value;
        cache.Set("otodom:buildId", id, BuildIdTtl);
        return id;
    }

    static string BuildNextDataSearchUrl(string buildId, string pathAfterWyniki, Dictionary<string, string> query)
    {
        var path = pathAfterWyniki.Trim('/');
        var qs = string.Join("&", query.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return $"https://www.otodom.pl/_next/data/{buildId}/pl/wyniki/{path}.json?{qs}";
    }

    public static bool TryParseSearchUrl(string raw, out string pathAfterWyniki, out Dictionary<string, string> query)
    {
        pathAfterWyniki = "";
        query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw)) return false;

        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return false;
        if (!uri.Host.Contains("otodom.pl", StringComparison.OrdinalIgnoreCase))
            return false;

        var segs = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var wynikiIdx = Array.FindIndex(segs, s => s.Equals("wyniki", StringComparison.OrdinalIgnoreCase));
        if (wynikiIdx < 0 || wynikiIdx >= segs.Length - 1) return false;
        pathAfterWyniki = string.Join('/', segs.Skip(wynikiIdx + 1));
        if (pathAfterWyniki.Length == 0) return false;

        foreach (var (key, values) in QueryHelpers.ParseQuery(uri.Query))
        {
            if (values.Count > 0 && !string.IsNullOrEmpty(values[0]))
                query[key] = values[0]!;
        }
        query.Remove("mapBounds");
        query.Remove("page");
        return true;
    }

    public static void SelfCheck()
    {
        const string sample =
            "https://www.otodom.pl/pl/wyniki/sprzedaz/mieszkanie/lodzkie/lodz/lodz/lodz?priceMax=650000&areaMin=50";
        if (!TryParseSearchUrl(sample, out var path, out var q))
            throw new InvalidOperationException("OtodomMapService.SelfCheck: parse failed");
        if (!path.StartsWith("sprzedaz/mieszkanie/lodzkie/", StringComparison.Ordinal))
            throw new InvalidOperationException("OtodomMapService.SelfCheck: bad path " + path);
        if (q.GetValueOrDefault("priceMax") != "650000")
            throw new InvalidOperationException("OtodomMapService.SelfCheck: bad query");

        var built = TryBuildFromFilters(
            new OtodomPinsQuery(SeedData.LodzId, 650000, 50, DefaultRooms, "SELL", 19, 51, 20, 52, null),
            out var builtPath, out _, out var err);
        if (!built || err is not null || !builtPath.Contains("lodzkie/lodz", StringComparison.Ordinal))
            throw new InvalidOperationException("OtodomMapService.SelfCheck: filter build failed");
    }

    readonly record struct OtodomAdSummary(
        long Id,
        string Slug,
        string Title,
        string? Transaction,
        double? Price,
        double? AreaM2,
        string? Rooms);

    sealed record CachedOtodomSet(
        IReadOnlyList<OtodomPinDto> Pins,
        int TotalMatched,
        int Listed,
        DateTime FetchedAt);
}

public record OtodomPinsQuery(
    Guid? CityId,
    double? PriceMax,
    double? AreaMin,
    string[]? Rooms,
    string? Transaction,
    double West,
    double South,
    double East,
    double North,
    string? SearchUrl);

public record OtodomPinDto(
    long Id,
    string Slug,
    string Title,
    double Lat,
    double Lon,
    double? Price,
    double? AreaM2,
    string? Rooms,
    string? Transaction,
    string Url);

public record OtodomPinsResult(
    bool Ok,
    string? Error,
    IReadOnlyList<OtodomPinDto> Pins,
    DateTime? FetchedAt,
    int? TotalMatched = null,
    int? Listed = null)
{
    public static OtodomPinsResult Fail(string error) => new(false, error, [], null, null, null);
}
