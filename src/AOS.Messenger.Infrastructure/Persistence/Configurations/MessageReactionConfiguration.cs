using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for MessageReaction entity.
/// Composite primary key: (MessageId, UserId) - at most one reaction per user per message.
/// Composite foreign key: (ConversationId, MessageId) -> Messages(ConversationId, Id).
/// </summary>
public sealed class MessageReactionConfiguration : IEntityTypeConfiguration<MessageReaction>
{
    public void Configure(EntityTypeBuilder<MessageReaction> builder)
    {
        builder.ToTable("MessageReactions");

        builder.HasKey(r => new { r.MessageId, r.UserId });

        builder.Property(r => r.MessageId)
            .HasConversion<MessageIdConverter>()
            .IsRequired();

        builder.Property(r => r.UserId)
            .HasConversion<UserIdConverter>()
            .IsRequired();

        builder.Property(r => r.ConversationId)
            .HasConversion<ConversationIdConverter>()
            .IsRequired();

        builder.Property(r => r.Emoji)
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(r => r.CreatedAtUtc)
            .IsRequired();

        // Index on (ConversationId, MessageId) for fast conversation batch loading
        builder.HasIndex(r => new { r.ConversationId, r.MessageId });

        // Composite foreign key to Messages(ConversationId, Id)
        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(r => new { r.ConversationId, r.MessageId })
            .HasPrincipalKey(m => new { m.ConversationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
