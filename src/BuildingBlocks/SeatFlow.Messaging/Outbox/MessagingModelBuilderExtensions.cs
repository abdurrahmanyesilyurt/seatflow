using Microsoft.EntityFrameworkCore;

namespace SeatFlow.Messaging.Outbox;

public static class MessagingModelBuilderExtensions
{
    /// <summary>Adds the outbox and inbox tables to a service's DbContext. Names are explicit because the relay uses raw SQL.</summary>
    public static ModelBuilder AddOutboxAndInbox(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages");
            b.HasKey(x => x.Id).HasName("pk_outbox_messages");
            b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(x => x.MessageType).HasColumnName("message_type").HasMaxLength(200);
            b.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
            b.Property(x => x.Mandatory).HasColumnName("mandatory");
            b.Property(x => x.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
            b.Property(x => x.TraceParent).HasColumnName("trace_parent").HasMaxLength(100);
            b.Property(x => x.TraceState).HasColumnName("trace_state").HasMaxLength(500);
            b.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            b.Property(x => x.ProcessedAt).HasColumnName("processed_at");
            b.Property(x => x.NextAttemptAt).HasColumnName("next_attempt_at");
            b.Property(x => x.Attempts).HasColumnName("attempts");
            b.Property(x => x.LastError).HasColumnName("last_error").HasMaxLength(2000);

            // Partial index: the relay only ever scans unpublished rows, so the index stays tiny.
            b.HasIndex(x => x.OccurredAt).HasDatabaseName("ix_outbox_messages_pending").HasFilter("processed_at IS NULL");
        });

        modelBuilder.Entity<InboxMessage>(b =>
        {
            b.ToTable("inbox_messages");
            b.HasKey(x => new { x.MessageId, x.Consumer }).HasName("pk_inbox_messages");
            b.Property(x => x.MessageId).HasColumnName("message_id");
            b.Property(x => x.Consumer).HasColumnName("consumer").HasMaxLength(200);
            b.Property(x => x.ProcessedAt).HasColumnName("processed_at");
        });

        return modelBuilder;
    }
}
