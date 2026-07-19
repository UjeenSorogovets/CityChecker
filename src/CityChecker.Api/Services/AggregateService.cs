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
        var q = db.Notes.AsNoTracking().Where(n =>
            (n.Level == NoteLevel.District && n.TargetDistrictId == districtId) ||
            (n.Level == NoteLevel.Building && n.TargetDistrictId == districtId));
        return await AvgAsync(q, ct);
    }

    public async Task<AggregateDto> ForBuildingAsync(Guid buildingId, CancellationToken ct = default)
    {
        var q = db.Notes.AsNoTracking().Where(n => n.TargetBuildingId == buildingId && n.Level == NoteLevel.Building);
        return await AvgAsync(q, ct);
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
