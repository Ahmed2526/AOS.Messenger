using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Persistence contract for direct conversations and per-user conversation state.
/// Implemented by Infrastructure using EF Core.
/// </summary>
public interface IConversationPersistence
{
    Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken ct = default);
    Task<Conversation?> GetByParticipantsAsync(UserId userA, UserId userB, CancellationToken ct = default);
    Task<Conversation> AddAsync(Conversation conversation, CancellationToken ct = default);
    Task<UserConversationState?> GetUserStateAsync(ConversationId conversationId, UserId userId, CancellationToken ct = default);
    Task UpsertUserStateAsync(UserConversationState state, CancellationToken ct = default);
    Task<IReadOnlyList<Conversation>> GetConversationsForUserAsync(UserId userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserConversationState>> GetUserStatesForUserAsync(UserId userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserConversationState>> GetUserStatesForConversationsAsync(IReadOnlyList<ConversationId> conversationIds, CancellationToken ct = default);
    Task<IReadOnlyList<UserConversationState>> GetPeerStatesForConversationsAsync(IReadOnlyList<ConversationId> conversationIds, UserId callerId, CancellationToken ct = default);
    Task<Message?> GetLastMessageAsync(ConversationId conversationId, DateTimeOffset? minCreatedAtUtc = null, CancellationToken ct = default);
    Task<int> GetUnreadCountAsync(ConversationId conversationId, UserId userId, MessageId? lastReadMessageId = null, DateTimeOffset? minCreatedAtUtc = null, CancellationToken ct = default);
    Task<bool> HasMessagesAfterAsync(ConversationId conversationId, DateTimeOffset afterUtc, CancellationToken ct = default);
    Task<Dictionary<ConversationId, Message>> GetLastMessagesForConversationsAsync(IReadOnlyList<ConversationId> conversationIds, CancellationToken ct = default);
    Task<Dictionary<ConversationId, Message>> GetLastMessagesForConversationsAsync(IReadOnlyList<ConversationId> conversationIds, UserId callerId, CancellationToken ct = default);
    Task<Dictionary<ConversationId, int>> GetUnreadCountsForConversationsAsync(UserId userId, IReadOnlyList<(ConversationId ConversationId, MessageId? LastReadMessageId, DateTimeOffset? ClearedAtUtc)> requests, CancellationToken ct = default);
    Task<HashSet<ConversationId>> GetConversationsWithMessagesAfterAsync(IReadOnlyList<(ConversationId ConversationId, DateTimeOffset ThresholdUtc)> requests, CancellationToken ct = default);
    Task<HashSet<ConversationId>> GetConversationsWithMessagesAfterAsync(IReadOnlyList<(ConversationId ConversationId, DateTimeOffset ThresholdUtc)> requests, UserId callerId, CancellationToken ct = default);
    Task<bool> AdvanceDeliveredWatermarkAsync(ConversationId conversationId, UserId userId, MessageId messageId, DateTimeOffset msgCreatedAtUtc, CancellationToken ct = default);
    Task<bool> AdvanceReadWatermarkAsync(ConversationId conversationId, UserId userId, MessageId messageId, DateTimeOffset msgCreatedAtUtc, CancellationToken ct = default);
    Task<IReadOnlyList<UserId>> GetPeerUserIdsWithExistingConversationAsync(UserId userId, CancellationToken ct = default);
    Task<UserId?> GetVerifiedTypingRecipientAsync(UserId senderId, ConversationId conversationId, CancellationToken ct = default);
    Task<bool> AreAuthorizedDirectPeersAsync(ConversationId conversationId, UserId userA, UserId userB, CancellationToken ct = default);
}
