using System.Security.Claims;
using CityChecker.Api.Auth;
using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using CityChecker.Api.Dtos;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace CityChecker.Api.Endpoints;

public static class NoteEndpoints
{
    public const int DefaultPointRadiusMeters = 300;
    public const int MinPointRadiusMeters = 50;
    public const int MaxPointRadiusMeters = 2000;

    public static void MapNoteEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/notes").RequireAuthorization();

        group.MapGet("/", async (
            ClaimsPrincipal user,
            IConfiguration config,
            AppDbContext db,
            Guid? cityId,
            Guid? districtId,
            Guid? buildingId,
            NoteLevel? level) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;

            var q = db.Notes.AsNoTracking().AsQueryable();
            if (cityId is not null) q = q.Where(n => n.TargetCityId == cityId);
            if (districtId is not null) q = q.Where(n => n.TargetDistrictId == districtId);
            if (buildingId is not null) q = q.Where(n => n.TargetBuildingId == buildingId);
            if (level is not null) q = q.Where(n => n.Level == level);

            var notes = await q.OrderByDescending(n => n.CreatedAt).ToListAsync();
            return Results.Ok(notes.Select(ToDto));
        });

        group.MapPost("/", async (NoteWriteDto body, ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            if (Validate(body) is { } bad) return bad;

            var googleId = user.GetGoogleUserId()!;
            var note = await FromWriteAsync(body, googleId, db);
            note.NoteId = Guid.NewGuid();
            note.CreatedAt = DateTime.UtcNow;
            db.Notes.Add(note);
            await db.SaveChangesAsync();
            return Results.Created($"/api/notes/{note.NoteId}", ToDto(note));
        });

        group.MapPut("/{noteId:guid}", async (Guid noteId, NoteWriteDto body, ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            if (Validate(body) is { } bad) return bad;

            var googleId = user.GetGoogleUserId()!;
            var note = await db.Notes.FirstOrDefaultAsync(n => n.NoteId == noteId);
            if (note is null) return Results.NotFound();
            if (!string.Equals(note.AuthorGoogleId, googleId, StringComparison.Ordinal))
                return Results.Forbid();

            var built = await FromWriteAsync(body, googleId, db);
            note.Level = built.Level;
            note.TargetCityId = built.TargetCityId;
            note.TargetDistrictId = built.TargetDistrictId;
            note.TargetBuildingId = built.TargetBuildingId;
            note.Lat = built.Lat;
            note.Lon = built.Lon;
            note.RadiusMeters = built.RadiusMeters;
            note.Text = built.Text;
            note.ScoreOverall = built.ScoreOverall;
            note.ScoreNature = built.ScoreNature;
            note.ScoreShops = built.ScoreShops;
            note.ScoreTransport = built.ScoreTransport;
            note.ScoreSafety = built.ScoreSafety;
            note.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(note));
        });

        group.MapDelete("/{noteId:guid}", async (Guid noteId, ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            var googleId = user.GetGoogleUserId()!;
            var note = await db.Notes.FirstOrDefaultAsync(n => n.NoteId == noteId);
            if (note is null) return Results.NotFound();
            if (!string.Equals(note.AuthorGoogleId, googleId, StringComparison.Ordinal))
                return Results.Forbid();

            db.Notes.Remove(note);
            await db.SaveChangesAsync();
            return Results.NoContent();
        });
    }

    static IResult? Validate(NoteWriteDto body)
    {
        if (string.IsNullOrWhiteSpace(body.Text) || body.Text.Length > 4000)
            return Results.BadRequest(new { error = "Text is required (max 4000 chars)." });
        if (body.ScoreOverall is < 1 or > 10)
            return Results.BadRequest(new { error = "ScoreOverall must be 1–10." });
        if (!ScoreOk(body.ScoreNature) || !ScoreOk(body.ScoreShops) || !ScoreOk(body.ScoreTransport) || !ScoreOk(body.ScoreSafety))
            return Results.BadRequest(new { error = "Optional scores must be 1–10 when set." });

        return body.Level switch
        {
            NoteLevel.City when body.TargetDistrictId is not null || body.TargetBuildingId is not null || body.Lat is not null || body.Lon is not null
                => Results.BadRequest(new { error = "City notes must not set district/building/coordinates." }),
            NoteLevel.Point when body.Lat is null || body.Lon is null
                => Results.BadRequest(new { error = "Point notes require lat and lon." }),
            NoteLevel.Point when body.Lat is < -90 or > 90 || body.Lon is < -180 or > 180
                => Results.BadRequest(new { error = "Invalid lat/lon." }),
            NoteLevel.Point when body.RadiusMeters is not null and (< MinPointRadiusMeters or > MaxPointRadiusMeters)
                => Results.BadRequest(new { error = $"RadiusMeters must be {MinPointRadiusMeters}–{MaxPointRadiusMeters}." }),
            NoteLevel.Building when body.TargetBuildingId is null
                => Results.BadRequest(new { error = "Building notes require targetBuildingId." }),
            _ => null
        };
    }

    static bool ScoreOk(int? s) => s is null or (>= 1 and <= 10);

    static async Task<Note> FromWriteAsync(NoteWriteDto body, string googleId, AppDbContext db)
    {
        Guid? districtId = body.TargetDistrictId;
        double? lat = null;
        double? lon = null;
        int? radius = null;

        if (body.Level == NoteLevel.Point)
        {
            lat = body.Lat;
            lon = body.Lon;
            radius = body.RadiusMeters ?? DefaultPointRadiusMeters;
            districtId = await ResolveDistrictAsync(db, body.TargetCityId, lat!.Value, lon!.Value);
        }

        return new Note
        {
            AuthorGoogleId = googleId,
            Level = body.Level,
            TargetCityId = body.TargetCityId,
            TargetDistrictId = body.Level == NoteLevel.Point ? districtId : body.TargetDistrictId,
            TargetBuildingId = body.Level == NoteLevel.Building ? body.TargetBuildingId : null,
            Lat = lat,
            Lon = lon,
            RadiusMeters = radius,
            Text = body.Text.Trim(),
            ScoreOverall = body.ScoreOverall,
            ScoreNature = body.ScoreNature,
            ScoreShops = body.ScoreShops,
            ScoreTransport = body.ScoreTransport,
            ScoreSafety = body.ScoreSafety
        };
    }

    static async Task<Guid?> ResolveDistrictAsync(AppDbContext db, Guid cityId, double lat, double lon)
    {
        var point = new Point(lon, lat) { SRID = 4326 };
        return await db.Districts.AsNoTracking()
            .Where(d => d.CityId == cityId && d.Geom.Contains(point))
            .Select(d => (Guid?)d.DistrictId)
            .FirstOrDefaultAsync();
    }

    static NoteDto ToDto(Note n) => new(
        n.NoteId, n.AuthorGoogleId, n.Level, n.TargetCityId, n.TargetDistrictId, n.TargetBuildingId,
        n.Lat, n.Lon, n.RadiusMeters,
        n.Text, n.ScoreOverall, n.ScoreNature, n.ScoreShops, n.ScoreTransport, n.ScoreSafety,
        n.CreatedAt, n.UpdatedAt);
}
