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
    }
}
