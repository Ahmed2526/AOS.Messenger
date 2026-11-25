using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="UserRecentSticker"/>.
/// Composite PK (UserId, StickerId), Restrict FKs, and reverse StickerId index for cleanup checks.
/// </summary>
public sealed class UserRecentStickerConfiguration : IEntityTypeConfiguration<UserRecentSticker>
{
    public void Configure(EntityTypeBuilder<UserRecentSticker> builder)
    {
        builder.ToTable("UserRecentStickers");

        builder.HasKey(r => new { r.UserId, r.StickerId });

        builder.Property(r => r.UserId)
            .HasConversion<UserIdConverter>()
            .IsRequired();

        builder.Property(r => r.StickerId)
            .HasConversion<StickerIdConverter>()
            .IsRequired();

        builder.Property(r => r.LastUsedAtUtc)
            .IsRequired();

        builder.Property(r => r.UseCount)
            .HasDefaultValue(1)
            .IsRequired();

        builder.HasIndex(r => new { r.UserId, r.LastUsedAtUtc })
            .IsDescending(false, true);

        builder.HasIndex(r => r.StickerId)
            .HasDatabaseName("IX_UserRecentStickers_StickerId");

        builder.HasOne<Sticker>()
            .WithMany()
            .HasForeignKey(r => r.StickerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
