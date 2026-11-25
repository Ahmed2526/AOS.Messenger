using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="StickerPack"/>.
/// Enforces NEWSEQUENTIALID() clustered PK, filtered unique slug for built-in packs,
/// Restrict owner FK to prevent cascade cycles, and optimistic concurrency.
/// </summary>
public sealed class StickerPackConfiguration : IEntityTypeConfiguration<StickerPack>
{
    public void Configure(EntityTypeBuilder<StickerPack> builder)
    {
        builder.ToTable("StickerPacks", t =>
        {
            t.HasCheckConstraint(
                "CK_StickerPacks_Ownership",
                "([IsSystemPack] = 1 AND [OwnerUserId] IS NULL AND [Slug] IS NOT NULL) OR ([IsSystemPack] = 0 AND [OwnerUserId] IS NOT NULL AND [Slug] IS NULL)");
        });

        builder.HasKey(p => p.Id).IsClustered();

        builder.Property(p => p.Id)
            .HasConversion<StickerPackIdConverter>()
            .HasDefaultValueSql("NEWSEQUENTIALID()");

        builder.Property(p => p.Slug)
            .HasMaxLength(StickerConstraints.MaxSlugLength);

        builder.Property(p => p.Name)
            .HasMaxLength(StickerConstraints.MaxPackNameLength)
            .IsRequired();

        builder.Property(p => p.AuthorName)
            .HasMaxLength(StickerConstraints.MaxAuthorNameLength)
            .IsRequired();

        builder.Property(p => p.OwnerUserId)
            .HasConversion<NullableUserIdConverter>();

        builder.Property(p => p.IsSystemPack)
            .IsRequired();

        builder.Property(p => p.SortOrder)
            .IsRequired();

        builder.Property(p => p.IsDeleted)
            .HasDefaultValue(false)
            .IsRequired();

        builder.Property(p => p.CreatedAtUtc)
            .IsRequired();

        builder.Property(p => p.UpdatedAtUtc)
            .IsRequired();

        builder.Property(p => p.RowVersion)
            .IsRowVersion();

        builder.HasIndex(p => p.Slug)
            .IsUnique()
            .HasDatabaseName("IX_StickerPacks_Slug")
            .HasFilter("[Slug] IS NOT NULL");

        builder.HasIndex(p => new { p.OwnerUserId, p.IsDeleted });

        builder.HasIndex(p => new { p.IsSystemPack, p.IsDeleted, p.SortOrder });
    }
}
