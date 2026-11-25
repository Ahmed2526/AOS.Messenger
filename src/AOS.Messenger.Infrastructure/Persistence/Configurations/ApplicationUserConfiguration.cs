using AOS.Messenger.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AOS.Messenger.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF Core configuration for ApplicationUser.
/// Enforces DisplayName bounds (max 64 characters) and sequential GUID default.
/// </summary>
public sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.Id)
            .HasDefaultValueSql("NEWSEQUENTIALID()");

        builder.Property(u => u.DisplayName)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(u => u.LastSeenAtUtc);

        builder.Property(u => u.AvatarObjectKey)
            .HasMaxLength(256);

        builder.Property(u => u.AvatarThumbnailObjectKey)
            .HasMaxLength(256);

        builder.Property(u => u.AvatarContentType)
            .HasMaxLength(64);

        builder.Property(u => u.AvatarVersion)
            .HasMaxLength(32);

        builder.HasIndex(u => u.AvatarObjectKey)
            .IsUnique()
            .HasFilter("[AvatarObjectKey] IS NOT NULL");

        builder.HasIndex(u => u.AvatarThumbnailObjectKey)
            .IsUnique()
            .HasFilter("[AvatarThumbnailObjectKey] IS NOT NULL");

        builder.HasIndex(u => new { u.EmailConfirmed, u.CreatedAtUtc });

        builder.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_AspNetUsers_AvatarKeys",
                "(([AvatarObjectKey] IS NULL AND [AvatarThumbnailObjectKey] IS NULL) OR ([AvatarObjectKey] LIKE N'avatars/%' AND [AvatarThumbnailObjectKey] LIKE N'avatars/%' AND [AvatarObjectKey] <> [AvatarThumbnailObjectKey]))");
        });
    }
}
