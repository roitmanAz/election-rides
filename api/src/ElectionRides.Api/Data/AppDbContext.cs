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
    public DbSet<Person> People => Set<Person>();
    public DbSet<PersonPhone> PersonPhones => Set<PersonPhone>();
    public DbSet<RideRequest> Requests => Set<RideRequest>();
    public DbSet<RequestPassenger> Passengers => Set<RequestPassenger>();
    public DbSet<AddressLocation> AddressLocations => Set<AddressLocation>();

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

        // Intake (spec 5.1)
        b.Entity<Person>(e =>
        {
            e.Property(x => x.FullName).HasMaxLength(100);
            e.Property(x => x.IdNumber).HasMaxLength(9);
            e.HasIndex(x => new { x.CityId, x.IdNumber }).IsUnique().HasFilter("\"IdNumber\" IS NOT NULL");
            e.HasMany(x => x.Phones).WithOne().HasForeignKey(x => x.PersonId);
        });
        b.Entity<PersonPhone>(e =>
        {
            e.HasKey(x => new { x.PersonId, x.Phone });
            e.Property(x => x.Phone).HasMaxLength(10);
            e.HasIndex(x => x.Phone);
        });
        b.Entity<RideRequest>(e =>
        {
            e.HasIndex(x => new { x.CityId, x.OperationId, x.Number }).IsUnique();
            e.HasIndex(x => new { x.CityId, x.Status });
            e.HasIndex(x => x.CallerPhone);
            e.Property(x => x.CallerPhone).HasMaxLength(10);
            e.Property(x => x.BuildingNumber).HasMaxLength(10);
            e.Property(x => x.Entrance).HasMaxLength(20);
            e.Property(x => x.DriverNotes).HasMaxLength(300);
            e.Property(x => x.UrgencyReason).HasMaxLength(120);
            e.Property(x => x.CancelReason).HasMaxLength(200);
            e.Property(x => x.Location).HasColumnType("geography (point, 4326)");
            e.HasOne(x => x.Street).WithMany().HasForeignKey(x => x.StreetId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Passengers).WithOne().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<RequestPassenger>(e =>
        {
            e.HasOne(x => x.Person).WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
            e.Property(x => x.BallotBox).HasMaxLength(10);
        });
        b.Entity<AddressLocation>(e =>
        {
            e.HasKey(x => new { x.StreetId, x.BuildingNumber });
            e.Property(x => x.BuildingNumber).HasMaxLength(10);
            e.Property(x => x.Location).HasColumnType("geography (point, 4326)");
        });
    }
}
