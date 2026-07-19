using CityChecker.Api.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CityChecker.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<City> Cities => Set<City>();
    public DbSet<District> Districts => Set<District>();
    public DbSet<Building> Buildings => Set<Building>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<DistrictImportRaw> DistrictsImportRaw => Set<DistrictImportRaw>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("postgis");

        modelBuilder.Entity<City>(e =>
        {
            e.HasKey(x => x.CityId);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Voivodeship).HasMaxLength(100).IsRequired();
            e.Property(x => x.OfficialCode).HasMaxLength(32);
        });

        modelBuilder.Entity<District>(e =>
        {
            e.HasKey(x => x.DistrictId);
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.OfficialCode).HasMaxLength(64);
            e.Property(x => x.SourceName).HasMaxLength(200);
            e.Property(x => x.Geom)
                .HasColumnType("geometry(MultiPolygon, 4326)")
                .IsRequired();
            e.HasOne(x => x.City).WithMany(c => c.Districts).HasForeignKey(x => x.CityId);
            e.HasIndex(x => new { x.CityId, x.Name });
            e.HasIndex(x => x.Geom).HasMethod("GIST");
        });

        modelBuilder.Entity<DistrictImportRaw>(e =>
        {
            e.ToTable("districts_import_raw");
            e.HasKey(x => x.Id);
            e.Property(x => x.Osiedla).HasMaxLength(200);
            e.Property(x => x.PunktyGraniczne).HasMaxLength(500);
        });

        modelBuilder.Entity<Building>(e =>
        {
            e.HasKey(x => x.BuildingId);
            e.Property(x => x.AddressLine).HasMaxLength(300).IsRequired();
            e.HasOne(x => x.City).WithMany(c => c.Buildings).HasForeignKey(x => x.CityId);
            e.HasOne(x => x.District).WithMany(d => d.Buildings).HasForeignKey(x => x.DistrictId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.CityId, x.AddressLine });
            e.HasIndex(x => new { x.Lat, x.Lon });
        });

        modelBuilder.Entity<AppUser>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.Email).HasMaxLength(320).IsRequired();
            e.Property(x => x.PasswordHash).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<Note>(e =>
        {
            e.HasKey(x => x.NoteId);
            e.Property(x => x.AuthorGoogleId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Text).HasMaxLength(4000).IsRequired();
            e.HasOne(x => x.TargetCity).WithMany().HasForeignKey(x => x.TargetCityId);
            e.HasOne(x => x.TargetDistrict).WithMany().HasForeignKey(x => x.TargetDistrictId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasOne(x => x.TargetBuilding).WithMany().HasForeignKey(x => x.TargetBuildingId)
                .OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.TargetCityId, x.Level });
            e.HasIndex(x => x.TargetDistrictId);
            e.HasIndex(x => x.TargetBuildingId);
            e.HasIndex(x => x.AuthorGoogleId);
        });
    }
}
