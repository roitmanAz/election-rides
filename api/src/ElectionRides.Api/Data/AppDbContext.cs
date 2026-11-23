using ElectionRides.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace ElectionRides.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<City> Cities => Set<City>();
    public DbSet<ElectionOperation> Operations => Set<ElectionOperation>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<PartySettings> PartySettings => Set<PartySettings>();
    public DbSet<LoginChallenge> LoginChallenges => Set<LoginChallenge>();
    public DbSet<TrustedDevice> TrustedDevices => Set<TrustedDevice>();
    public DbSet<PasswordToken> PasswordTokens => Set<PasswordToken>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<Community> Communities => Set<Community>();
    public DbSet<Street> Streets => Set<Street>();
    public DbSet<Cluster> Clusters => Set<Cluster>();
    public DbSet<BallotBox> BallotBoxes => Set<BallotBox>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // PostGIS is needed later for pickup locations and distances.
        b.HasPostgresExtension("postgis");

        b.Entity<City>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.HqPhone).HasMaxLength(10);
        });

        b.Entity<ElectionOperation>(e =>
        {
            // Only one active operation at a time, enforced by the database.
            e.HasIndex(x => x.IsActive).IsUnique().HasFilter("\"IsActive\" = true");
            e.HasMany(x => x.Cities).WithMany();
        });

        b.Entity<AppUser>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(254);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.FullName).HasMaxLength(100);
            e.HasOne(x => x.City).WithMany().HasForeignKey(x => x.CityId).OnDelete(DeleteBehavior.Restrict);
            // A system admin has no city; every other role must have one.
            e.ToTable(t => t.HasCheckConstraint("CK_User_CityByRole",
                $"(\"Role\" = {(int)UserRole.SystemAdmin} AND \"CityId\" IS NULL) OR (\"Role\" <> {(int)UserRole.SystemAdmin} AND \"CityId\" IS NOT NULL)"));
        });

        b.Entity<PartySettings>(e =>
        {
            e.ToTable(t => t.HasCheckConstraint("CK_PartySettings_Single", "\"Id\" = 1"));
            e.HasData(new PartySettings());
        });
        b.Entity<LoginChallenge>(e => e.HasIndex(x => x.UserId));
        b.Entity<TrustedDevice>(e => e.HasIndex(x => x.TokenHash).IsUnique());
        b.Entity<PasswordToken>(e => e.HasIndex(x => x.TokenHash).IsUnique());
        b.Entity<AuditEntry>(e =>
        {
            e.HasIndex(x => x.At);
            e.HasIndex(x => x.CityId);
        });

        // City lists: a name appears once per city.
        b.Entity<Community>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(60);
            e.HasIndex(x => new { x.CityId, x.Name }).IsUnique();
            e.HasOne<City>().WithMany().HasForeignKey(x => x.CityId);
        });
        b.Entity<Street>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80);
            e.HasIndex(x => new { x.CityId, x.Name }).IsUnique();
            e.HasOne<City>().WithMany().HasForeignKey(x => x.CityId);
        });
        b.Entity<Cluster>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.ShortName).HasMaxLength(14);
            e.Property(x => x.BuildingNumber).HasMaxLength(10);
            e.Property(x => x.DropoffNote).HasMaxLength(300);
            e.Property(x => x.Location).HasColumnType("geography (point, 4326)");
            e.HasIndex(x => new { x.CityId, x.ShortName }).IsUnique();
            e.HasIndex(x => new { x.CityId, x.Number }).IsUnique().HasFilter("\"Number\" IS NOT NULL");
            e.HasOne<City>().WithMany().HasForeignKey(x => x.CityId);
            e.HasOne(x => x.Street).WithMany().HasForeignKey(x => x.StreetId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Boxes).WithOne().HasForeignKey(x => x.ClusterId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<BallotBox>(e =>
        {
            e.Property(x => x.Number).HasMaxLength(10);
            // A ballot box belongs to one cluster in the city.
            e.HasIndex(x => new { x.CityId, x.Number }).IsUnique();
        });
    }
}
