using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="UserFavoriteSticker"/>.
/// Composite PK (UserId, StickerId), Restrict FKs, and reverse StickerId index for cleanup checks.
/// </summary>
public sealed class UserFavoriteStickerConfiguration : IEntityTypeConfiguration<UserFavoriteSticker>
{
    public void Configure(EntityTypeBuilder<UserFavoriteSticker> builder)
    {
        builder.ToTable("UserFavoriteStickers");

        builder.HasKey(f => new { f.UserId, f.StickerId });

        builder.Property(f => f.UserId)
            .HasConversion<UserIdConverter>()
            .IsRequired();

        builder.Property(f => f.StickerId)
            .HasConversion<StickerIdConverter>()
            .IsRequired();

        builder.Property(f => f.CreatedAtUtc)
            .IsRequired();

        builder.HasIndex(f => new { f.UserId, f.CreatedAtUtc })
            .IsDescending(false, true);

        builder.HasIndex(f => f.StickerId)
            .HasDatabaseName("IX_UserFavoriteStickers_StickerId");

        builder.HasOne<Sticker>()
            .WithMany()
            .HasForeignKey(f => f.StickerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
