using System.Linq.Expressions;
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
            .OrderByDescending(c => c.CreatedAtUtc)
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

    public async Task<IReadOnlyList<UserConversationState>> GetUserStatesForConversationsAsync(
        IReadOnlyList<ConversationId> conversationIds,
        CancellationToken ct = default)
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
            var states = await _db.UserConversationStates
                .AsNoTracking()
                .Where(s => chunkList.Contains(s.ConversationId))
                .ToListAsync(ct);

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

        var lastReadIds = requests
            .Where(r => r.LastReadMessageId is not null)
            .Select(r => r.LastReadMessageId!)
            .Distinct()
            .ToList();

        var lastReadMap = new Dictionary<MessageId, Message>();
        if (lastReadIds.Count > 0)
        {
            foreach (var chunk in lastReadIds.Chunk(500))
            {
                var chunkList = chunk.ToList();
                var chunkMap = await _db.Messages
                    .AsNoTracking()
                    .Where(m => chunkList.Contains(m.Id))
                    .ToDictionaryAsync(m => m.Id, ct);

                foreach (var (k, v) in chunkMap)
                {
                    lastReadMap[k] = v;
                }
            }
        }

        var parameter = Expression.Parameter(typeof(Message), "m");
        var convIdProp = Expression.Property(parameter, nameof(Message.ConversationId));
        var senderIdProp = Expression.Property(parameter, nameof(Message.SenderId));
        var createdAtProp = Expression.Property(parameter, nameof(Message.CreatedAtUtc));
        var idProp = Expression.Property(parameter, nameof(Message.Id));

        var notSender = Expression.NotEqual(
            senderIdProp,
            Expression.Constant(userId));

        var result = new Dictionary<ConversationId, int>();
        foreach (var req in requests)
        {
            result.TryAdd(req.ConversationId, 0);
        }

        foreach (var reqChunk in requests.Chunk(500))
        {
            Expression? combinedOr = null;

            foreach (var req in reqChunk)
            {
                var convMatch = Expression.Equal(
                    convIdProp,
                    Expression.Constant(req.ConversationId));

                Expression? filter = null;

                if (req.LastReadMessageId is not null &&
                    lastReadMap.TryGetValue(req.LastReadMessageId, out var lastRead) &&
                    lastRead.ConversationId == req.ConversationId)
                {
                    var createdAfter = Expression.GreaterThan(
                        createdAtProp,
                        Expression.Constant(lastRead.CreatedAtUtc));
                    var createdEqual = Expression.Equal(
                        createdAtProp,
                        Expression.Constant(lastRead.CreatedAtUtc));
                    var idGreater = Expression.GreaterThan(
                        idProp,
                        Expression.Constant(lastRead.Id));
                    var tieBreaker = Expression.AndAlso(createdEqual, idGreater);
                    filter = Expression.OrElse(createdAfter, tieBreaker);
                }

                if (req.ClearedAtUtc.HasValue)
                {
                    var afterCleared = Expression.GreaterThanOrEqual(
                        createdAtProp,
                        Expression.Constant(req.ClearedAtUtc.Value));
                    filter = filter is null
                        ? afterCleared
                        : Expression.AndAlso(filter, afterCleared);
                }

                var convCondition = filter is null
                    ? convMatch
                    : Expression.AndAlso(convMatch, filter);

                combinedOr = combinedOr is null
                    ? convCondition
                    : Expression.OrElse(combinedOr, convCondition);
            }

            if (combinedOr is not null)
            {
                var body = Expression.AndAlso(notSender, combinedOr);
                var lambda = Expression.Lambda<Func<Message, bool>>(body, parameter);

                var counts = await _db.Messages
                    .AsNoTracking()
                    .Where(lambda)
                    .Where(m => !_db.UserMessageDeletions.Any(d =>
                        d.ConversationId == m.ConversationId &&
                        d.UserId == userId &&
                        d.MessageId == m.Id))
                    .GroupBy(m => m.ConversationId)
                    .Select(g => new { ConversationId = g.Key, Count = g.Count() })
                    .ToListAsync(ct);

                foreach (var item in counts)
                {
                    result[item.ConversationId] = item.Count;
                }
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
