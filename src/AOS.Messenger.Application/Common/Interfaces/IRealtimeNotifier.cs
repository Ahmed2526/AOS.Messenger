using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Application abstraction for dispatching realtime notifications to connected clients.
/// Decouples Application and Infrastructure from concrete realtime transport technologies (SignalR).
/// </summary>
public interface IRealtimeNotifier
{
    /// <summary>
    /// Dispatches a new message event to all active participants in the conversation.
    /// Broadcast occurs strictly after the message is persisted and committed.
    /// </summary>
    Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default);
    Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default);
    Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default);
    Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default);
    Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default);
    Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default);
    Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default);
    Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default);
    Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default);
}
