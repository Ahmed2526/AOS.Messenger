using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for <see cref="PendingAvatarUpload"/>.
/// Enforces NEWSEQUENTIALID() clustered PK, staging prefix, unique storage keys, and user-scoped quota indexes.
/// </summary>
public sealed class PendingAvatarUploadConfiguration : IEntityTypeConfiguration<PendingAvatarUpload>
{
    public void Configure(EntityTypeBuilder<PendingAvatarUpload> builder)
    {
        builder.ToTable("PendingAvatarUploads", t =>
        {
            t.HasCheckConstraint(
                "CK_PendingAvatarUploads_ObjectKey_Prefix",
                "[ObjectKey] LIKE N'pending/%'");
            t.HasCheckConstraint(
                "CK_PendingAvatarUploads_Sizes_Positive",
                "[DeclaredSizeBytes] > 0 AND ([VerifiedSizeBytes] IS NULL OR [VerifiedSizeBytes] > 0)");
        });

        builder.HasKey(p => p.Id).IsClustered();

        builder.Property(p => p.Id)
            .HasConversion<AvatarUploadIdConverter>()
            .HasDefaultValueSql("NEWSEQUENTIALID()");

        builder.Property(p => p.UserId)
            .HasConversion<UserIdConverter>()
            .IsRequired();

        builder.Property(p => p.StorageObjectId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(p => p.ObjectKey)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(p => p.State)
            .IsRequired();

        builder.Property(p => p.DeclaredContentType)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(p => p.DeclaredSizeBytes)
            .IsRequired();

        builder.Property(p => p.VerifiedSizeBytes);

        builder.Property(p => p.FailureReason)
            .HasMaxLength(256);

        builder.Property(p => p.CreatedAtUtc)
            .IsRequired();

        builder.Property(p => p.CompletedAtUtc);

        builder.Property(p => p.PurgedAtUtc);

        builder.Property(p => p.RowVersion)
            .IsRowVersion();

        builder.HasIndex(p => p.ObjectKey)
            .IsUnique();

        builder.HasIndex(p => p.StorageObjectId)
            .IsUnique();

        builder.HasIndex(p => new { p.UserId, p.State, p.CreatedAtUtc });

        builder.HasIndex(p => new { p.UserId, p.CreatedAtUtc })
            .IncludeProperties(p => new { p.State, p.DeclaredSizeBytes, p.VerifiedSizeBytes });

        builder.HasIndex(p => new { p.State, p.PurgedAtUtc, p.CreatedAtUtc });
    }
}
