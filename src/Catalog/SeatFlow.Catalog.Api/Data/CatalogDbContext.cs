using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SeatFlow.Catalog.Domain;
using SeatFlow.Messaging.Outbox;

namespace SeatFlow.Catalog.Data;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Venue> Venues => Set<Venue>();

    public DbSet<Event> Events => Set<Event>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Venue>(b =>
        {
            b.HasKey(v => v.Id);
            b.Property(v => v.Id).ValueGeneratedNever();
            b.Property(v => v.Name).HasMaxLength(200);
            b.Property(v => v.City).HasMaxLength(100);
            b.HasIndex(v => v.Name).IsUnique();
            b.Ignore(v => v.Capacity);
            b.OwnsMany(v => v.Sections, s => s.ToJson());
        });

        modelBuilder.Entity<Event>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).ValueGeneratedNever();
            b.Property(e => e.Title).HasMaxLength(200);
            b.Property(e => e.Description).HasMaxLength(2000);
            b.Property(e => e.Currency).HasMaxLength(3).IsFixedLength();
            b.HasOne(e => e.Venue).WithMany().HasForeignKey(e => e.VenueId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => e.StartsAt);
            b.OwnsMany(e => e.Prices, p => p.ToJson());
        });

        modelBuilder.AddOutboxAndInbox();
    }
}

/// <summary>Used by <c>dotnet ef migrations add</c> only; no database connection is opened.</summary>
internal sealed class CatalogDbContextDesignFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>()
            .UseNpgsql("Host=localhost;Database=catalog")
            .UseSnakeCaseNamingConvention()
            .Options);
}
