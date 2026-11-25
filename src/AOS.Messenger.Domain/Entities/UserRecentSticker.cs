using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Per-user recently used sticker tracking record.
/// </summary>
public sealed class UserRecentSticker
{
    public UserId UserId { get; private set; } = null!;
    public StickerId StickerId { get; private set; } = null!;
    public DateTimeOffset LastUsedAtUtc { get; private set; }
    public int UseCount { get; private set; }

    private UserRecentSticker()
    {
    }

    public static UserRecentSticker Create(UserId userId, StickerId stickerId, DateTimeOffset usedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(stickerId);

        return new UserRecentSticker
        {
            UserId = userId,
            StickerId = stickerId,
            LastUsedAtUtc = usedAtUtc,
            UseCount = 1
        };
    }

    public void RecordUse(DateTimeOffset usedAtUtc)
    {
        LastUsedAtUtc = usedAtUtc;
        UseCount++;
    }
}
