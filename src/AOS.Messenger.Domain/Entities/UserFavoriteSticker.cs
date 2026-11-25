using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Per-user starred/favorite sticker association.
/// </summary>
public sealed class UserFavoriteSticker
{
    public UserId UserId { get; private set; } = null!;
    public StickerId StickerId { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private UserFavoriteSticker()
    {
    }

    public static UserFavoriteSticker Create(UserId userId, StickerId stickerId, DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(stickerId);

        return new UserFavoriteSticker
        {
            UserId = userId,
            StickerId = stickerId,
            CreatedAtUtc = createdAtUtc
        };
    }
}
