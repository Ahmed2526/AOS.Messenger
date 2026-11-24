using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for Media entity.
/// Enforces NEWSEQUENTIALID() clustered PK, storage object uniqueness, state/prefix/size check constraints,
/// composite message FK (ConversationId, MessageId) -> Messages(ConversationId, Id), and concurrency RowVersion.
/// </summary>
public sealed class MediaConfiguration : IEntityTypeConfiguration<Media>
{
    public void Configure(EntityTypeBuilder<Media> builder)
    {
        builder.ToTable("Media", t =>
        {
            t.HasCheckConstraint(
                "CK_Media_State_MessageId",
                "([State] = 2 AND [MessageId] IS NOT NULL) OR ([State] IN (0, 1, 3) AND [MessageId] IS NULL) OR ([State] = 4)");
            t.HasCheckConstraint(
                "CK_Media_ObjectKey_Prefix",
                "([State] = 0 AND [ObjectKey] LIKE N'pending/%') OR ([State] IN (1, 2) AND [ObjectKey] LIKE N'media/%') OR ([State] IN (3, 4))");
            t.HasCheckConstraint(
                "CK_Media_Sizes_Positive",
                "[DeclaredSizeBytes] > 0 AND ([VerifiedSizeBytes] IS NULL OR [VerifiedSizeBytes] > 0)");
        });

        builder.HasKey(m => m.Id).IsClustered();

        builder.Property(m => m.Id)
            .HasConversion<MediaIdConverter>()
            .HasDefaultValueSql("NEWSEQUENTIALID()");

        builder.Property(m => m.StorageObjectId)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(m => m.UploaderId)
            .HasConversion<UserIdConverter>()
            .IsRequired();

        builder.Property(m => m.ConversationId)
            .HasConversion<ConversationIdConverter>()
            .IsRequired();

        builder.Property(m => m.MessageId)
            .HasConversion<NullableMessageIdConverter>();

        builder.Property(m => m.DisplayOrder)
            .HasDefaultValue(0)
            .IsRequired();

        builder.Property(m => m.Kind)
            .IsRequired();

        builder.Property(m => m.State)
            .IsRequired();

        builder.Property(m => m.ObjectKey)
            .HasMaxLength(256)
            .IsRequired();

        builder.Property(m => m.SanitizedFileName)
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(m => m.DeclaredContentType)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(m => m.VerifiedContentType)
            .HasMaxLength(128);

        builder.Property(m => m.DeclaredSizeBytes)
            .IsRequired();

        builder.Property(m => m.VerifiedSizeBytes);

        builder.Property(m => m.Width);

        builder.Property(m => m.Height);

        builder.Property(m => m.DurationMs);

        builder.Property(m => m.CreatedAtUtc)
            .IsRequired();

        builder.Property(m => m.UploadedAtUtc);

        builder.Property(m => m.AttachedAtUtc);

        builder.Property(m => m.DeletedAtUtc);

        builder.Property(m => m.PurgedAtUtc);

        builder.Property(m => m.FailureReason)
            .HasMaxLength(256);

        builder.Property(m => m.RowVersion)
            .IsRowVersion();

        // Unique object key and storage object identifier indexes
        builder.HasIndex(m => m.ObjectKey)
            .IsUnique();

        builder.HasIndex(m => m.StorageObjectId)
            .IsUnique();

        // Filtered index for fast batched message attachment lookups
        builder.HasIndex(m => new { m.ConversationId, m.MessageId, m.DisplayOrder })
            .HasFilter("[MessageId] IS NOT NULL");

        // Index for per-user concurrent pending cap
        builder.HasIndex(m => new { m.UploaderId, m.State, m.CreatedAtUtc });

        // Covering index for 24-hour per-user daily upload quota summation under sp_getapplock
        builder.HasIndex(m => new { m.UploaderId, m.CreatedAtUtc })
            .IncludeProperties(m => new { m.State, m.DeclaredSizeBytes, m.VerifiedSizeBytes });

        // Index for background cleanup sweeps
        builder.HasIndex(m => new { m.State, m.PurgedAtUtc, m.CreatedAtUtc });

        // Foreign keys (Restrict to prevent SQL Server multiple cascade paths)
        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Message>()
            .WithMany()
            .HasForeignKey(m => new { m.ConversationId, m.MessageId })
            .HasPrincipalKey(msg => new { msg.ConversationId, msg.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
