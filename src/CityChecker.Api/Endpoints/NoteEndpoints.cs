using System.Security.Claims;
using CityChecker.Api.Auth;
using CityChecker.Api.Data;
using CityChecker.Api.Data.Entities;
using CityChecker.Api.Dtos;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Endpoints;

public static class NoteEndpoints
{
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
            var note = FromWrite(body, googleId);
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

            note.Level = body.Level;
            note.TargetCityId = body.TargetCityId;
            note.TargetDistrictId = body.TargetDistrictId;
            note.TargetBuildingId = body.TargetBuildingId;
            note.Text = body.Text.Trim();
            note.ScoreOverall = body.ScoreOverall;
            note.ScoreNature = body.ScoreNature;
            note.ScoreShops = body.ScoreShops;
            note.ScoreTransport = body.ScoreTransport;
            note.ScoreSafety = body.ScoreSafety;
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
            NoteLevel.City when body.TargetDistrictId is not null || body.TargetBuildingId is not null
                => Results.BadRequest(new { error = "City notes must not set district/building." }),
            NoteLevel.District when body.TargetDistrictId is null
                => Results.BadRequest(new { error = "District notes require targetDistrictId." }),
            NoteLevel.Building when body.TargetBuildingId is null
                => Results.BadRequest(new { error = "Building notes require targetBuildingId." }),
            _ => null
        };
    }

    static bool ScoreOk(int? s) => s is null or (>= 1 and <= 10);

    static Note FromWrite(NoteWriteDto body, string googleId) => new()
    {
        AuthorGoogleId = googleId,
        Level = body.Level,
        TargetCityId = body.TargetCityId,
        TargetDistrictId = body.TargetDistrictId,
        TargetBuildingId = body.TargetBuildingId,
        Text = body.Text.Trim(),
        ScoreOverall = body.ScoreOverall,
        ScoreNature = body.ScoreNature,
        ScoreShops = body.ScoreShops,
        ScoreTransport = body.ScoreTransport,
        ScoreSafety = body.ScoreSafety
    };

    // EF projection needs expression-compatible mapper — use after materialize for PUT/POST; for GET use Select
    static NoteDto ToDto(Note n) => new(
        n.NoteId, n.AuthorGoogleId, n.Level, n.TargetCityId, n.TargetDistrictId, n.TargetBuildingId,
        n.Text, n.ScoreOverall, n.ScoreNature, n.ScoreShops, n.ScoreTransport, n.ScoreSafety,
        n.CreatedAt, n.UpdatedAt);
}
