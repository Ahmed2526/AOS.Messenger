using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Represents a block relationship established by one user against another user.
/// </summary>
public sealed class UserBlock : IEquatable<UserBlock>
{
    public UserId BlockerId { get; private set; } = null!;
    public UserId BlockedUserId { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private UserBlock()
    {
    }

    public static UserBlock Create(UserId blockerId, UserId blockedUserId, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(blockerId);
        ArgumentNullException.ThrowIfNull(blockedUserId);

        if (blockerId == blockedUserId)
        {
            throw new DomainException("A user cannot block themselves.");
        }

        return new UserBlock
        {
            BlockerId = blockerId,
            BlockedUserId = blockedUserId,
            CreatedAtUtc = nowUtc
        };
    }

    public bool Equals(UserBlock? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return BlockerId.Equals(other.BlockerId) && BlockedUserId.Equals(other.BlockedUserId);
    }

    public override bool Equals(object? obj) =>
        obj is UserBlock other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(BlockerId, BlockedUserId);

    public static bool operator ==(UserBlock? left, UserBlock? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(UserBlock? left, UserBlock? right) =>
        !(left == right);
}
