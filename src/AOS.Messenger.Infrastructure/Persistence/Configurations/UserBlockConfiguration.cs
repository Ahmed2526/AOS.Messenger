using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for UserBlock entity.
/// Enforces composite primary key on (BlockerId, BlockedUserId), self-block check constraint,
/// and non-clustered index on BlockedUserId for bidirectional block lookups.
/// </summary>
public sealed class UserBlockConfiguration : IEntityTypeConfiguration<UserBlock>
{
    public void Configure(EntityTypeBuilder<UserBlock> builder)
    {
        builder.ToTable("UserBlocks", t =>
        {
            t.HasCheckConstraint("CK_UserBlocks_CannotBlockSelf", "[BlockerId] <> [BlockedUserId]");
        });

        builder.HasKey(b => new { b.BlockerId, b.BlockedUserId }).IsClustered();

        builder.Property(b => b.BlockerId)
            .HasConversion<UserIdConverter>();

        builder.Property(b => b.BlockedUserId)
            .HasConversion<UserIdConverter>();

        builder.Property(b => b.CreatedAtUtc)
            .IsRequired();

        // Non-clustered index on BlockedUserId for bidirectional block scans
        builder.HasIndex(b => b.BlockedUserId);
    }
}
