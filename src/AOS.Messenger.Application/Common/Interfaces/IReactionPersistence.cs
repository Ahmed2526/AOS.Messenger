using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Persistence contract for message emoji reactions.
/// Implemented by Infrastructure using EF Core.
/// </summary>
public interface IReactionPersistence
{
    Task<MessageReaction?> GetUserReactionAsync(ConversationId conversationId, MessageId messageId, UserId userId, CancellationToken ct = default);
    Task<string?> ToggleReactionAsync(ConversationId conversationId, MessageId messageId, UserId userId, string emoji, DateTimeOffset nowUtc, CancellationToken ct = default);
    Task<bool> RemoveReactionAsync(ConversationId conversationId, MessageId messageId, UserId userId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<MessageId, IReadOnlyList<ReactionSummaryDto>>> GetReactionsForMessagesAsync(ConversationId conversationId, IEnumerable<MessageId> messageIds, UserId currentUserId, CancellationToken ct = default);
    Task<IReadOnlyList<ReactionGroupDto>> GetReactionGroupsForMessageAsync(ConversationId conversationId, MessageId messageId, CancellationToken ct = default);
    Task DeleteReactionsForMessageAsync(ConversationId conversationId, MessageId messageId, CancellationToken ct = default);
}
