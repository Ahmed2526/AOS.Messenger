using System.Globalization;
using System.Text;
using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Represents a message sent within a conversation.
/// Enforces idempotency tracking, server-side UTC creation time, edit rules, and 3-hour delete-for-everyone tombstone semantics.
/// </summary>
public sealed class Message : Entity<MessageId>
{
    public ConversationId ConversationId { get; private set; } = null!;
    public UserId SenderId { get; private set; } = null!;
    public string IdempotencyKey { get; private set; } = null!;
    public string? Content { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? EditedAtUtc { get; private set; }
    public bool IsDeletedForEveryone { get; private set; }
    public DateTimeOffset? DeletedForEveryoneAtUtc { get; private set; }
    public MessageId? ParentMessageId { get; private set; }
    public bool HasAttachments { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private Message()
    {
    }

    public static Message Create(
        ConversationId conversationId,
        UserId senderId,
        string idempotencyKey,
        string? content,
        DateTimeOffset nowUtc,
        MessageId? parentMessageId = null,
        MessageId? id = null,
        bool hasAttachments = false)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(senderId);

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new DomainException("IdempotencyKey cannot be empty.");
        }

        var trimmedKey = idempotencyKey.Trim();
        if (trimmedKey.Length > 128)
        {
            throw new DomainException("IdempotencyKey cannot exceed 128 characters.");
        }

        string? normalizedContent;
        if (!hasAttachments)
        {
            if (!HasVisibleContent(content))
            {
                throw new DomainException("Message content cannot be empty.");
            }

            var trimmed = content!.Trim();
            if (trimmed.Length > 4000)
            {
                throw new DomainException("Message content cannot exceed 4000 characters.");
            }

            normalizedContent = trimmed;
        }
        else if (HasVisibleContent(content))
        {
            var trimmed = content!.Trim();
            if (trimmed.Length > 4000)
            {
                throw new DomainException("Message content cannot exceed 4000 characters.");
            }

            normalizedContent = trimmed;
        }
        else
        {
            normalizedContent = null;
        }

        return new Message
        {
            Id = id ?? new MessageId(0),
            ConversationId = conversationId,
            SenderId = senderId,
            IdempotencyKey = trimmedKey,
            Content = normalizedContent,
            CreatedAtUtc = nowUtc,
            ParentMessageId = parentMessageId,
            HasAttachments = hasAttachments,
            IsDeletedForEveryone = false,
            RowVersion = []
        };
    }

    public static bool HasVisibleContent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is not (UnicodeCategory.SpaceSeparator
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Control
                or UnicodeCategory.Format))
            {
                return true;
            }
        }

        return false;
    }

    public void Edit(string? newContent, DateTimeOffset nowUtc)
    {
        if (IsDeletedForEveryone)
        {
            throw new DomainException("Cannot edit a message that has been deleted for everyone.");
        }

        if (!HasAttachments)
        {
            if (!HasVisibleContent(newContent))
            {
                throw new DomainException("Message content cannot be empty.");
            }

            var trimmed = newContent!.Trim();
            if (trimmed.Length > 4000)
            {
                throw new DomainException("Message content cannot exceed 4000 characters.");
            }

            Content = trimmed;
        }
        else if (HasVisibleContent(newContent))
        {
            var trimmed = newContent!.Trim();
            if (trimmed.Length > 4000)
            {
                throw new DomainException("Message content cannot exceed 4000 characters.");
            }

            Content = trimmed;
        }
        else
        {
            Content = null;
        }

        EditedAtUtc = nowUtc;
    }

    public void DeleteForEveryone(DateTimeOffset deletedAtUtc)
    {
        if (IsDeletedForEveryone)
        {
            return;
        }

        IsDeletedForEveryone = true;
        DeletedForEveryoneAtUtc = deletedAtUtc;
        Content = null;
        EditedAtUtc = null;
    }

    public void DeleteForEveryone(UserId requestingUser, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(requestingUser);

        if (SenderId != requestingUser)
        {
            throw new DomainException("Only the sender can delete a message for everyone.");
        }

        if (nowUtc - CreatedAtUtc > TimeSpan.FromHours(3))
        {
            throw new DomainException("A message can only be deleted for everyone within 3 hours of creation.");
        }

        DeleteForEveryone(nowUtc);
    }
}
