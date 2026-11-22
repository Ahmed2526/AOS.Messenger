using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for UserConversationState entity.
/// Isolates per-user state (clear boundary, soft deletion, pin, archive, read marker).
/// Configures composite foreign key to Messages(ConversationId, Id) for read tracking with DeleteBehavior.Restrict.
/// </summary>
public sealed class UserConversationStateConfiguration : IEntityTypeConfiguration<UserConversationState>
{
    public void Configure(EntityTypeBuilder<UserConversationState> builder)
    {
        builder.ToTable("UserConversationStates");

        builder.HasKey(s => new { s.ConversationId, s.UserId }).IsClustered();

        builder.Property(s => s.ConversationId)
            .HasConversion<ConversationIdConverter>();

        builder.Property(s => s.UserId)
            .HasConversion<UserIdConverter>();

        builder.Property(s => s.LastDeliveredMessageId)
            .HasConversion<NullableMessageIdConverter>();

        builder.Property(s => s.LastDeliveredAtUtc);

        builder.Property(s => s.LastReadMessageId)
            .HasConversion<NullableMessageIdConverter>();

        builder.Property(s => s.LastReadAtUtc);

        builder.HasIndex(s => new { s.UserId, s.IsArchived, s.IsPinned });

        // Foreign key to Conversations (cascade delete)
        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(s => s.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite foreign key to Messages(ConversationId, Id) for delivery pointer
        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(s => new { s.ConversationId, s.LastDeliveredMessageId })
            .HasPrincipalKey(m => new { m.ConversationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // Composite foreign key to Messages(ConversationId, Id) for read pointer
        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(s => new { s.ConversationId, s.LastReadMessageId })
            .HasPrincipalKey(m => new { m.ConversationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
