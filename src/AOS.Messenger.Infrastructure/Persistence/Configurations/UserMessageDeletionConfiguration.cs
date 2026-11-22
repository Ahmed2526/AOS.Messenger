using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for UserMessageDeletion entity.
/// Composite primary key: (UserId, MessageId).
/// Composite foreign key: (ConversationId, MessageId) -> Messages(ConversationId, Id).
/// </summary>
public sealed class UserMessageDeletionConfiguration : IEntityTypeConfiguration<UserMessageDeletion>
{
    public void Configure(EntityTypeBuilder<UserMessageDeletion> builder)
    {
        builder.ToTable("UserMessageDeletions");

        builder.HasKey(d => new { d.UserId, d.MessageId });

        builder.Property(d => d.UserId)
            .HasConversion<UserIdConverter>()
            .IsRequired();

        builder.Property(d => d.MessageId)
            .HasConversion<MessageIdConverter>()
            .IsRequired();

        builder.Property(d => d.ConversationId)
            .HasConversion<ConversationIdConverter>()
            .IsRequired();

        builder.Property(d => d.DeletedAtUtc)
            .IsRequired();

        // Index on (ConversationId, UserId, MessageId) for fast scoping queries
        builder.HasIndex(d => new { d.ConversationId, d.UserId, d.MessageId });

        // Composite foreign key to Messages(ConversationId, Id)
        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(d => new { d.ConversationId, d.MessageId })
            .HasPrincipalKey(m => new { m.ConversationId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
