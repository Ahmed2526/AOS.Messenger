using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Represents a per-user soft deletion of a message ("Delete for me").
/// Composite primary key: (UserId, MessageId).
/// Composite foreign key: (ConversationId, MessageId) -> Messages(ConversationId, Id).
/// </summary>
public sealed class UserMessageDeletion : IEquatable<UserMessageDeletion>
{
    public ConversationId ConversationId { get; private set; } = null!;
    public MessageId MessageId { get; private set; } = null!;
    public UserId UserId { get; private set; } = null!;
    public DateTimeOffset DeletedAtUtc { get; private set; }

    private UserMessageDeletion()
    {
    }

    public static UserMessageDeletion Create(
        ConversationId conversationId,
        MessageId messageId,
        UserId userId,
        DateTimeOffset deletedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(userId);

        return new UserMessageDeletion
        {
            ConversationId = conversationId,
            MessageId = messageId,
            UserId = userId,
            DeletedAtUtc = deletedAtUtc
        };
    }

    public bool Equals(UserMessageDeletion? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return UserId.Equals(other.UserId) && MessageId.Equals(other.MessageId);
    }

    public override bool Equals(object? obj) =>
        obj is UserMessageDeletion other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(UserId, MessageId);

    public static bool operator ==(UserMessageDeletion? left, UserMessageDeletion? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(UserMessageDeletion? left, UserMessageDeletion? right) =>
        !(left == right);
}
