using System.Text.Json;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core persistence implementation for direct conversations and per-user conversation state.
/// </summary>
public sealed class ConversationPersistence : IConversationPersistence
{
    private readonly ApplicationDbContext _db;

    public ConversationPersistence(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return await _db.Conversations
            .FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Conversation?> GetByParticipantsAsync(UserId userA, UserId userB, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userA);
        ArgumentNullException.ThrowIfNull(userB);

        if (userA == userB)
        {
            return null;
        }

        var p1 = userA < userB ? userA : userB;
        var p2 = userA < userB ? userB : userA;

        return await _db.Conversations
            .FirstOrDefaultAsync(c => c.Participant1Id == p1 && c.Participant2Id == p2, ct);
    }

    public async Task<Conversation> AddAsync(Conversation conversation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        // Check if conversation already exists (canonical ordering ensured by entity)
        var existing = await GetByParticipantsAsync(conversation.Participant1Id, conversation.Participant2Id, ct);
        if (existing is not null)
        {
            return existing;
        }

        try
        {
            _db.Conversations.Add(conversation);
            await _db.SaveChangesAsync(ct);
            return conversation;
        }
        catch (DbUpdateException)
        {
            _db.Entry(conversation).State = EntityState.Detached;

            var existingAfterRace = await GetByParticipantsAsync(conversation.Participant1Id, conversation.Participant2Id, ct);
            if (existingAfterRace is not null)
            {
                return existingAfterRace;
            }

            throw;
        }
    }

    public async Task<UserConversationState?> GetUserStateAsync(ConversationId conversationId, UserId userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(userId);

        return await _db.UserConversationStates
            .FirstOrDefaultAsync(s => s.ConversationId == conversationId && s.UserId == userId, ct);
    }

    public async Task UpsertUserStateAsync(UserConversationState state, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        var existing = await _db.UserConversationStates
            .FirstOrDefaultAsync(s => s.ConversationId == state.ConversationId && s.UserId == state.UserId, ct);

        if (existing is null)
        {
            try
            {
                _db.UserConversationStates.Add(state);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                _db.Entry(state).State = EntityState.Detached;
                var reloaded = await _db.UserConversationStates
                    .FirstOrDefaultAsync(s => s.ConversationId == state.ConversationId && s.UserId == state.UserId, ct);

                if (reloaded is not null)
                {
                    _db.Entry(reloaded).CurrentValues.SetValues(state);
                    await _db.SaveChangesAsync(ct);
                }
                else
                {
                    throw;
                }
            }
        }
        else
        {
            if (!ReferenceEquals(existing, state))
            {
                _db.Entry(existing).CurrentValues.SetValues(state);
            }

            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<Conversation>> GetConversationsForUserAsync(UserId userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        return await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Participant1Id == userId || c.Participant2Id == userId)
            .OrderByDescending(c => _db.Messages
                .Where(m => m.ConversationId == c.Id)
                .Max(m => (DateTimeOffset?)m.CreatedAtUtc) ?? c.CreatedAtUtc)
            .ThenByDescending(c => c.Id)
            .Take(1000)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<UserConversationState>> GetUserStatesForUserAsync(UserId userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        return await _db.UserConversationStates
            .AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToListAsync(ct);
    }

    public Task<IReadOnlyList<UserConversationState>> GetUserStatesForConversationsAsync(
        IReadOnlyList<ConversationId> conversationIds,
        CancellationToken ct = default)
        => GetStatesForConversationsInternalAsync(conversationIds, excludeUserId: null, ct);

    public Task<IReadOnlyList<UserConversationState>> GetPeerStatesForConversationsAsync(
        IReadOnlyList<ConversationId> conversationIds,
        UserId callerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        return GetStatesForConversationsInternalAsync(conversationIds, excludeUserId: callerId, ct);
    }

    private async Task<IReadOnlyList<UserConversationState>> GetStatesForConversationsInternalAsync(
        IReadOnlyList<ConversationId> conversationIds,
        UserId? excludeUserId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);

        if (conversationIds.Count == 0)
        {
            return [];
        }

        var distinctIds = conversationIds.Distinct().ToList();
        var result = new List<UserConversationState>();

        foreach (var chunk in distinctIds.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var query = _db.UserConversationStates
                .AsNoTracking()
                .Where(s => chunkList.Contains(s.ConversationId));

            if (excludeUserId is not null)
            {
                query = query.Where(s => s.UserId != excludeUserId);
            }

            var states = await query.ToListAsync(ct);
            result.AddRange(states);
        }

        return result;
    }

    public async Task<Message?> GetLastMessageAsync(
        ConversationId conversationId,
        DateTimeOffset? minCreatedAtUtc = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        var query = _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId);

        if (minCreatedAtUtc.HasValue)
        {
            query = query.Where(m => m.CreatedAtUtc >= minCreatedAtUtc.Value);
        }

        return await query
            .OrderByDescending(m => m.CreatedAtUtc)
            .ThenByDescending(m => m.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int> GetUnreadCountAsync(
        ConversationId conversationId,
        UserId userId,
        MessageId? lastReadMessageId = null,
        DateTimeOffset? minCreatedAtUtc = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(userId);

        var query = _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId &&
                        m.SenderId != userId &&
                        !_db.UserMessageDeletions.Any(d =>
                            d.ConversationId == conversationId &&
                            d.UserId == userId &&
                            d.MessageId == m.Id));

        if (minCreatedAtUtc.HasValue)
        {
            query = query.Where(m => m.CreatedAtUtc >= minCreatedAtUtc.Value);
        }

        if (lastReadMessageId is not null)
        {
            var lastReadMessage = await _db.Messages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.Id == lastReadMessageId, ct);

            if (lastReadMessage is not null)
            {
                query = query.Where(m => m.CreatedAtUtc > lastReadMessage.CreatedAtUtc ||
                    (m.CreatedAtUtc == lastReadMessage.CreatedAtUtc && m.Id > lastReadMessage.Id));
            }
        }

        return await query.CountAsync(ct);
    }

    public async Task<bool> HasMessagesAfterAsync(
        ConversationId conversationId,
        DateTimeOffset afterUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        return await _db.Messages
            .AsNoTracking()
            .AnyAsync(m => m.ConversationId == conversationId && m.CreatedAtUtc > afterUtc, ct);
    }

    public async Task<Dictionary<ConversationId, Message>> GetLastMessagesForConversationsAsync(
        IReadOnlyList<ConversationId> conversationIds,
        CancellationToken ct = default)
    {
        return await GetLastMessagesForConversationsCoreAsync(conversationIds, callerId: null, ct);
    }

    public async Task<Dictionary<ConversationId, Message>> GetLastMessagesForConversationsAsync(
        IReadOnlyList<ConversationId> conversationIds,
        UserId callerId,
        CancellationToken ct = default)
    {
        return await GetLastMessagesForConversationsCoreAsync(conversationIds, callerId, ct);
    }

    private async Task<Dictionary<ConversationId, Message>> GetLastMessagesForConversationsCoreAsync(
        IReadOnlyList<ConversationId> conversationIds,
        UserId? callerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);

        if (conversationIds.Count == 0)
        {
            return [];
        }

        var distinctIds = conversationIds.Distinct().ToList();
        var result = new Dictionary<ConversationId, Message>();

        foreach (var chunk in distinctIds.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var query = _db.Messages
                .AsNoTracking()
                .Where(m => chunkList.Contains(m.ConversationId));

            if (callerId is not null)
            {
                query = query.Where(m => !_db.UserMessageDeletions.Any(d =>
                    d.ConversationId == m.ConversationId &&
                    d.UserId == callerId &&
                    d.MessageId == m.Id));
            }

            var messages = await query
                .GroupBy(m => m.ConversationId)
                .Select(g => g.OrderByDescending(m => m.CreatedAtUtc).ThenByDescending(m => m.Id).FirstOrDefault()!)
                .ToListAsync(ct);

            foreach (var m in messages.Where(m => m != null))
            {
                result[m.ConversationId] = m;
            }
        }

        return result;
    }

    public async Task<HashSet<ConversationId>> GetConversationsWithMessagesAfterAsync(
        IReadOnlyList<(ConversationId ConversationId, DateTimeOffset ThresholdUtc)> requests,
        CancellationToken ct = default)
    {
        return await GetConversationsWithMessagesAfterCoreAsync(requests, callerId: null, ct);
    }

    public async Task<HashSet<ConversationId>> GetConversationsWithMessagesAfterAsync(
        IReadOnlyList<(ConversationId ConversationId, DateTimeOffset ThresholdUtc)> requests,
        UserId callerId,
        CancellationToken ct = default)
    {
        return await GetConversationsWithMessagesAfterCoreAsync(requests, callerId, ct);
    }

    private async Task<HashSet<ConversationId>> GetConversationsWithMessagesAfterCoreAsync(
        IReadOnlyList<(ConversationId ConversationId, DateTimeOffset ThresholdUtc)> requests,
        UserId? callerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
        {
            return [];
        }

        var thresholdByConv = new Dictionary<ConversationId, DateTimeOffset>();
        foreach (var req in requests)
        {
            if (!thresholdByConv.TryGetValue(req.ConversationId, out var existing) || req.ThresholdUtc < existing)
            {
                thresholdByConv[req.ConversationId] = req.ThresholdUtc;
            }
        }

        var convIds = thresholdByConv.Keys.ToList();
        var minThreshold = requests.Min(r => r.ThresholdUtc);
        var result = new HashSet<ConversationId>();

        foreach (var chunk in convIds.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var query = _db.Messages
                .AsNoTracking()
                .Where(m => chunkList.Contains(m.ConversationId) && m.CreatedAtUtc > minThreshold);

            if (callerId is not null)
            {
                query = query.Where(m => !_db.UserMessageDeletions.Any(d =>
                    d.ConversationId == m.ConversationId &&
                    d.UserId == callerId &&
                    d.MessageId == m.Id));
            }

            var candidates = await query
                .GroupBy(m => m.ConversationId)
                .Select(g => new { ConversationId = g.Key, MaxCreatedAtUtc = g.Max(m => m.CreatedAtUtc) })
                .ToListAsync(ct);

            foreach (var c in candidates)
            {
                if (thresholdByConv.TryGetValue(c.ConversationId, out var threshold) && c.MaxCreatedAtUtc > threshold)
                {
                    result.Add(c.ConversationId);
                }
            }
        }

        return result;
    }

    public async Task<Dictionary<ConversationId, int>> GetUnreadCountsForConversationsAsync(
        UserId userId,
        IReadOnlyList<(ConversationId ConversationId, MessageId? LastReadMessageId, DateTimeOffset? ClearedAtUtc)> requests,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(requests);

        if (requests.Count == 0)
        {
            return [];
        }

        var result = new Dictionary<ConversationId, int>(requests.Count);
        foreach (var req in requests)
        {
            result.TryAdd(req.ConversationId, 0);
        }

        var userGuid = userId.Value;

        foreach (var reqChunk in requests.Chunk(500))
        {
            var payload = reqChunk
                .Select(r => new UnreadCountRequestJson(
                    r.ConversationId.Value,
                    r.LastReadMessageId?.Value,
                    r.ClearedAtUtc))
                .ToArray();
            var requestsJson = JsonSerializer.Serialize(payload);

            var counts = await _db.Database.SqlQuery<UnreadCountRow>(
                $"""
                 SELECT m.[ConversationId], COUNT(*) AS [UnreadCount]
                 FROM [Messages] AS m
                 INNER JOIN OPENJSON({requestsJson}) WITH (
                     [ConversationId] uniqueidentifier '$.ConversationId',
                     [LastReadMessageId] bigint '$.LastReadMessageId',
                     [ClearedAtUtc] datetimeoffset(7) '$.ClearedAtUtc'
                 ) AS r ON r.[ConversationId] = m.[ConversationId]
                 LEFT JOIN [Messages] AS lr ON lr.[ConversationId] = r.[ConversationId] AND lr.[Id] = r.[LastReadMessageId]
                 WHERE m.[SenderId] <> {userGuid}
                   AND (
                       lr.[Id] IS NULL
                       OR m.[CreatedAtUtc] > lr.[CreatedAtUtc]
                       OR (m.[CreatedAtUtc] = lr.[CreatedAtUtc] AND m.[Id] > lr.[Id])
                   )
                   AND (r.[ClearedAtUtc] IS NULL OR m.[CreatedAtUtc] >= r.[ClearedAtUtc])
                   AND NOT EXISTS (
                       SELECT 1
                       FROM [UserMessageDeletions] AS d
                       WHERE d.[ConversationId] = m.[ConversationId]
                         AND d.[UserId] = {userGuid}
                         AND d.[MessageId] = m.[Id]
                   )
                 GROUP BY m.[ConversationId]
                 """)
                .ToListAsync(ct);

            foreach (var item in counts)
            {
                result[new ConversationId(item.ConversationId)] = item.UnreadCount;
            }
        }

        return result;
    }

    public async Task<bool> AdvanceDeliveredWatermarkAsync(
        ConversationId conversationId,
        UserId userId,
        MessageId messageId,
        DateTimeOffset msgCreatedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(messageId);

        await EnsureUserStateExistsAsync(conversationId, userId, ct);

        var rows = await _db.UserConversationStates
            .Where(s => s.ConversationId == conversationId && s.UserId == userId &&
                        (s.LastDeliveredMessageId == null || s.LastDeliveredMessageId < messageId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.LastDeliveredMessageId, messageId)
                .SetProperty(s => s.LastDeliveredAtUtc, msgCreatedAtUtc), ct);

        return rows > 0;
    }

    public async Task<bool> AdvanceReadWatermarkAsync(
        ConversationId conversationId,
        UserId userId,
        MessageId messageId,
        DateTimeOffset msgCreatedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(messageId);

        await EnsureUserStateExistsAsync(conversationId, userId, ct);

        var rows = await _db.UserConversationStates
            .Where(s => s.ConversationId == conversationId && s.UserId == userId &&
                        (s.LastReadMessageId == null || s.LastReadMessageId < messageId))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(s => s.LastReadMessageId, messageId)
                .SetProperty(s => s.LastReadAtUtc, msgCreatedAtUtc)
                .SetProperty(s => s.LastDeliveredMessageId, s => s.LastDeliveredMessageId == null || s.LastDeliveredMessageId < messageId ? messageId : s.LastDeliveredMessageId)
                .SetProperty(s => s.LastDeliveredAtUtc, s => s.LastDeliveredMessageId == null || s.LastDeliveredMessageId < messageId ? msgCreatedAtUtc : s.LastDeliveredAtUtc), ct);

        return rows > 0;
    }

    public async Task<IReadOnlyList<UserId>> GetPeerUserIdsWithExistingConversationAsync(
        UserId userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        return await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Participant1Id == userId || c.Participant2Id == userId)
            .Select(c => c.Participant1Id == userId ? c.Participant2Id : c.Participant1Id)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<UserId?> GetVerifiedTypingRecipientAsync(
        UserId senderId,
        ConversationId conversationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(senderId);
        ArgumentNullException.ThrowIfNull(conversationId);

        var senderGuid = senderId.Value;
        var convGuid = conversationId.Value;

        var rows = await _db.Database.SqlQuery<Guid>(
            $"""
             SELECT TOP (1) u.[Id] AS [Value]
             FROM [Conversations] AS c
             INNER JOIN [AspNetUsers] AS u
                 ON u.[Id] = CASE
                     WHEN c.[Participant1Id] = {senderGuid} THEN c.[Participant2Id]
                     ELSE c.[Participant1Id]
                 END
             WHERE c.[Id] = {convGuid}
               AND (c.[Participant1Id] = {senderGuid} OR c.[Participant2Id] = {senderGuid})
               AND u.[IsDeleted] = 0
               AND u.[EmailConfirmed] = 1
               AND NOT EXISTS (
                   SELECT 1
                   FROM [UserBlocks] AS b
                   WHERE (b.[BlockerId] = {senderGuid} AND b.[BlockedUserId] = u.[Id])
                      OR (b.[BlockerId] = u.[Id] AND b.[BlockedUserId] = {senderGuid})
               )
             """)
            .ToListAsync(ct);

        return rows.Count > 0 ? new UserId(rows[0]) : null;
    }

    public async Task<bool> AreAuthorizedDirectPeersAsync(
        ConversationId conversationId,
        UserId userA,
        UserId userB,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(userA);
        ArgumentNullException.ThrowIfNull(userB);

        if (userA == userB)
        {
            return false;
        }

        return await _db.Conversations
            .AsNoTracking()
            .AnyAsync(c =>
                c.Id == conversationId
                && ((c.Participant1Id == userA && c.Participant2Id == userB)
                    || (c.Participant1Id == userB && c.Participant2Id == userA))
                && !_db.UserBlocks.Any(b =>
                    (b.BlockerId == userA && b.BlockedUserId == userB)
                    || (b.BlockerId == userB && b.BlockedUserId == userA)),
                ct);
    }

    private sealed record UnreadCountRequestJson(
        Guid ConversationId,
        long? LastReadMessageId,
        DateTimeOffset? ClearedAtUtc);

    private sealed record UnreadCountRow(
        Guid ConversationId,
        int UnreadCount);

    private async Task EnsureUserStateExistsAsync(ConversationId conversationId, UserId userId, CancellationToken ct)
    {
        var exists = await _db.UserConversationStates
            .AnyAsync(s => s.ConversationId == conversationId && s.UserId == userId, ct);

        if (exists)
        {
            return;
        }

        var newState = UserConversationState.Create(conversationId, userId);
        try
        {
            _db.UserConversationStates.Add(newState);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.Entry(newState).State = EntityState.Detached;
            var reloaded = await _db.UserConversationStates
                .AnyAsync(s => s.ConversationId == conversationId && s.UserId == userId, ct);
            if (!reloaded)
            {
                throw;
            }
        }
    }
}
