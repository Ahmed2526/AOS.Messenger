using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Result of persisting a message, indicating whether the message was newly created or resolved via idempotency.
/// </summary>
public sealed record PersistMessageResult(Message Message, bool IsNewlyCreated)
{
    public MessageId Id => Message.Id;
    public ConversationId ConversationId => Message.ConversationId;
    public UserId SenderId => Message.SenderId;
    public string IdempotencyKey => Message.IdempotencyKey;
    public string? Content => Message.Content;
    public DateTimeOffset CreatedAtUtc => Message.CreatedAtUtc;
    public DateTimeOffset? EditedAtUtc => Message.EditedAtUtc;
    public bool IsDeletedForEveryone => Message.IsDeletedForEveryone;
    public MessageId? ParentMessageId => Message.ParentMessageId;

    public static implicit operator Message(PersistMessageResult r) => r.Message;
    public void Deconstruct(out Message message, out bool isNewlyCreated) => (message, isNewlyCreated) = (Message, IsNewlyCreated);
}
