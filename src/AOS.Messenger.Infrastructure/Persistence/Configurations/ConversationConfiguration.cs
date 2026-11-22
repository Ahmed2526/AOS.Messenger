using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for Conversation entity.
/// Enforces check constraint ensuring Participant1Id < Participant2Id canonical ordering and participant pair uniqueness.
/// Configures clustered primary key with SQL Server NEWSEQUENTIALID().
/// </summary>
public sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations", t =>
        {
            t.HasCheckConstraint("CK_Conversations_ParticipantOrdering", "[Participant1Id] < [Participant2Id]");
        });

        builder.HasKey(c => c.Id).IsClustered();

        builder.Property(c => c.Id)
            .HasConversion<ConversationIdConverter>()
            .HasDefaultValueSql("NEWSEQUENTIALID()");

        builder.Property(c => c.Participant1Id)
            .HasConversion<UserIdConverter>()
            .IsRequired();

        builder.Property(c => c.Participant2Id)
            .HasConversion<UserIdConverter>()
            .IsRequired();

        builder.Property(c => c.CreatedAtUtc)
            .IsRequired();

        builder.Property(c => c.RowVersion)
            .IsRowVersion();

        builder.HasIndex(c => new { c.Participant1Id, c.Participant2Id })
            .IsUnique();
    }
}
