using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Represents the per-user state for a conversation (clear boundary, soft deletion, pin, archive, read marker).
/// Ensures user-specific mutations do not affect the shared Conversation entity or other participants.
/// </summary>
public sealed class UserConversationState : IEquatable<UserConversationState>
{
    public ConversationId ConversationId { get; private set; } = null!;
    public UserId UserId { get; private set; } = null!;
    public DateTimeOffset? ClearedAtUtc { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public DateTimeOffset? MutedAtUtc { get; private set; }
    public bool IsPinned { get; private set; }
    public DateTimeOffset? PinnedAtUtc { get; private set; }
    public bool IsArchived { get; private set; }
    public DateTimeOffset? ArchivedAtUtc { get; private set; }
    public MessageId? LastDeliveredMessageId { get; private set; }
    public DateTimeOffset? LastDeliveredAtUtc { get; private set; }
    public MessageId? LastReadMessageId { get; private set; }
    public DateTimeOffset? LastReadAtUtc { get; private set; }

    private UserConversationState()
    {
    }

    public static UserConversationState Create(ConversationId conversationId, UserId userId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(userId);

        return new UserConversationState
        {
            ConversationId = conversationId,
            UserId = userId
        };
    }

    public void ClearChat(DateTimeOffset nowUtc)
    {
        ClearedAtUtc = nowUtc;
    }

    public void DeleteConversation(DateTimeOffset nowUtc)
    {
        DeletedAtUtc = nowUtc;
    }

    public void Pin(DateTimeOffset nowUtc)
    {
        IsPinned = true;
        PinnedAtUtc = nowUtc;
    }

    public void Unpin()
    {
        IsPinned = false;
        PinnedAtUtc = null;
    }

    public void Archive(DateTimeOffset nowUtc)
    {
        IsArchived = true;
        ArchivedAtUtc = nowUtc;
    }

    public void Unarchive()
    {
        IsArchived = false;
        ArchivedAtUtc = null;
    }

    public bool AdvanceDelivered(MessageId messageId, DateTimeOffset messageCreatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        if (LastDeliveredMessageId is null || messageId.Value > LastDeliveredMessageId.Value)
        {
            LastDeliveredMessageId = messageId;
            LastDeliveredAtUtc = messageCreatedAtUtc;
            return true;
        }
        return false;
    }

    public bool AdvanceRead(MessageId messageId, DateTimeOffset messageCreatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        var advanced = false;
        if (LastReadMessageId is null || messageId.Value > LastReadMessageId.Value)
        {
            LastReadMessageId = messageId;
            LastReadAtUtc = messageCreatedAtUtc;
            advanced = true;
        }
        if (LastDeliveredMessageId is null || messageId.Value > LastDeliveredMessageId.Value)
        {
            LastDeliveredMessageId = messageId;
            LastDeliveredAtUtc = messageCreatedAtUtc;
        }
        return advanced;
    }

    public void MarkRead(MessageId messageId, DateTimeOffset nowUtc)
    {
        AdvanceRead(messageId, nowUtc);
    }

    public void Mute(DateTimeOffset nowUtc)
    {
        MutedAtUtc = nowUtc;
    }

    public void Unmute()
    {
        MutedAtUtc = null;
    }

    public bool Equals(UserConversationState? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return ConversationId.Equals(other.ConversationId) && UserId.Equals(other.UserId);
    }

    public override bool Equals(object? obj) =>
        obj is UserConversationState other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(ConversationId, UserId);

    public static bool operator ==(UserConversationState? left, UserConversationState? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(UserConversationState? left, UserConversationState? right) =>
        !(left == right);
}
