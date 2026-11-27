using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Services;

/// <summary>
/// Application service for querying and recording realtime presence and last-seen activity timestamps.
/// Enforces bidirectional block boundaries and user active verification.
/// </summary>
public sealed class PresenceService : IPresenceService
{
    private readonly IConversationPersistence _conversationPersistence;
    private readonly IUserPresencePersistence _userPresencePersistence;
    private readonly IOnlinePresenceTracker _onlinePresenceTracker;
    private readonly IBlockPersistence _blockPersistence;
    private readonly IUserReader _userReader;

    public PresenceService(
        IConversationPersistence conversationPersistence,
        IUserPresencePersistence userPresencePersistence,
        IOnlinePresenceTracker? onlinePresenceTracker,
        IBlockPersistence blockPersistence,
        IUserReader userReader)
    {
        _conversationPersistence = conversationPersistence;
        _userPresencePersistence = userPresencePersistence;
        _onlinePresenceTracker = onlinePresenceTracker ?? NullOnlinePresenceTracker.Instance;
        _blockPersistence = blockPersistence;
        _userReader = userReader;
    }

    public async Task<UserPresenceDto?> GetPeerPresenceAsync(UserId callerId, UserId targetUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(targetUserId);

        var conv = await _conversationPersistence.GetByParticipantsAsync(callerId, targetUserId, ct);
        if (conv is null)
        {
            return null;
        }

        if (await _blockPersistence.IsBlockedBidirectionalAsync(callerId, targetUserId, ct))
        {
            return null;
        }

        if (!await IsTargetUserActiveAsync(targetUserId, ct))
        {
            return null;
        }

        var isOnline = _onlinePresenceTracker.IsUserOnline(targetUserId);
        if (isOnline)
        {
            return new UserPresenceDto(targetUserId.Value, true, null);
        }

        var dict = await _userPresencePersistence.GetLastSeenBatchAsync([targetUserId], ct);
        dict.TryGetValue(targetUserId, out var lastSeen);
        return new UserPresenceDto(targetUserId.Value, false, lastSeen);
    }

    public async Task<IReadOnlyDictionary<Guid, UserPresenceDto>> GetPresenceForConversationsAsync(UserId callerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);

        var eligiblePeerIds = await GetEligiblePresencePeerIdsAsync(callerId, ct);
        if (eligiblePeerIds.Count == 0)
        {
            return new Dictionary<Guid, UserPresenceDto>();
        }

        var onlineStatus = new Dictionary<UserId, bool>(eligiblePeerIds.Count);
        var offlinePeerIds = new List<UserId>();
        foreach (var peerId in eligiblePeerIds)
        {
            var isOnline = _onlinePresenceTracker.IsUserOnline(peerId);
            onlineStatus[peerId] = isOnline;
            if (!isOnline)
            {
                offlinePeerIds.Add(peerId);
            }
        }

        var lastSeenBatch = offlinePeerIds.Count > 0
            ? await _userPresencePersistence.GetLastSeenBatchAsync(offlinePeerIds, ct)
            : new Dictionary<UserId, DateTimeOffset?>();

        var result = new Dictionary<Guid, UserPresenceDto>(eligiblePeerIds.Count);
        foreach (var peerId in eligiblePeerIds)
        {
            var isOnline = onlineStatus[peerId];
            DateTimeOffset? lastSeen = null;
            if (!isOnline)
            {
                lastSeenBatch.TryGetValue(peerId, out lastSeen);
            }

            result[peerId.Value] = new UserPresenceDto(peerId.Value, isOnline, lastSeen);
        }

        return result;
    }

    public async Task<IReadOnlyList<UserId>> GetEligiblePresencePeerIdsAsync(UserId userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var peerIds = await _conversationPersistence.GetPeerUserIdsWithExistingConversationAsync(userId, ct);
        if (peerIds.Count == 0)
        {
            return [];
        }

        var blockedPeerIds = await _blockPersistence.GetBidirectionalBlockedUserIdsAsync(userId, ct);
        var unblockedPeerIds = peerIds.Where(id => !blockedPeerIds.Contains(id)).ToList();
        if (unblockedPeerIds.Count == 0)
        {
            return [];
        }

        var activePeerIds = await _userReader.GetActiveUserIdsAsync(unblockedPeerIds, ct);
        return unblockedPeerIds.Where(activePeerIds.Contains).ToList();
    }

    public async Task RecordLastSeenAsync(UserId userId, DateTimeOffset lastSeenAtUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        await _userPresencePersistence.UpdateLastSeenAsync(userId, lastSeenAtUtc, ct);
    }

    private Task<bool> IsTargetUserActiveAsync(UserId userId, CancellationToken ct) =>
        _userReader.ExistsAsync(userId, ct);
}
