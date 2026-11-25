using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="Sticker"/>.
/// Enforces NEWSEQUENTIALID() clustered PK, Restrict FKs, strict staging-vs-permanent storage constraints,
/// filtered unique indexes for object keys/static paths, and quota lookup indexes.
/// </summary>
public sealed class StickerConfiguration : IEntityTypeConfiguration<Sticker>
{
    public void Configure(EntityTypeBuilder<Sticker> builder)
    {
        builder.ToTable("Stickers", t =>
        {
            t.HasCheckConstraint(
                "CK_Stickers_StorageSource",
                "([UploaderId] IS NULL AND [StaticAssetPath] IS NOT NULL AND [ObjectKey] IS NULL AND [State] = 1) OR ([UploaderId] IS NOT NULL AND [StaticAssetPath] IS NULL AND [ObjectKey] IS NOT NULL AND (([State] IN (0, 2) AND [ObjectKey] LIKE N'pending/%') OR ([State] IN (1, 3) AND [ObjectKey] LIKE N'stickers/%')))");
            t.HasCheckConstraint(
                "CK_Stickers_Dimensions_Positive",
                "[DeclaredSizeBytes] > 0 AND ([VerifiedSizeBytes] IS NULL OR [VerifiedSizeBytes] > 0) AND [Width] > 0 AND [Height] > 0");
        });

        builder.HasKey(s => s.Id).IsClustered();

        builder.Property(s => s.Id)
            .HasConversion<StickerIdConverter>()
            .HasDefaultValueSql("NEWSEQUENTIALID()");

        builder.Property(s => s.PackId)
            .HasConversion<StickerPackIdConverter>()
            .IsRequired();

        builder.Property(s => s.UploaderId)
            .HasConversion<NullableUserIdConverter>();

        builder.Property(s => s.State)
            .IsRequired();

        builder.Property(s => s.StorageObjectId)
            .HasMaxLength(64);

        builder.Property(s => s.ObjectKey)
            .HasMaxLength(256);

        builder.Property(s => s.StaticAssetPath)
            .HasMaxLength(256);

        builder.Property(s => s.ContentType)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(s => s.DeclaredSizeBytes)
            .IsRequired();

        builder.Property(s => s.VerifiedSizeBytes);

        builder.Property(s => s.Width)
            .IsRequired();

        builder.Property(s => s.Height)
            .IsRequired();

        builder.Property(s => s.IsAnimated)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(s => s.EmojiTags)
            .HasMaxLength(StickerConstraints.MaxEmojiTagsLength)
            .IsRequired();

        builder.Property(s => s.DisplayOrder)
            .IsRequired();

        builder.Property(s => s.CreatedAtUtc)
            .IsRequired();

        builder.Property(s => s.UploadedAtUtc);
        builder.Property(s => s.DeletedAtUtc);
        builder.Property(s => s.PurgedAtUtc);

        builder.Property(s => s.FailureReason)
            .HasMaxLength(MediaConstraints.MaxFailureReasonLength);

        builder.Property(s => s.RowVersion)
            .IsRowVersion();

        builder.HasIndex(s => s.StorageObjectId)
            .IsUnique()
            .HasFilter("[StorageObjectId] IS NOT NULL");

        builder.HasIndex(s => s.ObjectKey)
            .IsUnique()
            .HasFilter("[ObjectKey] IS NOT NULL");

        builder.HasIndex(s => s.StaticAssetPath)
            .IsUnique()
            .HasFilter("[StaticAssetPath] IS NOT NULL");

        builder.HasIndex(s => new { s.PackId, s.State, s.DisplayOrder });

        builder.HasIndex(s => new { s.UploaderId, s.State, s.CreatedAtUtc });

        builder.HasIndex(s => new { s.UploaderId, s.CreatedAtUtc })
            .IncludeProperties(s => new { s.State, s.DeclaredSizeBytes, s.VerifiedSizeBytes });

        builder.HasIndex(s => new { s.State, s.PurgedAtUtc, s.CreatedAtUtc });

        builder.HasOne<StickerPack>()
            .WithMany()
            .HasForeignKey(s => s.PackId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
