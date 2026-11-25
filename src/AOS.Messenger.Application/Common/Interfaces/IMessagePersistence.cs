using AOS.Messenger.Application.Common;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Persistence contract for messages and deterministic keyset pagination.
/// Implemented by Infrastructure using EF Core.
/// </summary>
public interface IMessagePersistence
{
    Task<Message?> GetByIdAsync(ConversationId conversationId, MessageId id, CancellationToken ct = default);
    Task<IReadOnlyDictionary<MessageId, Message>> GetByIdsAsync(ConversationId conversationId, IEnumerable<MessageId> ids, CancellationToken ct = default);
    Task<Message?> GetByIdempotencyKeyAsync(UserId senderId, string idempotencyKey, CancellationToken ct = default);
    Task<PersistMessageResult> AddAsync(Message message, CancellationToken ct = default);
    Task<PersistMessageResult> AddWithMediaAsync(Message message, IReadOnlyList<MediaId> mediaIds, DateTimeOffset attachedAtUtc, CancellationToken ct = default);
    Task UpdateAsync(Message message, CancellationToken ct = default);
    Task<IReadOnlyList<Message>> GetKeysetMessagesAsync(ConversationId conversationId, DateTimeOffset? cursorCreatedAtUtc, MessageId? cursorId, DateTimeOffset? clearedAtUtc, int pageSize = PaginationDefaults.DefaultMessagePageSize, CancellationToken ct = default);
    Task<IReadOnlyList<Message>> GetKeysetMessagesAsync(ConversationId conversationId, UserId callerId, DateTimeOffset? cursorCreatedAtUtc, MessageId? cursorId, DateTimeOffset? clearedAtUtc, int pageSize = PaginationDefaults.DefaultMessagePageSize, CancellationToken ct = default);
    Task DeleteForMeAsync(ConversationId conversationId, MessageId messageId, UserId callerId, DateTimeOffset deletedAtUtc, CancellationToken ct = default);
    Task<bool> IsMessageDeletedForUserAsync(ConversationId conversationId, MessageId messageId, UserId callerId, CancellationToken ct = default);
    Task<HashSet<MessageId>> GetDeletedMessageIdsForUserAsync(ConversationId conversationId, UserId callerId, IEnumerable<MessageId> messageIds, CancellationToken ct = default);
    Task DeleteForEveryoneTransactionalAsync(ConversationId conversationId, MessageId messageId, DateTimeOffset deletedAtUtc, CancellationToken ct = default);
}
