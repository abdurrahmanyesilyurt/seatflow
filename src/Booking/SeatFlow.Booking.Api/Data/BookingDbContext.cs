using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SeatFlow.Bookings.Domain;
using SeatFlow.Messaging.Outbox;

namespace SeatFlow.Bookings.Data;

public sealed class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    public const string PrimaryKeyName = "pk_bookings";
    public const string ActiveSeatIndexName = "ux_booking_seats_active_seat";

    public DbSet<Booking> Bookings => Set<Booking>();

    public DbSet<BookingSeat> BookingSeats => Set<BookingSeat>();

    public DbSet<EventInfo> Events => Set<EventInfo>();

    public DbSet<EventSeat> EventSeats => Set<EventSeat>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Booking>(b =>
        {
            b.ToTable("bookings");
            b.HasKey(x => x.Id).HasName(PrimaryKeyName);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.UserId).HasMaxLength(100);
            b.Property(x => x.UserEmail).HasMaxLength(320);
            b.Property(x => x.EventTitle).HasMaxLength(200);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.TotalAmount).HasPrecision(12, 2);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.RequestHash).HasMaxLength(64);
            b.Property(x => x.StatusReason).HasMaxLength(500);
            b.Property(x => x.Version).IsRowVersion();
            b.Ignore(x => x.SeatIds);

            b.HasMany(x => x.Seats).WithOne().HasForeignKey(s => s.BookingId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Seats).UsePropertyAccessMode(PropertyAccessMode.Field);

            b.HasIndex(x => new { x.UserId, x.CreatedAt });
            b.HasIndex(x => new { x.Status, x.HoldExpiresAt }); // expiry worker scan
        });

        modelBuilder.Entity<BookingSeat>(b =>
        {
            b.ToTable("booking_seats");
            b.HasKey(x => new { x.BookingId, x.SeatId });
            b.Property(x => x.SeatId).HasMaxLength(50);
            b.Property(x => x.Price).HasPrecision(12, 2);

            // The last line of defence against double-selling: at most one active booking per seat.
            b.HasIndex(x => new { x.EventId, x.SeatId })
                .IsUnique()
                .HasFilter("is_active")
                .HasDatabaseName(ActiveSeatIndexName);
        });

        modelBuilder.Entity<EventInfo>(b =>
        {
            b.ToTable("events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Title).HasMaxLength(200);
            b.Property(x => x.VenueName).HasMaxLength(200);
            b.Property(x => x.City).HasMaxLength(100);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
        });

        modelBuilder.Entity<EventSeat>(b =>
        {
            b.ToTable("event_seats");
            b.HasKey(x => new { x.EventId, x.SeatId });
            b.Property(x => x.SeatId).HasMaxLength(50);
            b.Property(x => x.Section).HasMaxLength(50);
            b.Property(x => x.Price).HasPrecision(12, 2);
        });

        modelBuilder.AddOutboxAndInbox();
    }
}

internal sealed class BookingDbContextDesignFactory : IDesignTimeDbContextFactory<BookingDbContext>
{
    public BookingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<BookingDbContext>()
            .UseNpgsql("Host=localhost;Database=booking")
            .UseSnakeCaseNamingConvention()
            .Options);
}
