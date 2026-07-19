using System.Text.Json.Serialization;
using CityChecker.Api.Data;
using CityChecker.Api.Endpoints;
using CityChecker.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(
        builder.Configuration.GetConnectionString("Default"),
        npgsql => npgsql.UseNetTopologySuite()));

builder.Services.Configure<NominatimOptions>(builder.Configuration.GetSection(NominatimOptions.Section));
builder.Services.Configure<ImportOptions>(builder.Configuration.GetSection(ImportOptions.Section));
builder.Services.AddHttpClient<NominatimClient>();
builder.Services.AddScoped<BuildingService>();
builder.Services.AddScoped<AggregateService>();
builder.Services.AddScoped<LodzDistrictImportService>();
builder.Services.AddScoped<PolygonDistrictImportService>();

var googleClientId = builder.Configuration["Google:ClientId"] ?? "";
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://accounts.google.com";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
            ValidateAudience = true,
            ValidAudience = googleClientId,
            ValidateLifetime = true,
            NameClaimType = "sub"
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

if (app.Environment.IsDevelopment())
    GeoHelper.SelfCheck();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await SeedData.EnsureSeededAsync(db);

    var lodzImporter = scope.ServiceProvider.GetRequiredService<LodzDistrictImportService>();
    var polyImporter = scope.ServiceProvider.GetRequiredService<PolygonDistrictImportService>();

    if (!await db.Districts.AnyAsync(d => d.CityId == SeedData.LodzId))
    {
        try
        {
            var result = await lodzImporter.ImportAsync();
            app.Logger.LogInformation(
                "Auto-imported Łódź districts: {Transformed}/{Unique} (skipped {Skipped})",
                result.Transformed, result.UniqueNames, result.Skipped);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "Auto-import of Łódź districts skipped");
        }
    }

    try
    {
        await polyImporter.ImportForCityAsync(
            SeedData.KrakowId, "DataImports/krakow-districts-polygons.json", "OSM Kraków dzielnice");
        await polyImporter.ImportForCityAsync(
            SeedData.WarszawaId, "DataImports/warszawa-districts-polygons.json", "OSM Warszawa dzielnice");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Auto-import of Kraków/Warszawa districts skipped");
    }
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/config", (IConfiguration config) => Results.Ok(new
{
    googleClientId = config["Google:ClientId"]
}));

app.MapCityEndpoints();
app.MapDistrictEndpoints();
app.MapBuildingEndpoints();
app.MapNoteEndpoints();
app.MapAggregateEndpoints();
app.MapAdminEndpoints();

app.MapFallbackToFile("index.html");

app.Run();
