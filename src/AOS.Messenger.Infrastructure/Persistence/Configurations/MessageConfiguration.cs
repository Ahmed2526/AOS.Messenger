using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for Message entity.
/// Enforces clustered bigint IDENTITY(1,1) primary key, idempotency key uniqueness per sender,
/// keyset pagination ordering index, conversation cascade deletion, composite unique index (ConversationId, Id),
/// composite reply self-referencing relationship, and optimistic concurrency token.
/// </summary>
public sealed class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages", t => t.HasCheckConstraint(
            "CK_Messages_Content_Tombstone",
            "([IsDeletedForEveryone] = 1 AND [Content] IS NULL) OR ([IsDeletedForEveryone] = 0 AND [Content] IS NOT NULL)"));

        builder.HasKey(m => m.Id).IsClustered();

        builder.Property(m => m.Id)
            .HasConversion<MessageIdConverter>()
            .UseIdentityColumn()
            .HasSentinel(new MessageId(0));

        builder.Property(m => m.ConversationId)
            .HasConversion<ConversationIdConverter>()
            .IsRequired();

        builder.Property(m => m.SenderId)
            .HasConversion<UserIdConverter>()
            .IsRequired();

        builder.Property(m => m.ParentMessageId)
            .HasConversion<NullableMessageIdConverter>();

        builder.Property(m => m.IdempotencyKey)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(m => m.Content)
            .HasMaxLength(4000);

        builder.Property(m => m.CreatedAtUtc)
            .IsRequired();

        builder.Property(m => m.EditedAtUtc);

        builder.Property(m => m.IsDeletedForEveryone)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(m => m.DeletedForEveryoneAtUtc);

        builder.Property(m => m.RowVersion)
            .IsRowVersion();

        // Unique index for idempotency: (SenderId, IdempotencyKey)
        builder.HasIndex(m => new { m.SenderId, m.IdempotencyKey })
            .IsUnique();

        // Keyset pagination index: (ConversationId, CreatedAtUtc DESC, Id DESC)
        builder.HasIndex(m => new { m.ConversationId, m.CreatedAtUtc, m.Id })
            .IsDescending(false, true, true);

        // Unique index on (ConversationId, Id) for composite foreign key references
        builder.HasIndex(m => new { m.ConversationId, m.Id })
            .IsUnique();

        // Foreign key to Conversations (cascade delete)
        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Composite self-referencing foreign key for reply hierarchy (restrict delete)
        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(m => new { m.ConversationId, m.ParentMessageId })
            .HasPrincipalKey(m => new { m.ConversationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
