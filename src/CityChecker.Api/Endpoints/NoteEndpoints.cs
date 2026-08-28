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
    public const int DefaultPointRadiusMeters = 50;
    public const int DefaultBuildingRadiusMeters = 15;
    public const int MaxNotePhotos = 4;
    public const int MaxPhotoUrlsLength = 2000;
    public const int MinPointRadiusMeters = 50;
    public const int MinBuildingRadiusMeters = 5;
    public const int MaxPointRadiusMeters = 2000;

    public static void MapNoteEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/notes").RequireAuthorization();

        group.MapGet("/access", async (ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            var email = await user.ResolveUserEmailAsync(db);
            return Results.Ok(new
            {
                userId = user.GetUserId(),
                isAdmin = NotesAccess.IsAdmin(config, email),
            });
        });

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
            if (districtId is not null)
            {
                // Include building notes pinned to a building inside this district (even if TargetDistrictId unset)
                q = q.Where(n =>
                    n.TargetDistrictId == districtId ||
                    (n.Level == NoteLevel.Building && n.TargetBuildingId != null &&
                     db.Buildings.Any(b => b.BuildingId == n.TargetBuildingId && b.DistrictId == districtId)));
            }
            if (buildingId is not null) q = q.Where(n => n.TargetBuildingId == buildingId);
            if (level is not null) q = q.Where(n => n.Level == level);

            var notes = await q.OrderByDescending(n => n.CreatedAt).ToListAsync();
            // Enrich building notes missing coords from Buildings row
            var needCoords = notes
                .Where(n => n.Level == NoteLevel.Building && n.TargetBuildingId != null &&
                            (n.Lat is null || n.Lon is null || n.TargetDistrictId is null))
                .Select(n => n.TargetBuildingId!.Value)
                .Distinct()
                .ToList();
            Dictionary<Guid, Building>? byId = null;
            if (needCoords.Count > 0)
            {
                byId = await db.Buildings.AsNoTracking()
                    .Where(b => needCoords.Contains(b.BuildingId))
                    .ToDictionaryAsync(b => b.BuildingId);
            }
            return Results.Ok(notes.Select(n => ToDto(n, byId)));
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
            if (await user.EnsureCanModifyNoteAsync(db, config, note.AuthorGoogleId) is { } denied)
                return denied;

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
            note.PhotoUrls = built.PhotoUrls;
            note.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(ToDto(note));
        });

        group.MapDelete("/{noteId:guid}", async (Guid noteId, ClaimsPrincipal user, IConfiguration config, AppDbContext db) =>
        {
            if (user.EnsureOwner(config) is { } err) return err;
            var note = await db.Notes.FirstOrDefaultAsync(n => n.NoteId == noteId);
            if (note is null) return Results.NotFound();
            if (await user.EnsureCanModifyNoteAsync(db, config, note.AuthorGoogleId) is { } denied)
                return denied;

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
        if (NormalizePhotoUrls(body.PhotoUrls) is null && !string.IsNullOrWhiteSpace(body.PhotoUrls))
            return Results.BadRequest(new { error = "PhotoUrls must be up to 4 Cloudinary HTTPS links." });

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
            NoteLevel.Building when body.RadiusMeters is not null and (< MinBuildingRadiusMeters or > MaxPointRadiusMeters)
                => Results.BadRequest(new { error = $"RadiusMeters must be {MinBuildingRadiusMeters}–{MaxPointRadiusMeters}." }),
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
        else if (body.Level == NoteLevel.Building)
        {
            // ponytail: same map/district effect as Point — pin at building coords
            var bld = await db.Buildings.AsNoTracking()
                .FirstOrDefaultAsync(b => b.BuildingId == body.TargetBuildingId);
            if (bld is null)
                throw new InvalidOperationException("Building not found.");
            lat = bld.Lat;
            lon = bld.Lon;
            radius = body.RadiusMeters ?? DefaultBuildingRadiusMeters;
            districtId = bld.DistrictId
                ?? await ResolveDistrictAsync(db, body.TargetCityId, lat.Value, lon.Value);
        }

        return new Note
        {
            AuthorGoogleId = googleId,
            Level = body.Level,
            TargetCityId = body.TargetCityId,
            TargetDistrictId = body.Level is NoteLevel.Point or NoteLevel.Building
                ? districtId
                : body.TargetDistrictId,
            TargetBuildingId = body.Level == NoteLevel.Building ? body.TargetBuildingId : null,
            Lat = lat,
            Lon = lon,
            RadiusMeters = radius,
            Text = body.Text.Trim(),
            ScoreOverall = body.ScoreOverall,
            ScoreNature = body.ScoreNature,
            ScoreShops = body.ScoreShops,
            ScoreTransport = body.ScoreTransport,
            ScoreSafety = body.ScoreSafety,
            PhotoUrls = NormalizePhotoUrls(body.PhotoUrls),
        };
    }

    static string? NormalizePhotoUrls(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > MaxNotePhotos) return null;
        foreach (var p in parts)
        {
            if (!Uri.TryCreate(p, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !uri.Host.Equals("res.cloudinary.com", StringComparison.OrdinalIgnoreCase)
                || p.Length > 500)
                return null;
        }
        var joined = string.Join(",", parts);
        return joined.Length > MaxPhotoUrlsLength ? null : joined;
    }

    static async Task<Guid?> ResolveDistrictAsync(AppDbContext db, Guid cityId, double lat, double lon)
    {
        var point = new Point(lon, lat) { SRID = 4326 };
        return await db.Districts.AsNoTracking()
            .Where(d => d.CityId == cityId && d.Geom.Contains(point))
            .Select(d => (Guid?)d.DistrictId)
            .FirstOrDefaultAsync();
    }

    static NoteDto ToDto(Note n, IReadOnlyDictionary<Guid, Building>? buildings = null)
    {
        var lat = n.Lat;
        var lon = n.Lon;
        var districtId = n.TargetDistrictId;
        var radius = n.RadiusMeters;
        if (n.Level == NoteLevel.Building && n.TargetBuildingId is Guid bid &&
            buildings is not null && buildings.TryGetValue(bid, out var bld))
        {
            lat ??= bld.Lat;
            lon ??= bld.Lon;
            districtId ??= bld.DistrictId;
            radius ??= DefaultBuildingRadiusMeters;
        }
        return new(
            n.NoteId, n.AuthorGoogleId, n.Level, n.TargetCityId, districtId, n.TargetBuildingId,
            lat, lon, radius,
            n.Text, n.PhotoUrls, n.ScoreOverall, n.ScoreNature, n.ScoreShops, n.ScoreTransport, n.ScoreSafety,
            n.CreatedAt, n.UpdatedAt);
    }
}
