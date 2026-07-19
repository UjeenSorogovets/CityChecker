using CityChecker.Api.Data.Entities;

namespace CityChecker.Api.Dtos;

public record CityDto(Guid CityId, string Name, string Voivodeship, double CenterLat, double CenterLon, string? OfficialCode);

public record DistrictListDto(
    Guid DistrictId,
    Guid CityId,
    string Name,
    string? OfficialCode,
    string? SourceName,
    double? AreaKm2);

public record DistrictDetailDto(
    Guid DistrictId,
    Guid CityId,
    string Name,
    string? OfficialCode,
    string? SourceName,
    double? AreaKm2,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record BuildingDto(Guid BuildingId, Guid CityId, Guid? DistrictId, string AddressLine, double Lat, double Lon);

public record NoteDto(
    Guid NoteId,
    string AuthorGoogleId,
    NoteLevel Level,
    Guid TargetCityId,
    Guid? TargetDistrictId,
    Guid? TargetBuildingId,
    string Text,
    int ScoreOverall,
    int? ScoreNature,
    int? ScoreShops,
    int? ScoreTransport,
    int? ScoreSafety,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

public record NoteWriteDto(
    NoteLevel Level,
    Guid TargetCityId,
    Guid? TargetDistrictId,
    Guid? TargetBuildingId,
    string Text,
    int ScoreOverall,
    int? ScoreNature,
    int? ScoreShops,
    int? ScoreTransport,
    int? ScoreSafety);

public record ReverseGeocodeRequest(double Lat, double Lon);

public record AggregateDto(
    double? ScoreOverall,
    double? ScoreNature,
    double? ScoreShops,
    double? ScoreTransport,
    double? ScoreSafety,
    int NoteCount);
