using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using SeatFlow.Messaging.Outbox;
using SeatFlow.Payments.Domain;

namespace SeatFlow.Payments.Data;

public sealed class PaymentDbContext(DbContextOptions<PaymentDbContext> options) : DbContext(options)
{
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payments");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.UserId).HasMaxLength(100);
            b.Property(x => x.Amount).HasPrecision(12, 2);
            b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength();
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.ProviderReference).HasMaxLength(100);
            b.Property(x => x.FailureReason).HasMaxLength(200);
            b.Property(x => x.RefundReason).HasMaxLength(500);
            b.Property(x => x.Version).IsRowVersion();
            b.HasIndex(x => x.BookingId).IsUnique();
        });

        modelBuilder.AddOutboxAndInbox();
    }
}

internal sealed class PaymentDbContextDesignFactory : IDesignTimeDbContextFactory<PaymentDbContext>
{
    public PaymentDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PaymentDbContext>()
            .UseNpgsql("Host=localhost;Database=payment")
            .UseSnakeCaseNamingConvention()
            .Options);
}
