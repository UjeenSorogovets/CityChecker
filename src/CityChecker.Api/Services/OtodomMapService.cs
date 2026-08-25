using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace CityChecker.Api.Services;

/// <summary>
/// Shared Otodom map pins: Postgres cache by city+filters; Refresh scrapes Next.js data endpoints.
/// </summary>
public class OtodomMapService(
    AppDbContext db,
    HttpClient http,
    IMemoryCache cache,
    IServiceScopeFactory scopes,
    ILogger<OtodomMapService> log)
{
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
        [SeedData.WroclawId] = "dolnoslaskie/wroclaw/wroclaw/wroclaw",
        [SeedData.GdanskId] = "pomorskie/gdansk/gdansk/gdansk",
    };

    static readonly string[] DefaultRooms =
        ["TWO", "THREE", "FOUR", "FIVE", "SIX_OR_MORE"];

    // ponytail: coalesce concurrent refreshes for the same filter key
    static readonly ConcurrentDictionary<string, Task<OtodomPinsResult>> InflightRefresh = new();

    public async Task<OtodomPinsResult> GetCachedPinsAsync(OtodomPinsQuery q, CancellationToken ct = default)
    {
        if (!TryResolveFilterKey(q, out var key, out var pathAfterWyniki, out var otodomQuery, out var err))
            return OtodomPinsResult.Fail(err!);

        var set = await FindPinSetAsync(key, ct);
        if (set is null)
            return new OtodomPinsResult(true, null, [], null, null, null, "Missing");

        var tx = set.Transaction;
        var pins = await db.OtodomPins.AsNoTracking()
            .Where(p => p.PinSetId == set.PinSetId
                        && p.Lat >= q.South && p.Lat <= q.North
                        && p.Lon >= q.West && p.Lon <= q.East)
            .OrderBy(p => p.ExternalId)
            .Select(p => new OtodomPinDto(
                p.ExternalId, p.Slug, p.Title, p.Lat, p.Lon, p.Price, p.AreaM2, p.Rooms, tx, p.Url))
            .ToListAsync(ct);

        return new OtodomPinsResult(
            set.Status != "Failed",
            set.Status == "Failed" ? (set.LastError ?? "Otodom refresh failed.") : null,
            pins,
            set.FetchedAt, set.TotalMatched, set.Listed, set.Status);
    }

    public async Task<OtodomPinsResult> RefreshPinsAsync(OtodomPinsQuery q, CancellationToken ct = default)
    {
        if (!TryResolveFilterKey(q, out var key, out var pathAfterWyniki, out var otodomQuery, out var err))
            return OtodomPinsResult.Fail(err!);

        var flightKey = key.CacheKey;
        var load = InflightRefresh.GetOrAdd(flightKey, _ => RefreshAndStoreAsync(key, pathAfterWyniki, otodomQuery));
        try
        {
            await load.WaitAsync(ct);
            return await GetCachedPinsAsync(q, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Otodom refresh failed");
            return OtodomPinsResult.Fail(FormatError(ex));
        }
        finally
        {
            InflightRefresh.TryRemove(flightKey, out _);
        }
    }

    async Task<OtodomPinsResult> RefreshAndStoreAsync(
        FilterKey key,
        string pathAfterWyniki,
        Dictionary<string, string> otodomQuery)
    {
        await using (var scope = scopes.CreateAsyncScope())
        {
            var sdb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var set = await FindPinSetAsync(sdb, key, CancellationToken.None);
            if (set is null)
            {
                set = new OtodomPinSet
                {
                    PinSetId = Guid.NewGuid(),
                    CityId = key.CityId,
                    Transaction = key.Transaction,
                    PriceMax = key.PriceMax,
                    AreaMin = key.AreaMin,
                    RoomsKey = key.RoomsKey,
                    Status = "Refreshing",
                };
                sdb.OtodomPinSets.Add(set);
            }
            else
            {
                set.Status = "Refreshing";
                set.LastError = null;
            }
            await sdb.SaveChangesAsync(CancellationToken.None);
        }

        try
        {
            // ponytail: do NOT set viewType=listing — Otodom returns __N_REDIRECT with no searchAds
            otodomQuery.Remove("viewType");
            otodomQuery["limit"] = PageSize.ToString(CultureInfo.InvariantCulture);
            otodomQuery["by"] = otodomQuery.GetValueOrDefault("by") ?? "DEFAULT";
            otodomQuery["direction"] = otodomQuery.GetValueOrDefault("direction") ?? "DESC";

            var buildId = await GetBuildIdAsync(CancellationToken.None)
                ?? throw new InvalidOperationException("Could not read Otodom buildId (site changed or blocked).");
            var (summaries, totalMatched) = await FetchAllListPagesAsync(
                buildId, pathAfterWyniki, otodomQuery, CancellationToken.None);
            var scraped = await EnrichAllCoordinatesAsync(buildId, summaries, CancellationToken.None);

            await using var scope = scopes.CreateAsyncScope();
            var sdb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var set = await FindPinSetAsync(sdb, key, CancellationToken.None)
                ?? throw new InvalidOperationException("Otodom pin set missing after refresh start.");

            var oldPins = await sdb.OtodomPins.Where(p => p.PinSetId == set.PinSetId).ToListAsync(CancellationToken.None);
            sdb.OtodomPins.RemoveRange(oldPins);

            foreach (var p in scraped)
            {
                sdb.OtodomPins.Add(new OtodomPin
                {
                    PinId = Guid.NewGuid(),
                    PinSetId = set.PinSetId,
                    ExternalId = p.Id,
                    Slug = Truncate(p.Slug, 400),
                    Title = Truncate(p.Title, 500),
                    Lat = p.Lat,
                    Lon = p.Lon,
                    Price = p.Price,
                    AreaM2 = p.AreaM2,
                    Rooms = string.IsNullOrEmpty(p.Rooms) ? null : Truncate(p.Rooms, 64),
                    Url = Truncate(p.Url, 1000),
                });
            }

            set.TotalMatched = totalMatched;
            set.Listed = summaries.Count;
            set.FetchedAt = DateTime.UtcNow;
            set.Status = "Ready";
            set.LastError = null;
            await sdb.SaveChangesAsync(CancellationToken.None);

            return new OtodomPinsResult(true, null, scraped, set.FetchedAt, set.TotalMatched, set.Listed, "Ready");
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Otodom refresh store failed for {Key}", key.CacheKey);
            var msg = Truncate(FormatError(ex), 1000);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sdb = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var set = await FindPinSetAsync(sdb, key, CancellationToken.None);
                if (set is not null)
                {
                    set.Status = "Failed";
                    set.LastError = msg;
                    // keep previous pins if any
                    await sdb.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception saveEx)
            {
                log.LogWarning(saveEx, "Otodom failed to persist Failed status");
            }
            return OtodomPinsResult.Fail(msg);
        }
    }

    static async Task<OtodomPinSet?> FindPinSetAsync(AppDbContext sdb, FilterKey key, CancellationToken ct) =>
        await sdb.OtodomPinSets.FirstOrDefaultAsync(
            s => s.CityId == key.CityId
                 && s.Transaction == key.Transaction
                 && s.PriceMax == key.PriceMax
                 && s.AreaMin == key.AreaMin
                 && s.RoomsKey == key.RoomsKey,
            ct);

    async Task<OtodomPinSet?> FindPinSetAsync(FilterKey key, CancellationToken ct) =>
        await FindPinSetAsync(db, key, ct);

    static bool TryResolveFilterKey(
        OtodomPinsQuery q,
        out FilterKey key,
        out string pathAfterWyniki,
        out Dictionary<string, string> otodomQuery,
        out string? error)
    {
        key = default;
        pathAfterWyniki = "";
        otodomQuery = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = null;

        if (q.CityId is null || !CityPaths.TryGetValue(q.CityId.Value, out var cityPath))
        {
            error = "Pick a seeded city (Łódź / Kraków / Warszawa / Wrocław / Gdańsk).";
            return false;
        }

        var transaction = (q.Transaction ?? "SELL").Equals("RENT", StringComparison.OrdinalIgnoreCase)
            ? "RENT"
            : "SELL";
        var txPath = transaction == "RENT" ? "wynajem" : "sprzedaz";
        pathAfterWyniki = $"{txPath}/mieszkanie/{cityPath}";

        var priceMax = (int)Math.Round(q.PriceMax ?? 650_000);
        var areaMin = (int)Math.Round(q.AreaMin ?? 50);
        var rooms = NormalizeRooms(q.Rooms);
        var roomsKey = string.Join(",", rooms.OrderBy(r => r, StringComparer.Ordinal));

        otodomQuery["ownerTypeSingleSelect"] = "ALL";
        otodomQuery["priceMax"] = priceMax.ToString(CultureInfo.InvariantCulture);
        otodomQuery["areaMin"] = areaMin.ToString(CultureInfo.InvariantCulture);
        otodomQuery["roomsNumber"] = "[" + string.Join(",", rooms) + "]";

        key = new FilterKey(q.CityId.Value, transaction, priceMax, areaMin, roomsKey);
        return true;
    }

    static string[] NormalizeRooms(string[]? raw)
    {
        var rooms = (raw is { Length: > 0 } ? raw : DefaultRooms)
            .Select(NormalizeRoom)
            .Where(r => r is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return rooms.Length == 0 ? DefaultRooms : rooms;
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

    static string FormatError(Exception ex) => ex.Message switch
    {
        var m when m.Contains("buildId", StringComparison.OrdinalIgnoreCase) => m,
        var m when m.Contains("Unexpected", StringComparison.OrdinalIgnoreCase) => m,
        var m when m.Contains("Otodom", StringComparison.OrdinalIgnoreCase) => m,
        var m when m.Contains("returned", StringComparison.OrdinalIgnoreCase) => m,
        _ => "Otodom request failed (timeout or blocked). Try again later.",
    };

    // ponytail: map raw status → actionable text (403 on VPS is usually anti-bot, not a bad city path)
    static string DescribeSearchHttp(int code) => code switch
    {
        403 => "Otodom blocked this server (HTTP 403 anti-bot). Wait and retry — datacenter IPs are often blocked.",
        429 => "Otodom rate-limited the request (HTTP 429). Wait a few minutes and retry.",
        404 => "Otodom search URL not found (HTTP 404). City path may be wrong or Otodom changed their URLs.",
        >= 500 and < 600 => $"Otodom is temporarily unavailable (HTTP {code}). Retry later.",
        _ => $"Otodom search failed (HTTP {code}).",
    };

    static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max];
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
                    throw new InvalidOperationException(DescribeSearchHttp((int)listRes.StatusCode));
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

        if (!TryResolveFilterKey(
                new OtodomPinsQuery(SeedData.LodzId, 650000, 50, DefaultRooms, "SELL", 19, 51, 20, 52, null),
                out var key, out var builtPath, out _, out var err)
            || err is not null
            || !builtPath.Contains("lodzkie/lodz", StringComparison.Ordinal)
            || key.RoomsKey.Length == 0)
            throw new InvalidOperationException("OtodomMapService.SelfCheck: filter key failed");

        if (!DescribeSearchHttp(403).Contains("403", StringComparison.Ordinal))
            throw new InvalidOperationException("OtodomMapService.SelfCheck: http describe failed");
    }

    readonly record struct FilterKey(
        Guid CityId,
        string Transaction,
        int PriceMax,
        int AreaMin,
        string RoomsKey)
    {
        public string CacheKey => $"{CityId:N}|{Transaction}|{PriceMax}|{AreaMin}|{RoomsKey}";
    }

    readonly record struct OtodomAdSummary(
        long Id,
        string Slug,
        string Title,
        string? Transaction,
        double? Price,
        double? AreaM2,
        string? Rooms);
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
    int? Listed = null,
    string? Status = null)
{
    public static OtodomPinsResult Fail(string error) => new(false, error, [], null, null, null, "Failed");
}
