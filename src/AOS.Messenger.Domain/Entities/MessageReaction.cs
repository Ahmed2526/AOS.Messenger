using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Represents an emoji reaction added by a user to a message.
/// Composite primary key: (MessageId, UserId) - at most one reaction per user per message.
/// Composite foreign key: (ConversationId, MessageId) -> Messages(ConversationId, Id).
/// </summary>
public sealed class MessageReaction : IEquatable<MessageReaction>
{
    public ConversationId ConversationId { get; private set; } = null!;
    public MessageId MessageId { get; private set; } = null!;
    public UserId UserId { get; private set; } = null!;
    public string Emoji { get; private set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; private set; }

    private MessageReaction()
    {
    }

    public static MessageReaction Create(
        ConversationId conversationId,
        MessageId messageId,
        UserId userId,
        string emoji,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(userId);

        if (!ReactionAllowlist.IsAllowed(emoji))
        {
            throw new DomainException($"Emoji '{emoji}' is not in the allowed reaction set.");
        }

        return new MessageReaction
        {
            ConversationId = conversationId,
            MessageId = messageId,
            UserId = userId,
            Emoji = ReactionAllowlist.Normalize(emoji),
            CreatedAtUtc = createdAtUtc
        };
    }

    public void UpdateEmoji(string newEmoji, DateTimeOffset nowUtc)
    {
        if (!ReactionAllowlist.IsAllowed(newEmoji))
        {
            throw new DomainException($"Emoji '{newEmoji}' is not in the allowed reaction set.");
        }

        Emoji = ReactionAllowlist.Normalize(newEmoji);
        CreatedAtUtc = nowUtc;
    }

    public bool Equals(MessageReaction? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return MessageId.Equals(other.MessageId) && UserId.Equals(other.UserId);
    }

    public override bool Equals(object? obj) =>
        obj is MessageReaction other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(MessageId, UserId);

    public static bool operator ==(MessageReaction? left, MessageReaction? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(MessageReaction? left, MessageReaction? right) =>
        !(left == right);
}
