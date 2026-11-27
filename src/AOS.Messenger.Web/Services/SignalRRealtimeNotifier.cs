using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Web.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AOS.Messenger.Web.Services;

/// <summary>
/// SignalR implementation of <see cref="IRealtimeNotifier"/> that broadcasts events
/// directly to authorized conversation participants after verifying live block status,
/// preventing event leakage to newly blocked users without relying on static group subscriptions.
/// </summary>
public sealed class SignalRRealtimeNotifier : IRealtimeNotifier
{
    private readonly IHubContext<ChatHub, IChatClient> _hubContext;
    private readonly IConversationPersistence? _conversationPersistence;
    private readonly IBlockPersistence? _blockPersistence;
    private readonly ILogger<SignalRRealtimeNotifier> _logger;

    public SignalRRealtimeNotifier(
        IHubContext<ChatHub, IChatClient> hubContext,
        IConversationPersistence conversationPersistence,
        IBlockPersistence blockPersistence,
        ILogger<SignalRRealtimeNotifier> logger)
    {
        _hubContext = hubContext;
        _conversationPersistence = conversationPersistence;
        _blockPersistence = blockPersistence;
        _logger = logger;
    }

    public async Task NotifyNewMessageAsync(
        ConversationId conversationId,
        MessageDto message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(message);

        var recipientUserIds = await GetAuthorizedRecipientsAsync(conversationId, ct);
        if (recipientUserIds is null || recipientUserIds.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Broadcasting message {MessageId} to conversation {ConversationId} participants ({Count} recipients)",
            message.Id.Value, conversationId.Value, recipientUserIds.Count);

        await _hubContext.Clients.Users(recipientUserIds).ReceiveMessage(message);
    }

    public async Task NotifyReceiptUpdatedAsync(
        UserId recipientUserId,
        ReceiptUpdateDto receipt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipientUserId);
        ArgumentNullException.ThrowIfNull(receipt);

        if (!await IsAuthorizedDirectPeerAsync(new ConversationId(receipt.ConversationId), new UserId(receipt.UserId), recipientUserId, ct))
        {
            return;
        }

        await _hubContext.Clients.User(recipientUserId.Value.ToString()).ReceiveReceiptUpdated(receipt);
    }

    public async Task NotifyTypingAsync(
        UserId recipientUserId,
        TypingIndicatorDto typing,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipientUserId);
        ArgumentNullException.ThrowIfNull(typing);

        if (!await IsAuthorizedDirectPeerAsync(new ConversationId(typing.ConversationId), new UserId(typing.UserId), recipientUserId, ct))
        {
            return;
        }

        await _hubContext.Clients.User(recipientUserId.Value.ToString()).ReceiveTypingIndicator(typing);
    }

    public async Task NotifyRecordingAsync(
        UserId recipientUserId,
        RecordingIndicatorDto recording,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipientUserId);
        ArgumentNullException.ThrowIfNull(recording);

        if (!await IsAuthorizedDirectPeerAsync(new ConversationId(recording.ConversationId), new UserId(recording.UserId), recipientUserId, ct))
        {
            return;
        }

        await _hubContext.Clients.User(recipientUserId.Value.ToString()).ReceiveRecordingIndicator(recording);
    }

    public async Task NotifyVoicePlayedAsync(
        ConversationId conversationId,
        UserId callerUserId,
        UserId uploaderUserId,
        VoicePlayedDto played,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(callerUserId);
        ArgumentNullException.ThrowIfNull(uploaderUserId);
        ArgumentNullException.ThrowIfNull(played);

        var targetIds = new List<string> { callerUserId.Value.ToString() };
        if (callerUserId != uploaderUserId
            && await IsAuthorizedDirectPeerAsync(conversationId, callerUserId, uploaderUserId, ct))
        {
            targetIds.Add(uploaderUserId.Value.ToString());
        }

        await _hubContext.Clients.Users(targetIds).ReceiveVoicePlayed(played);
    }

    public async Task NotifyPresenceChangedAsync(
        IReadOnlyList<UserId> recipientUserIds,
        UserPresenceDto presence,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(recipientUserIds);
        ArgumentNullException.ThrowIfNull(presence);

        var ids = recipientUserIds.Select(u => u.Value.ToString()).ToList();
        if (ids.Count > 0)
        {
            await _hubContext.Clients.Users(ids).ReceivePresenceChanged(presence);
        }
    }

    public async Task NotifyMessageEditedAsync(
        ConversationId conversationId,
        MessageEditedDto message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(message);

        var recipientUserIds = await GetAuthorizedRecipientsAsync(conversationId, ct);
        if (recipientUserIds is null || recipientUserIds.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Broadcasting message edit for {MessageId} in conversation {ConversationId}",
            message.MessageId, conversationId.Value);

        await _hubContext.Clients.Users(recipientUserIds).ReceiveMessageEdited(message);
    }

    public async Task NotifyMessageDeletedForEveryoneAsync(
        ConversationId conversationId,
        MessageDeletedForEveryoneDto message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(message);

        var recipientUserIds = await GetAuthorizedRecipientsAsync(conversationId, ct);
        if (recipientUserIds is null || recipientUserIds.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Broadcasting delete for everyone for message {MessageId} in conversation {ConversationId}",
            message.MessageId, conversationId.Value);

        await _hubContext.Clients.Users(recipientUserIds).ReceiveMessageDeletedForEveryone(message);
    }

    public async Task NotifyMessageDeletedForMeAsync(
        UserId callerUserId,
        MessageDeletedForMeDto message,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerUserId);
        ArgumentNullException.ThrowIfNull(message);

        await _hubContext.Clients.User(callerUserId.Value.ToString()).ReceiveMessageDeletedForMe(message);
    }

    public async Task NotifyReactionUpdatedAsync(
        ConversationId conversationId,
        ReactionUpdatedDto reaction,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(reaction);

        var recipientUserIds = await GetAuthorizedRecipientsAsync(conversationId, ct);
        if (recipientUserIds is null || recipientUserIds.Count == 0)
        {
            return;
        }

        _logger.LogDebug("Broadcasting reaction update for message {MessageId} in conversation {ConversationId}",
            reaction.MessageId, conversationId.Value);

        await _hubContext.Clients.Users(recipientUserIds).ReceiveReactionUpdated(reaction);
    }

    public async Task NotifyUserProfileUpdatedAsync(
        UserId userId,
        UserProfileUpdatedDto profile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(profile);

        var targetIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            userId.Value.ToString()
        };

        if (_conversationPersistence is not null && _blockPersistence is not null)
        {
            var peerUserIds = await _conversationPersistence.GetPeerUserIdsWithExistingConversationAsync(userId, ct);
            var blockedPeerIds = await _blockPersistence.GetBidirectionalBlockedUserIdsAsync(userId, ct);

            foreach (var peerId in peerUserIds)
            {
                if (!blockedPeerIds.Contains(peerId))
                {
                    targetIds.Add(peerId.Value.ToString());
                }
            }
        }

        _logger.LogDebug(
            "Broadcasting profile update for user {UserId} to {Count} recipient users.",
            userId.Value,
            targetIds.Count);

        await _hubContext.Clients.Users(targetIds.ToList()).ReceiveUserProfileUpdated(profile);
    }

    private async Task<List<string>?> GetAuthorizedRecipientsAsync(ConversationId conversationId, CancellationToken ct)
    {
        if (_conversationPersistence is null || _blockPersistence is null)
        {
            _logger.LogError("Realtime notifier missing persistence dependencies; suppressing broadcast to prevent information disclosure.");
            return null;
        }

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            _logger.LogWarning("Conversation {ConversationId} not found when attempting realtime broadcast", conversationId.Value);
            return null;
        }

        var isBlocked = await _blockPersistence.IsBlockedBidirectionalAsync(
            conversation.Participant1Id,
            conversation.Participant2Id,
            ct);

        if (isBlocked)
        {
            _logger.LogDebug("Conversation {ConversationId} has active block; suppressing realtime broadcast", conversationId.Value);
            return null;
        }

        return [conversation.Participant1Id.Value.ToString(), conversation.Participant2Id.Value.ToString()];
    }

    private async Task<bool> IsAuthorizedDirectPeerAsync(
        ConversationId conversationId,
        UserId senderUserId,
        UserId recipientUserId,
        CancellationToken ct)
    {
        if (_conversationPersistence is null || _blockPersistence is null)
        {
            _logger.LogError("Realtime notifier missing persistence dependencies; suppressing direct broadcast to prevent information disclosure.");
            return false;
        }

        var authorized = await _conversationPersistence.AreAuthorizedDirectPeersAsync(
            conversationId,
            senderUserId,
            recipientUserId,
            ct);

        if (!authorized)
        {
            _logger.LogDebug("Conversation {ConversationId} missing or blocked; suppressing direct realtime broadcast", conversationId.Value);
            return false;
        }

        return true;
    }
}
