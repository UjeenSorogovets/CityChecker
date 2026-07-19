using System.Globalization;
using System.Security.Claims;
using System.Text;
using CityChecker.Api.Auth;
using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using CityChecker.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Endpoints;

public static class HousingEndpoints
{
    public static void MapHousingEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/housing").RequireAuthorization();

        // --- Anchors ---
        g.MapGet("/anchors", async (ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var list = await db.MapAnchors.AsNoTracking()
                .Where(a => a.UserId == uid)
                .OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt)
                .Select(a => new AnchorDto(a.AnchorId, a.Label, a.Lat, a.Lon, a.SortOrder))
                .ToListAsync();
            return Results.Ok(list);
        });

        g.MapPost("/anchors", async (AnchorWriteDto body, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.Label)) return Results.BadRequest(new { error = "Label required." });
            var count = await db.MapAnchors.CountAsync(a => a.UserId == uid);
            if (count >= 8) return Results.BadRequest(new { error = "Max 8 anchors." });
            var a = new MapAnchor
            {
                AnchorId = Guid.NewGuid(),
                UserId = uid,
                Label = body.Label.Trim(),
                Lat = body.Lat,
                Lon = body.Lon,
                SortOrder = body.SortOrder ?? count,
                CreatedAt = DateTime.UtcNow,
            };
            db.MapAnchors.Add(a);
            await db.SaveChangesAsync();
            return Results.Created($"/api/housing/anchors/{a.AnchorId}", new AnchorDto(a.AnchorId, a.Label, a.Lat, a.Lon, a.SortOrder));
        });

        g.MapDelete("/anchors/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var a = await db.MapAnchors.FirstOrDefaultAsync(x => x.AnchorId == id && x.UserId == uid);
            if (a is null) return Results.NotFound();
            db.MapAnchors.Remove(a);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // --- Commute matrix (district centroids × anchors) ---
        g.MapGet("/commute/{cityId:guid}", async (
            Guid cityId, ClaimsPrincipal user, AppDbContext db, HousingGeoService geo, CancellationToken ct) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();

            var anchors = await db.MapAnchors.AsNoTracking()
                .Where(a => a.UserId == uid).OrderBy(a => a.SortOrder).ToListAsync(ct);
            var districts = await db.Districts.AsNoTracking()
                .Where(d => d.CityId == cityId)
                .Select(d => new { d.DistrictId, d.Name, Centroid = d.Geom.Centroid })
                .ToListAsync(ct);

            var rows = new List<DistrictCommuteDto>();
            foreach (var d in districts)
            {
                var legs = new List<CommuteLegDto>();
                foreach (var a in anchors)
                {
                    var (min, km) = await geo.DriveAsync(d.Centroid.Y, d.Centroid.X, a.Lat, a.Lon, ct);
                    legs.Add(new CommuteLegDto(a.AnchorId, a.Label, min, km));
                }
                var maxMin = legs.Where(l => l.DriveMinutes != null).Select(l => l.DriveMinutes!.Value).DefaultIfEmpty().Max();
                rows.Add(new DistrictCommuteDto(d.DistrictId, d.Name, d.Centroid.Y, d.Centroid.X, legs, maxMin > 0 ? maxMin : null));
            }
            return Results.Ok(rows);
        });

        // --- District picks (shortlist / veto) ---
        g.MapGet("/picks", async (Guid? cityId, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var q = db.DistrictPicks.AsNoTracking().Where(p => p.UserId == uid);
            if (cityId is not null)
                q = q.Where(p => p.District.CityId == cityId);
            var list = await q
                .Include(p => p.District)
                .OrderBy(p => p.Status).ThenBy(p => p.District.Name)
                .ToListAsync();
            return Results.Ok(list.Select(p => ToPickDto(p, p.District.Name, p.District.CityId)));
        });

        g.MapPut("/picks/{districtId:guid}", async (Guid districtId, PickWriteDto body, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            if (!await db.Districts.AnyAsync(d => d.DistrictId == districtId))
                return Results.NotFound();

            var pick = await db.DistrictPicks.FirstOrDefaultAsync(p => p.UserId == uid && p.DistrictId == districtId);
            if (pick is null)
            {
                pick = new DistrictPick
                {
                    PickId = Guid.NewGuid(),
                    UserId = uid,
                    DistrictId = districtId,
                };
                db.DistrictPicks.Add(pick);
            }
            pick.Status = body.Status;
            pick.VetoReason = body.VetoReason;
            pick.QuietScore = body.QuietScore ?? pick.QuietScore;
            pick.ReminderAt = body.ReminderAt;
            pick.ReminderNote = body.ReminderNote;
            pick.RiskNotes = body.RiskNotes;
            pick.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var d = await db.Districts.AsNoTracking().FirstAsync(x => x.DistrictId == districtId);
            return Results.Ok(ToPickDto(pick, d.Name, d.CityId));
        });

        g.MapPost("/picks/{districtId:guid}/probe", async (
            Guid districtId, ClaimsPrincipal user, AppDbContext db, HousingGeoService geo, CancellationToken ct) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var d = await db.Districts.AsNoTracking()
                .Where(x => x.DistrictId == districtId)
                .Select(x => new { x.DistrictId, x.Name, x.CityId, Centroid = x.Geom.Centroid })
                .FirstOrDefaultAsync(ct);
            if (d is null) return Results.NotFound();

            var probe = await geo.ProbeAroundAsync(d.Centroid.Y, d.Centroid.X, ct);
            var pick = await db.DistrictPicks.FirstOrDefaultAsync(p => p.UserId == uid && p.DistrictId == districtId, ct);
            if (pick is null)
            {
                pick = new DistrictPick
                {
                    PickId = Guid.NewGuid(),
                    UserId = uid,
                    DistrictId = districtId,
                    Status = DistrictPickStatus.Exploring,
                };
                db.DistrictPicks.Add(pick);
            }
            pick.ParkCount = probe.ParkCount;
            pick.ShopCount = probe.ShopCount;
            pick.PharmacyCount = probe.PharmacyCount;
            pick.SchoolCount = probe.SchoolCount;
            pick.TransitStopCount = probe.TransitStopCount;
            pick.NearestHighwayKm = probe.NearestHighwayKm;
            pick.QuietScore = probe.QuietScore ?? pick.QuietScore;
            pick.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToPickDto(pick, d.Name, d.CityId));
        });

        // --- Visits ---
        g.MapGet("/visits", async (Guid? districtId, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var q = db.DistrictVisits.AsNoTracking().Where(v => v.UserId == uid);
            if (districtId is not null) q = q.Where(v => v.DistrictId == districtId);
            var list = await q.OrderByDescending(v => v.VisitedAt)
                .Select(v => new VisitDto(v.VisitId, v.DistrictId, v.VisitedAt, v.EveningFeel, v.Daylight, v.DogWalk, v.SaturdayLife, v.WinterFeel, v.Notes))
                .ToListAsync();
            return Results.Ok(list);
        });

        g.MapPost("/visits", async (VisitWriteDto body, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            if (!await db.Districts.AnyAsync(d => d.DistrictId == body.DistrictId))
                return Results.NotFound();
            var v = new DistrictVisit
            {
                VisitId = Guid.NewGuid(),
                UserId = uid,
                DistrictId = body.DistrictId,
                VisitedAt = body.VisitedAt ?? DateTime.UtcNow,
                EveningFeel = body.EveningFeel,
                Daylight = body.Daylight,
                DogWalk = body.DogWalk,
                SaturdayLife = body.SaturdayLife,
                WinterFeel = body.WinterFeel,
                Notes = body.Notes,
            };
            db.DistrictVisits.Add(v);
            await db.SaveChangesAsync();
            return Results.Created($"/api/housing/visits/{v.VisitId}",
                new VisitDto(v.VisitId, v.DistrictId, v.VisitedAt, v.EveningFeel, v.Daylight, v.DogWalk, v.SaturdayLife, v.WinterFeel, v.Notes));
        });

        // --- Offers ---
        g.MapGet("/offers", async (bool? finalistsOnly, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var q = db.HousingOffers.AsNoTracking().Where(o => o.UserId == uid);
            if (finalistsOnly == true) q = q.Where(o => o.IsFinalist);
            var list = await q.OrderByDescending(o => o.IsFinalist).ThenByDescending(o => o.CreatedAt)
                .ToListAsync();
            return Results.Ok(list.Select(ToOfferDto));
        });

        g.MapPost("/offers", async (OfferWriteDto body, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(body.Title)) return Results.BadRequest(new { error = "Title required." });
            var o = FromWrite(body, uid);
            o.OfferId = Guid.NewGuid();
            o.CreatedAt = DateTime.UtcNow;
            db.HousingOffers.Add(o);
            await db.SaveChangesAsync();
            return Results.Created($"/api/housing/offers/{o.OfferId}", ToOfferDto(o));
        });

        g.MapPut("/offers/{id:guid}", async (Guid id, OfferWriteDto body, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var o = await db.HousingOffers.FirstOrDefaultAsync(x => x.OfferId == id && x.UserId == uid);
            if (o is null) return Results.NotFound();
            ApplyWrite(o, body);
            o.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToOfferDto(o));
        });

        g.MapDelete("/offers/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var o = await db.HousingOffers.FirstOrDefaultAsync(x => x.OfferId == id && x.UserId == uid);
            if (o is null) return Results.NotFound();
            db.HousingOffers.Remove(o);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // --- Decision profile (weights) ---
        g.MapGet("/profile", async (ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var p = await db.DecisionProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid)
                    ?? new DecisionProfile { UserId = uid! };
            return Results.Ok(new ProfileDto(p.WeightCommute, p.WeightQuiet, p.WeightPrice, p.WeightGreen, p.WeightComfort));
        });

        g.MapPut("/profile", async (ProfileDto body, ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var p = await db.DecisionProfiles.FirstOrDefaultAsync(x => x.UserId == uid);
            if (p is null)
            {
                p = new DecisionProfile { ProfileId = Guid.NewGuid(), UserId = uid };
                db.DecisionProfiles.Add(p);
            }
            p.WeightCommute = body.WeightCommute;
            p.WeightQuiet = body.WeightQuiet;
            p.WeightPrice = body.WeightPrice;
            p.WeightGreen = body.WeightGreen;
            p.WeightComfort = body.WeightComfort;
            await db.SaveChangesAsync();
            return Results.Ok(body);
        });

        // --- Compare / rank districts ---
        g.MapGet("/compare/{cityId:guid}", async (
            Guid cityId, ClaimsPrincipal user, AppDbContext db, AggregateService aggregates, HousingGeoService geo, CancellationToken ct) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();

            var profile = await db.DecisionProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == uid, ct)
                          ?? new DecisionProfile();
            var picks = await db.DistrictPicks.AsNoTracking()
                .Where(p => p.UserId == uid && p.District.CityId == cityId).ToListAsync(ct);
            var visits = await db.DistrictVisits.AsNoTracking()
                .Where(v => v.UserId == uid && v.District.CityId == cityId)
                .GroupBy(v => v.DistrictId)
                .Select(g => new { DistrictId = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            var visitMap = visits.ToDictionary(x => x.DistrictId, x => x.Count);

            var batch = await aggregates.ForCityBatchAsync(cityId, ct);
            var comfortMap = batch.Districts.ToDictionary(d => d.Id, d => d);

            var anchors = await db.MapAnchors.AsNoTracking().Where(a => a.UserId == uid).ToListAsync(ct);
            var districts = await db.Districts.AsNoTracking()
                .Where(d => d.CityId == cityId)
                .Select(d => new { d.DistrictId, d.Name, Centroid = d.Geom.Centroid })
                .ToListAsync(ct);

            var shortlistIds = picks.Where(p => p.Status == DistrictPickStatus.Shortlist).Select(p => p.DistrictId).ToHashSet();
            var focus = shortlistIds.Count > 0
                ? districts.Where(d => shortlistIds.Contains(d.DistrictId)).ToList()
                : districts;

            var rows = new List<DistrictCompareRowDto>();
            foreach (var d in focus)
            {
                var pick = picks.FirstOrDefault(p => p.DistrictId == d.DistrictId);
                double? worstCommute = null;
                foreach (var a in anchors)
                {
                    var (min, _) = await geo.DriveAsync(d.Centroid.Y, d.Centroid.X, a.Lat, a.Lon, ct);
                    if (min is not null && (worstCommute is null || min > worstCommute))
                        worstCommute = min;
                }
                comfortMap.TryGetValue(d.DistrictId, out var comfort);
                var ranked = RankScore(profile, worstCommute, pick?.QuietScore, pick?.ParkCount, comfort?.ScoreOverall);
                rows.Add(new DistrictCompareRowDto(
                    d.DistrictId, d.Name,
                    pick?.Status ?? DistrictPickStatus.Exploring,
                    pick?.VetoReason,
                    comfort?.ScoreOverall,
                    worstCommute,
                    pick?.QuietScore,
                    pick?.ParkCount,
                    pick?.ShopCount,
                    pick?.TransitStopCount,
                    pick?.NearestHighwayKm,
                    visitMap.GetValueOrDefault(d.DistrictId),
                    pick?.ReminderAt,
                    pick?.RiskNotes,
                    ranked));
            }

            return Results.Ok(rows.OrderByDescending(r => r.RankScore).ToList());
        });

        // --- Finalist matrix ---
        g.MapGet("/finalists", async (ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var list = await db.HousingOffers.AsNoTracking()
                .Where(o => o.UserId == uid && o.IsFinalist)
                .OrderBy(o => o.Title)
                .ToListAsync();
            return Results.Ok(list.Select(o =>
            {
                var dto = ToOfferDto(o);
                var deal = DealAvg(o);
                var monthly = MonthlyTotal(o);
                return new FinalistRowDto(dto, deal, monthly);
            }));
        });

        // --- Export CSV ---
        g.MapGet("/export.csv", async (ClaimsPrincipal user, AppDbContext db) =>
        {
            var uid = user.GetUserId();
            if (uid is null) return Results.Unauthorized();
            var picks = await db.DistrictPicks.AsNoTracking()
                .Where(p => p.UserId == uid)
                .Select(p => new { p.District.Name, p.Status, p.VetoReason, p.QuietScore, p.ParkCount, p.ShopCount })
                .ToListAsync();
            var offers = await db.HousingOffers.AsNoTracking().Where(o => o.UserId == uid).ToListAsync();

            var sb = new StringBuilder();
            sb.AppendLine("type,name,status,mode,price,sqm,monthly,dealAvg,notes");
            foreach (var p in picks)
                sb.AppendLine(Csv("district", p.Name, p.Status.ToString(), "", "", "", "", "", p.VetoReason ?? ""));
            foreach (var o in offers)
                sb.AppendLine(Csv("offer", o.Title, o.IsFinalist ? "finalist" : "", o.Mode.ToString(),
                    o.Price?.ToString(CultureInfo.InvariantCulture) ?? "",
                    o.Sqm?.ToString(CultureInfo.InvariantCulture) ?? "",
                    MonthlyTotal(o)?.ToString(CultureInfo.InvariantCulture) ?? "",
                    DealAvg(o)?.ToString(CultureInfo.InvariantCulture) ?? "",
                    o.KillerFlaw ?? ""));

            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", "citychecker-export.csv");
        });
    }

    static string Csv(params string?[] cells) =>
        string.Join(",", cells.Select(c => $"\"{(c ?? "").Replace("\"", "\"\"")}\""));

    static double? RankScore(DecisionProfile p, double? commuteMin, int? quiet, int? parks, double? comfort)
    {
        var wSum = p.WeightCommute + p.WeightQuiet + p.WeightPrice + p.WeightGreen + p.WeightComfort;
        if (wSum <= 0) wSum = 1;
        // commute: 60+ min → 0, 0 min → 10
        var commuteScore = commuteMin is null ? 5 : Math.Clamp(10 - commuteMin.Value / 6.0, 0, 10);
        var quietScore = quiet ?? 5;
        var greenScore = parks is null ? 5 : Math.Clamp(parks.Value, 0, 10);
        var comfortScore = comfort ?? 5;
        var priceScore = 5; // district-level price not stored; neutral
        return Math.Round(
            (p.WeightCommute * commuteScore + p.WeightQuiet * quietScore + p.WeightPrice * priceScore
             + p.WeightGreen * greenScore + p.WeightComfort * comfortScore) / wSum, 2);
    }

    static double? DealAvg(HousingOffer o)
    {
        var scores = new int?[]
        {
            o.ScorePrice, o.ScoreLayout, o.ScoreLight, o.ScoreCondition, o.ScoreNeighbors,
            o.ScoreHeating, o.ScoreBalcony, o.ScoreElevator, o.ScoreParking, o.ScoreCellar
        }.Where(s => s is not null).Select(s => s!.Value).ToList();
        return scores.Count == 0 ? null : Math.Round(scores.Average(), 1);
    }

    static decimal? MonthlyTotal(HousingOffer o)
    {
        var parts = new decimal?[] { o.RentOrMortgage, o.Media, o.Internet, o.ParkingFee, o.Czynsz };
        if (parts.All(p => p is null)) return null;
        return parts.Sum(p => p ?? 0);
    }

    static DistrictPickDto ToPickDto(DistrictPick p, string name, Guid cityId) => new(
        p.PickId, p.DistrictId, cityId, name, p.Status, p.VetoReason, p.QuietScore,
        p.ParkCount, p.ShopCount, p.PharmacyCount, p.SchoolCount, p.TransitStopCount,
        p.NearestHighwayKm, p.ReminderAt, p.ReminderNote, p.RiskNotes, p.UpdatedAt);

    static OfferDto ToOfferDto(HousingOffer o) => new(
        o.OfferId, o.CityId, o.DistrictId, o.BuildingId, o.Title, o.Url, o.Mode, o.Lat, o.Lon,
        o.Price, o.Sqm, o.Floor, o.Rooms, o.YearBuilt,
        o.RentOrMortgage, o.Media, o.Internet, o.ParkingFee, o.Czynsz, MonthlyTotal(o),
        o.ScorePrice, o.ScoreLayout, o.ScoreLight, o.ScoreCondition, o.ScoreNeighbors,
        o.ScoreHeating, o.ScoreBalcony, o.ScoreElevator, o.ScoreParking, o.ScoreCellar,
        DealAvg(o), o.KillerFlaw,
        o.PricePerSqm, o.RenovationBudget, o.HasKsiega, o.HasSluzebnosc, o.HasSpoldzielniaDebt,
        o.Deposit, o.NoticeDays, o.Furnished, o.LandlordNotes,
        o.PhotoUrls, o.VoiceNoteUrl, o.IsFinalist, o.ReminderAt, o.ReminderNote,
        o.CreatedAt, o.UpdatedAt);

    static HousingOffer FromWrite(OfferWriteDto body, string uid)
    {
        var o = new HousingOffer { UserId = uid };
        ApplyWrite(o, body);
        return o;
    }

    static void ApplyWrite(HousingOffer o, OfferWriteDto body)
    {
        o.CityId = body.CityId;
        o.DistrictId = body.DistrictId;
        o.BuildingId = body.BuildingId;
        o.Title = body.Title.Trim();
        o.Url = body.Url;
        o.Mode = body.Mode;
        o.Lat = body.Lat;
        o.Lon = body.Lon;
        o.Price = body.Price;
        o.Sqm = body.Sqm;
        o.Floor = body.Floor;
        o.Rooms = body.Rooms;
        o.YearBuilt = body.YearBuilt;
        o.RentOrMortgage = body.RentOrMortgage;
        o.Media = body.Media;
        o.Internet = body.Internet;
        o.ParkingFee = body.ParkingFee;
        o.Czynsz = body.Czynsz;
        o.ScorePrice = body.ScorePrice;
        o.ScoreLayout = body.ScoreLayout;
        o.ScoreLight = body.ScoreLight;
        o.ScoreCondition = body.ScoreCondition;
        o.ScoreNeighbors = body.ScoreNeighbors;
        o.ScoreHeating = body.ScoreHeating;
        o.ScoreBalcony = body.ScoreBalcony;
        o.ScoreElevator = body.ScoreElevator;
        o.ScoreParking = body.ScoreParking;
        o.ScoreCellar = body.ScoreCellar;
        o.KillerFlaw = body.KillerFlaw;
        o.PricePerSqm = body.PricePerSqm;
        o.RenovationBudget = body.RenovationBudget;
        o.HasKsiega = body.HasKsiega;
        o.HasSluzebnosc = body.HasSluzebnosc;
        o.HasSpoldzielniaDebt = body.HasSpoldzielniaDebt;
        o.Deposit = body.Deposit;
        o.NoticeDays = body.NoticeDays;
        o.Furnished = body.Furnished;
        o.LandlordNotes = body.LandlordNotes;
        o.PhotoUrls = body.PhotoUrls;
        o.VoiceNoteUrl = body.VoiceNoteUrl;
        o.IsFinalist = body.IsFinalist;
        o.ReminderAt = body.ReminderAt;
        o.ReminderNote = body.ReminderNote;
    }
}

public record AnchorDto(Guid AnchorId, string Label, double Lat, double Lon, int SortOrder);
public record AnchorWriteDto(string Label, double Lat, double Lon, int? SortOrder);
public record CommuteLegDto(Guid AnchorId, string Label, double? DriveMinutes, double? DriveKm);
public record DistrictCommuteDto(Guid DistrictId, string Name, double Lat, double Lon, IReadOnlyList<CommuteLegDto> Legs, double? WorstDriveMinutes);
public record DistrictPickDto(
    Guid PickId, Guid DistrictId, Guid CityId, string Name, DistrictPickStatus Status, string? VetoReason,
    int? QuietScore, int? ParkCount, int? ShopCount, int? PharmacyCount, int? SchoolCount, int? TransitStopCount,
    double? NearestHighwayKm, DateTime? ReminderAt, string? ReminderNote, string? RiskNotes, DateTime UpdatedAt);
public record PickWriteDto(
    DistrictPickStatus Status, string? VetoReason, int? QuietScore,
    DateTime? ReminderAt, string? ReminderNote, string? RiskNotes);
public record VisitDto(Guid VisitId, Guid DistrictId, DateTime VisitedAt, int? EveningFeel, int? Daylight, int? DogWalk, int? SaturdayLife, int? WinterFeel, string? Notes);
public record VisitWriteDto(Guid DistrictId, DateTime? VisitedAt, int? EveningFeel, int? Daylight, int? DogWalk, int? SaturdayLife, int? WinterFeel, string? Notes);
public record ProfileDto(int WeightCommute, int WeightQuiet, int WeightPrice, int WeightGreen, int WeightComfort);
public record DistrictCompareRowDto(
    Guid DistrictId, string Name, DistrictPickStatus Status, string? VetoReason,
    double? ComfortAvg, double? WorstCommuteMin, int? QuietScore,
    int? ParkCount, int? ShopCount, int? TransitStopCount, double? NearestHighwayKm,
    int VisitCount, DateTime? ReminderAt, string? RiskNotes, double? RankScore);
public record OfferDto(
    Guid OfferId, Guid? CityId, Guid? DistrictId, Guid? BuildingId, string Title, string? Url, OfferMode Mode,
    double Lat, double Lon, decimal? Price, double? Sqm, int? Floor, int? Rooms, int? YearBuilt,
    decimal? RentOrMortgage, decimal? Media, decimal? Internet, decimal? ParkingFee, decimal? Czynsz, decimal? MonthlyTotal,
    int? ScorePrice, int? ScoreLayout, int? ScoreLight, int? ScoreCondition, int? ScoreNeighbors,
    int? ScoreHeating, int? ScoreBalcony, int? ScoreElevator, int? ScoreParking, int? ScoreCellar,
    double? DealAvg, string? KillerFlaw,
    decimal? PricePerSqm, decimal? RenovationBudget, bool? HasKsiega, bool? HasSluzebnosc, bool? HasSpoldzielniaDebt,
    decimal? Deposit, int? NoticeDays, bool? Furnished, string? LandlordNotes,
    string? PhotoUrls, string? VoiceNoteUrl, bool IsFinalist, DateTime? ReminderAt, string? ReminderNote,
    DateTime CreatedAt, DateTime? UpdatedAt);
public record OfferWriteDto(
    Guid? CityId, Guid? DistrictId, Guid? BuildingId, string Title, string? Url, OfferMode Mode,
    double Lat, double Lon, decimal? Price, double? Sqm, int? Floor, int? Rooms, int? YearBuilt,
    decimal? RentOrMortgage, decimal? Media, decimal? Internet, decimal? ParkingFee, decimal? Czynsz,
    int? ScorePrice, int? ScoreLayout, int? ScoreLight, int? ScoreCondition, int? ScoreNeighbors,
    int? ScoreHeating, int? ScoreBalcony, int? ScoreElevator, int? ScoreParking, int? ScoreCellar,
    string? KillerFlaw,
    decimal? PricePerSqm, decimal? RenovationBudget, bool? HasKsiega, bool? HasSluzebnosc, bool? HasSpoldzielniaDebt,
    decimal? Deposit, int? NoticeDays, bool? Furnished, string? LandlordNotes,
    string? PhotoUrls, string? VoiceNoteUrl, bool IsFinalist, DateTime? ReminderAt, string? ReminderNote);
public record FinalistRowDto(OfferDto Offer, double? DealAvg, decimal? MonthlyTotal);
