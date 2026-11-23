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
    }
}
