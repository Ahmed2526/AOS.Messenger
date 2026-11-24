using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Services;

/// <summary>
/// Application service implementation orchestrating direct conversation lifecycle,
/// participant authorization, bidirectional block enforcement, and per-user state management.
/// </summary>
public sealed class ConversationService : IConversationService
{
    private readonly IConversationPersistence _conversationPersistence;
    private readonly IUserReader _userReader;
    private readonly IBlockPersistence _blockPersistence;
    private readonly IBlockService _blockService;
    private readonly IOnlinePresenceTracker _onlinePresenceTracker;
    private readonly IUserPresencePersistence? _userPresencePersistence;
    private readonly IMediaPersistence _mediaPersistence;
    private readonly TimeProvider _timeProvider;

    public ConversationService(
        IConversationPersistence conversationPersistence,
        IUserReader userReader,
        IBlockPersistence blockPersistence,
        IBlockService blockService,
        IOnlinePresenceTracker? onlinePresenceTracker = null,
        IUserPresencePersistence? userPresencePersistence = null,
        TimeProvider? timeProvider = null,
        IMediaPersistence? mediaPersistence = null)
    {
        _conversationPersistence = conversationPersistence;
        _userReader = userReader;
        _blockPersistence = blockPersistence;
        _blockService = blockService;
        _onlinePresenceTracker = onlinePresenceTracker ?? NullOnlinePresenceTracker.Instance;
        _userPresencePersistence = userPresencePersistence;
        _mediaPersistence = mediaPersistence ?? NullMediaPersistence.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ConversationDetailDto> GetOrCreateConversationAsync(
        UserId currentUserId,
        UserId targetUserId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentUserId);
        ArgumentNullException.ThrowIfNull(targetUserId);

        if (currentUserId == targetUserId)
        {
            throw new DomainException("Cannot create a conversation with oneself.");
        }

        var targetExists = await _userReader.ExistsAsync(targetUserId, ct);
        if (!targetExists)
        {
            throw new NotFoundException("User", targetUserId.Value);
        }

        var isBlocked = await _blockService.IsBlockedBidirectionalAsync(currentUserId, targetUserId, ct);
        if (isBlocked)
        {
            throw new ForbiddenException("Cannot start a conversation with a blocked user.");
        }

        var existing = await _conversationPersistence.GetByParticipantsAsync(currentUserId, targetUserId, ct);
        if (existing is not null)
        {
            return await BuildConversationDetailDtoAsync(existing, currentUserId, ct);
        }

        var newConversation = Conversation.Create(currentUserId, targetUserId, _timeProvider.GetUtcNow());
        var created = await _conversationPersistence.AddAsync(newConversation, ct);

        return await BuildConversationDetailDtoAsync(created, currentUserId, ct);
    }

    public async Task<ConversationDetailDto> GetConversationAsync(
        UserId currentUserId,
        ConversationId conversationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentUserId);
        ArgumentNullException.ThrowIfNull(conversationId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(currentUserId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        return await BuildConversationDetailDtoAsync(conversation, currentUserId, ct);
    }

    public async Task<IReadOnlyList<ConversationSummaryDto>> GetConversationListAsync(
        UserId currentUserId,
        bool includeArchived = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentUserId);

        var conversations = await _conversationPersistence.GetConversationsForUserAsync(currentUserId, ct);
        if (conversations.Count == 0)
        {
            return [];
        }

        var userStates = await _conversationPersistence.GetUserStatesForUserAsync(currentUserId, ct);
        var stateByConversationId = userStates.ToDictionary(s => s.ConversationId);

        // Identify conversations with DeletedAtUtc to check for new messages in batch
        var deletedRequests = new List<(ConversationId ConversationId, DateTimeOffset ThresholdUtc)>();
        foreach (var conversation in conversations)
        {
            if (stateByConversationId.TryGetValue(conversation.Id, out var state) && state.DeletedAtUtc.HasValue)
            {
                deletedRequests.Add((conversation.Id, state.DeletedAtUtc.Value));
            }
        }

        var convsWithNewMessages = deletedRequests.Count > 0
            ? await _conversationPersistence.GetConversationsWithMessagesAfterAsync(deletedRequests, currentUserId, ct)
            : [];

        // Filter active conversations based on archived and soft-deleted states
        var activeConversations = new List<Conversation>();
        foreach (var conversation in conversations)
        {
            stateByConversationId.TryGetValue(conversation.Id, out var state);

            var isArchived = state?.IsArchived ?? false;

            // Archive filter: if includeArchived is false, exclude archived
            if (!includeArchived && isArchived)
            {
                continue;
            }

            // Soft-deleted filter: filter out if DeletedAtUtc is set and no message has arrived after DeletedAtUtc
            if (state?.DeletedAtUtc is not null && !convsWithNewMessages.Contains(conversation.Id))
            {
                continue;
            }

            activeConversations.Add(conversation);
        }

        if (activeConversations.Count == 0)
        {
            return [];
        }

        var activeConvIds = activeConversations.Select(c => c.Id).ToList();

        // Batch fetch last messages for all active conversations respecting caller's deletions
        var lastMessages = await _conversationPersistence.GetLastMessagesForConversationsAsync(activeConvIds, currentUserId, ct);

        // Batch fetch unread counts for all active conversations
        var unreadRequests = new List<(ConversationId ConversationId, MessageId? LastReadMessageId, DateTimeOffset? ClearedAtUtc)>(activeConversations.Count);
        foreach (var conv in activeConversations)
        {
            stateByConversationId.TryGetValue(conv.Id, out var state);

            DateTimeOffset? minCreatedAtUtc = state?.ClearedAtUtc;
            if (state?.DeletedAtUtc is not null)
            {
                if (!minCreatedAtUtc.HasValue || state.DeletedAtUtc.Value > minCreatedAtUtc.Value)
                {
                    minCreatedAtUtc = state.DeletedAtUtc.Value;
                }
            }

            unreadRequests.Add((conv.Id, state?.LastReadMessageId, minCreatedAtUtc));
        }

        var unreadCounts = await _conversationPersistence.GetUnreadCountsForConversationsAsync(currentUserId, unreadRequests, ct);

        // Batch fetch user profiles for other participants
        var otherParticipantIds = activeConversations
            .Select(c => c.GetOtherParticipant(currentUserId))
            .Distinct()
            .ToList();

        var profiles = await _userReader.GetByIdsAsync(otherParticipantIds, ct);
        var profileById = profiles.ToDictionary(p => p.Id);

        var blockedUserIds = await _blockPersistence.GetBidirectionalBlockedUserIdsAsync(currentUserId, ct);

        var offlineEligiblePeerIds = new List<UserId>();
        foreach (var peerId in otherParticipantIds)
        {
            if (!blockedUserIds.Contains(peerId) &&
                profileById.TryGetValue(peerId, out var p) &&
                !p.IsDeleted)
            {
                if (!_onlinePresenceTracker.IsUserOnline(peerId))
                {
                    offlineEligiblePeerIds.Add(peerId);
                }
            }
        }

        var lastSeenMap = _userPresencePersistence is not null && offlineEligiblePeerIds.Count > 0
            ? await _userPresencePersistence.GetLastSeenBatchAsync(offlineEligiblePeerIds, ct)
            : new Dictionary<UserId, DateTimeOffset?>();

        var allConversationStates = await _conversationPersistence.GetUserStatesForConversationsAsync(activeConvIds, ct);
        var stateByConvAndUser = allConversationStates.ToDictionary(s => (s.ConversationId, s.UserId));

        var mediaLookupKeys = new List<(ConversationId ConversationId, MessageId MessageId)>();
        foreach (var conversation in activeConversations)
        {
            stateByConversationId.TryGetValue(conversation.Id, out var state);
            var minCreatedAtUtc = ComputeEffectiveCutoffUtc(state);

            if (lastMessages.TryGetValue(conversation.Id, out var lm)
                && (!minCreatedAtUtc.HasValue || lm.CreatedAtUtc >= minCreatedAtUtc.Value)
                && !lm.IsDeletedForEveryone
                && lm.HasAttachments)
            {
                mediaLookupKeys.Add((conversation.Id, lm.Id));
            }
        }

        var lastMessageMedia = mediaLookupKeys.Count > 0
            ? await _mediaPersistence.GetByMessageIdsAcrossConversationsAsync(mediaLookupKeys, ct)
            : new Dictionary<(ConversationId ConversationId, MessageId MessageId), IReadOnlyList<Media>>();

        var summaries = new List<ConversationSummaryDto>(activeConversations.Count);

        foreach (var conversation in activeConversations)
        {
            stateByConversationId.TryGetValue(conversation.Id, out var state);

            var isArchived = state?.IsArchived ?? false;
            var isPinned = state?.IsPinned ?? false;
            var isMuted = state?.MutedAtUtc != null;

            var minCreatedAtUtc = ComputeEffectiveCutoffUtc(state);

            lastMessages.TryGetValue(conversation.Id, out var lastMessage);
            if (lastMessage is not null && minCreatedAtUtc.HasValue && lastMessage.CreatedAtUtc < minCreatedAtUtc.Value)
            {
                lastMessage = null;
            }

            string? lastMessagePreview = null;
            var lastActivityAtUtc = conversation.CreatedAtUtc;

            if (lastMessage is not null)
            {
                lastActivityAtUtc = lastMessage.CreatedAtUtc;
                if (lastMessage.IsDeletedForEveryone)
                {
                    lastMessagePreview = "This message was deleted";
                }
                else if (lastMessage.HasAttachments)
                {
                    lastMessageMedia.TryGetValue((conversation.Id, lastMessage.Id), out var attachments);
                    lastMessagePreview = MediaPreviewFormatter.FormatPreview(lastMessage.Content, attachments);
                }
                else
                {
                    lastMessagePreview = lastMessage.Content;
                }
            }

            unreadCounts.TryGetValue(conversation.Id, out var unreadCount);

            var otherId = conversation.GetOtherParticipant(currentUserId);
            var isBlocked = blockedUserIds.Contains(otherId);
            var isPeerDeleted = !profileById.TryGetValue(otherId, out var profile) || profile.IsDeleted;

            bool isOnline = false;
            DateTimeOffset? lastSeen = null;
            MessageId? peerDelivered = null;
            MessageId? peerRead = null;

            if (!isBlocked && !isPeerDeleted)
            {
                isOnline = _onlinePresenceTracker.IsUserOnline(otherId);
                if (!isOnline)
                {
                    lastSeenMap.TryGetValue(otherId, out lastSeen);
                }

                stateByConvAndUser.TryGetValue((conversation.Id, otherId), out var peerState);
                peerDelivered = peerState?.LastDeliveredMessageId;
                peerRead = peerState?.LastReadMessageId;
            }

            string name;
            string handle;

            if (!isPeerDeleted && profile is not null)
            {
                name = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.UserName : profile.DisplayName;
                handle = profile.UserName;
            }
            else
            {
                name = "Deleted Account";
                handle = "deleted_user";
            }

            summaries.Add(new ConversationSummaryDto(
                conversation.Id,
                otherId,
                name,
                handle,
                lastMessagePreview,
                lastActivityAtUtc,
                unreadCount,
                isPinned,
                isArchived,
                isMuted,
                isOnline,
                lastSeen,
                peerDelivered,
                peerRead,
                lastMessage?.Id));
        }

        // Deterministic ordering: Pinned first (IsPinned DESC), then by LastActivityAtUtc DESC, then Id DESC
        return summaries
            .OrderByDescending(s => s.IsPinned)
            .ThenByDescending(s => s.LastActivityAtUtc)
            .ThenByDescending(s => s.Id.Value)
            .ToList();
    }

    public async Task<bool> PinConversationAsync(
        UserId currentUserId,
        ConversationId conversationId,
        bool isPinned,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentUserId);
        ArgumentNullException.ThrowIfNull(conversationId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(currentUserId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var state = await _conversationPersistence.GetUserStateAsync(conversationId, currentUserId, ct)
            ?? UserConversationState.Create(conversationId, currentUserId);

        if (isPinned)
        {
            state.Pin(_timeProvider.GetUtcNow());
        }
        else
        {
            state.Unpin();
        }

        await _conversationPersistence.UpsertUserStateAsync(state, ct);
        return true;
    }

    public async Task<bool> ArchiveConversationAsync(
        UserId currentUserId,
        ConversationId conversationId,
        bool isArchived,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentUserId);
        ArgumentNullException.ThrowIfNull(conversationId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(currentUserId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var state = await _conversationPersistence.GetUserStateAsync(conversationId, currentUserId, ct)
            ?? UserConversationState.Create(conversationId, currentUserId);

        if (isArchived)
        {
            state.Archive(_timeProvider.GetUtcNow());
        }
        else
        {
            state.Unarchive();
        }

        await _conversationPersistence.UpsertUserStateAsync(state, ct);
        return true;
    }

    public async Task<bool> MuteConversationAsync(
        UserId currentUserId,
        ConversationId conversationId,
        bool isMuted,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentUserId);
        ArgumentNullException.ThrowIfNull(conversationId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(currentUserId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var state = await _conversationPersistence.GetUserStateAsync(conversationId, currentUserId, ct)
            ?? UserConversationState.Create(conversationId, currentUserId);

        if (isMuted)
        {
            state.Mute(_timeProvider.GetUtcNow());
        }
        else
        {
            state.Unmute();
        }

        await _conversationPersistence.UpsertUserStateAsync(state, ct);
        return true;
    }

    public async Task<bool> ClearChatAsync(
        UserId currentUserId,
        ConversationId conversationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentUserId);
        ArgumentNullException.ThrowIfNull(conversationId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(currentUserId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var state = await _conversationPersistence.GetUserStateAsync(conversationId, currentUserId, ct)
            ?? UserConversationState.Create(conversationId, currentUserId);

        state.ClearChat(_timeProvider.GetUtcNow());

        await _conversationPersistence.UpsertUserStateAsync(state, ct);
        return true;
    }

    public async Task<bool> DeleteConversationAsync(
        UserId currentUserId,
        ConversationId conversationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(currentUserId);
        ArgumentNullException.ThrowIfNull(conversationId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(currentUserId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var state = await _conversationPersistence.GetUserStateAsync(conversationId, currentUserId, ct)
            ?? UserConversationState.Create(conversationId, currentUserId);

        state.DeleteConversation(_timeProvider.GetUtcNow());

        await _conversationPersistence.UpsertUserStateAsync(state, ct);
        return true;
    }

    private async Task<ConversationDetailDto> BuildConversationDetailDtoAsync(
        Conversation conversation,
        UserId currentUserId,
        CancellationToken ct)
    {
        var otherParticipantId = conversation.GetOtherParticipant(currentUserId);
        var profile = await _userReader.GetByIdAsync(otherParticipantId, ct);

        string displayName;
        string userName;
        string? bio = null;

        if (profile is null || profile.IsDeleted)
        {
            displayName = "Deleted Account";
            userName = "deleted_user";
            bio = null;
        }
        else
        {
            displayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.UserName : profile.DisplayName;
            userName = profile.UserName;
            bio = profile.Bio;
        }

        var isPeerDeleted = profile is null || profile.IsDeleted;
        var isBlocked = await _blockService.IsBlockedBidirectionalAsync(currentUserId, otherParticipantId, ct);
        var canSend = !isBlocked;
        var state = await _conversationPersistence.GetUserStateAsync(conversation.Id, currentUserId, ct);

        var isPinned = state?.IsPinned ?? false;
        var isArchived = state?.IsArchived ?? false;
        var isMuted = state?.MutedAtUtc != null;

        bool isOnline = false;
        DateTimeOffset? lastSeen = null;
        MessageId? peerDelivered = null;
        MessageId? peerRead = null;

        if (!isBlocked && !isPeerDeleted)
        {
            var peerState = await _conversationPersistence.GetUserStateAsync(conversation.Id, otherParticipantId, ct);
            peerDelivered = peerState?.LastDeliveredMessageId;
            peerRead = peerState?.LastReadMessageId;

            isOnline = _onlinePresenceTracker.IsUserOnline(otherParticipantId);
            if (!isOnline && _userPresencePersistence is not null)
            {
                var dict = await _userPresencePersistence.GetLastSeenBatchAsync(new[] { otherParticipantId }, ct);
                dict.TryGetValue(otherParticipantId, out lastSeen);
            }
        }

        return new ConversationDetailDto(
            conversation.Id,
            otherParticipantId,
            displayName,
            userName,
            bio,
            isBlocked,
            isPinned,
            isArchived,
            isMuted,
            conversation.CreatedAtUtc,
            isOnline,
            lastSeen,
            peerDelivered,
            peerRead,
            isPeerDeleted,
            canSend);
    }

    private static DateTimeOffset? ComputeEffectiveCutoffUtc(UserConversationState? state)
    {
        DateTimeOffset? cutoff = state?.ClearedAtUtc;
        if (state?.DeletedAtUtc is not null &&
            (!cutoff.HasValue || state.DeletedAtUtc.Value > cutoff.Value))
        {
            cutoff = state.DeletedAtUtc.Value;
        }

        return cutoff;
    }
}
