using AOS.Messenger.Domain.ValueObjects;
using Microsoft.AspNetCore.Identity;

namespace AOS.Messenger.Infrastructure.Identity;

/// <summary>
/// ASP.NET Core Identity user entity. Extends IdentityUser with a Guid key.
/// Exists exclusively within the Infrastructure layer to isolate Identity concerns from the Domain model.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = string.Empty;

    public string? Bio { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public bool IsDeleted { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public DateTimeOffset? ScheduledPermanentDeletionAtUtc { get; set; }

    public DateTimeOffset? LastSeenAtUtc { get; set; }

    public string? AvatarObjectKey { get; set; }

    public string? AvatarThumbnailObjectKey { get; set; }

    public string? AvatarContentType { get; set; }

    public long? AvatarSizeBytes { get; set; }

    public long? AvatarThumbnailSizeBytes { get; set; }

    public string? AvatarVersion { get; set; }

    public DateTimeOffset? AvatarUpdatedAtUtc { get; set; }

    public UserId ToDomainUserId() => new(Id);
}
