using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using CityChecker.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Services;

public class AggregateService(AppDbContext db)
{
    public async Task<AggregateDto> ForCityAsync(Guid cityId, CancellationToken ct = default)
    {
        var q = db.Notes.AsNoTracking().Where(n => n.TargetCityId == cityId && n.Level == NoteLevel.City);
        return await AvgAsync(q, ct);
    }

    public async Task<AggregateDto> ForDistrictAsync(Guid districtId, CancellationToken ct = default)
    {
        // Point notes + building notes (by TargetDistrictId or Building.DistrictId)
        var q = db.Notes.AsNoTracking().Where(n =>
            (n.TargetDistrictId == districtId && (n.Level == NoteLevel.Point || n.Level == NoteLevel.Building)) ||
            (n.Level == NoteLevel.Building && n.TargetBuildingId != null &&
             db.Buildings.Any(b => b.BuildingId == n.TargetBuildingId && b.DistrictId == districtId)));
        return await AvgAsync(q, ct);
    }

    public async Task<AggregateDto> ForBuildingAsync(Guid buildingId, CancellationToken ct = default)
    {
        var q = db.Notes.AsNoTracking().Where(n => n.TargetBuildingId == buildingId && n.Level == NoteLevel.Building);
        return await AvgAsync(q, ct);
    }

    public async Task<CityAggregatesDto> ForCityBatchAsync(Guid cityId, CancellationToken ct = default)
    {
        var cityAgg = await ForCityAsync(cityId, ct);

        // ponytail: per-district Avg reuses ForDistrictAsync (incl. building-via-Building.DistrictId)
        var districtIds = await db.Districts.AsNoTracking()
            .Where(d => d.CityId == cityId)
            .Select(d => d.DistrictId)
            .ToListAsync(ct);
        var districtRows = new List<IdAggregateDto>();
        foreach (var id in districtIds)
        {
            var a = await ForDistrictAsync(id, ct);
            if (a.NoteCount > 0)
            {
                districtRows.Add(new IdAggregateDto(
                    id, a.ScoreOverall, a.ScoreNature, a.ScoreShops, a.ScoreTransport, a.ScoreSafety, a.NoteCount));
            }
        }

        var buildingRows = await db.Notes.AsNoTracking()
            .Where(n => n.TargetCityId == cityId && n.Level == NoteLevel.Building && n.TargetBuildingId != null)
            .GroupBy(n => n.TargetBuildingId!.Value)
            .Select(g => new
            {
                Id = g.Key,
                NoteCount = g.Count(),
                ScoreOverall = g.Average(n => (double)n.ScoreOverall),
                ScoreNature = g.Where(n => n.ScoreNature != null).Average(n => (double?)n.ScoreNature),
                ScoreShops = g.Where(n => n.ScoreShops != null).Average(n => (double?)n.ScoreShops),
                ScoreTransport = g.Where(n => n.ScoreTransport != null).Average(n => (double?)n.ScoreTransport),
                ScoreSafety = g.Where(n => n.ScoreSafety != null).Average(n => (double?)n.ScoreSafety),
            })
            .ToListAsync(ct);

        return new CityAggregatesDto(
            cityAgg,
            districtRows,
            buildingRows.Select(r => new IdAggregateDto(
                r.Id, Round(r.ScoreOverall), Round(r.ScoreNature), Round(r.ScoreShops),
                Round(r.ScoreTransport), Round(r.ScoreSafety), r.NoteCount)).ToList());
    }

    static async Task<AggregateDto> AvgAsync(IQueryable<Note> q, CancellationToken ct)
    {
        var count = await q.CountAsync(ct);
        if (count == 0)
            return new AggregateDto(null, null, null, null, null, 0);

        var overall = await q.AverageAsync(n => (double)n.ScoreOverall, ct);
        return new AggregateDto(
            Round(overall),
            Round(await AvgOptionalAsync(q, n => n.ScoreNature, ct)),
            Round(await AvgOptionalAsync(q, n => n.ScoreShops, ct)),
            Round(await AvgOptionalAsync(q, n => n.ScoreTransport, ct)),
            Round(await AvgOptionalAsync(q, n => n.ScoreSafety, ct)),
            count);
    }

    static async Task<double?> AvgOptionalAsync(IQueryable<Note> q, System.Linq.Expressions.Expression<Func<Note, int?>> selector, CancellationToken ct)
    {
        var values = q.Select(selector).Where(v => v != null);
        if (!await values.AnyAsync(ct)) return null;
        return await values.AverageAsync(ct);
    }

    static double? Round(double? v) => v is null ? null : Math.Round(v.Value, 2);
}
