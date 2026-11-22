using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core persistence implementation for messages, idempotency enforcement,
/// deterministic keyset pagination, per-user deletions, and transactional delete-for-everyone.
/// </summary>
public sealed class MessagePersistence : IMessagePersistence
{
    private readonly ApplicationDbContext _db;

    public MessagePersistence(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Message?> GetByIdAsync(ConversationId conversationId, MessageId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(id);

        return await _db.Messages
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.Id == id, ct);
    }

    public async Task<IReadOnlyDictionary<MessageId, Message>> GetByIdsAsync(ConversationId conversationId, IEnumerable<MessageId> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(ids);

        var idList = ids.Distinct().ToList();
        if (idList.Count == 0)
        {
            return new Dictionary<MessageId, Message>();
        }

        var result = new Dictionary<MessageId, Message>();
        foreach (var chunk in idList.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var messages = await _db.Messages
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId && chunkList.Contains(m.Id))
                .ToListAsync(ct);

            foreach (var m in messages)
            {
                result[m.Id] = m;
            }
        }

        return result;
    }

    public async Task<Message?> GetByIdempotencyKeyAsync(UserId senderId, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(senderId);
        ArgumentNullException.ThrowIfNull(idempotencyKey);

        var trimmedKey = idempotencyKey.Trim();
        return await _db.Messages
            .FirstOrDefaultAsync(m => m.SenderId == senderId && m.IdempotencyKey == trimmedKey, ct);
    }

    public async Task<PersistMessageResult> AddAsync(Message message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Check for existing message with this idempotency key for this sender
        var existing = await GetByIdempotencyKeyAsync(message.SenderId, message.IdempotencyKey, ct);
        if (existing is not null)
        {
            if (existing.ConversationId == message.ConversationId
                && existing.Content == message.Content
                && existing.ParentMessageId == message.ParentMessageId)
            {
                return new PersistMessageResult(existing, IsNewlyCreated: false);
            }

            throw new Application.Common.ConflictException("An existing message already exists with this idempotency key in a different context or with different content.");
        }

        try
        {
            _db.Messages.Add(message);
            await _db.SaveChangesAsync(ct);
            return new PersistMessageResult(message, IsNewlyCreated: true);
        }
        catch (DbUpdateException)
        {
            _db.Entry(message).State = EntityState.Detached;

            var existingAfterRace = await GetByIdempotencyKeyAsync(message.SenderId, message.IdempotencyKey, ct);
            if (existingAfterRace is not null)
            {
                if (existingAfterRace.ConversationId == message.ConversationId
                    && existingAfterRace.Content == message.Content
                    && existingAfterRace.ParentMessageId == message.ParentMessageId)
                {
                    return new PersistMessageResult(existingAfterRace, IsNewlyCreated: false);
                }

                throw new Application.Common.ConflictException("An existing message already exists with this idempotency key in a different context or with different content.");
            }

            throw;
        }
    }

    public async Task UpdateAsync(Message message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        _db.Messages.Update(message);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _db.Entry(message).State = EntityState.Detached;
            throw new Application.Common.ConflictException(
                "The message was modified or deleted concurrently. Please refresh and try again.",
                ex);
        }
    }

    public async Task<IReadOnlyList<Message>> GetKeysetMessagesAsync(
        ConversationId conversationId,
        DateTimeOffset? cursorCreatedAtUtc,
        MessageId? cursorId,
        DateTimeOffset? clearedAtUtc,
        int pageSize = 30,
        CancellationToken ct = default)
    {
        return await GetKeysetMessagesAsync(conversationId, callerId: null, cursorCreatedAtUtc, cursorId, clearedAtUtc, pageSize, ct);
    }

    public async Task<IReadOnlyList<Message>> GetKeysetMessagesAsync(
        ConversationId conversationId,
        UserId? callerId,
        DateTimeOffset? cursorCreatedAtUtc,
        MessageId? cursorId,
        DateTimeOffset? clearedAtUtc,
        int pageSize = 30,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        if (pageSize <= 0)
        {
            pageSize = 30;
        }

        var query = _db.Messages
            .AsNoTracking()
            .Where(m => m.ConversationId == conversationId);

        if (callerId is not null)
        {
            query = query.Where(m => !_db.UserMessageDeletions.Any(d =>
                d.ConversationId == conversationId &&
                d.UserId == callerId &&
                d.MessageId == m.Id));
        }

        if (clearedAtUtc.HasValue)
        {
            query = query.Where(m => m.CreatedAtUtc >= clearedAtUtc.Value);
        }

        if (cursorCreatedAtUtc.HasValue && cursorId is not null)
        {
            var cursorTime = cursorCreatedAtUtc.Value;
            var cId = cursorId;
            query = query.Where(m => m.CreatedAtUtc < cursorTime ||
                (m.CreatedAtUtc == cursorTime && m.Id < cId));
        }

        return await query
            .OrderByDescending(m => m.CreatedAtUtc)
            .ThenByDescending(m => m.Id)
            .Take(pageSize + 1)
            .ToListAsync(ct);
    }

    public async Task DeleteForMeAsync(
        ConversationId conversationId,
        MessageId messageId,
        UserId callerId,
        DateTimeOffset deletedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(callerId);

        var existing = await _db.UserMessageDeletions
            .AnyAsync(d => d.ConversationId == conversationId && d.UserId == callerId && d.MessageId == messageId, ct);

        if (existing)
        {
            return;
        }

        var deletion = UserMessageDeletion.Create(conversationId, messageId, callerId, deletedAtUtc);
        try
        {
            _db.UserMessageDeletions.Add(deletion);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.Entry(deletion).State = EntityState.Detached;

            var existsAfterRace = await _db.UserMessageDeletions
                .AnyAsync(d => d.ConversationId == conversationId && d.UserId == callerId && d.MessageId == messageId, ct);
            if (!existsAfterRace)
            {
                throw;
            }
        }
    }

    public async Task<bool> IsMessageDeletedForUserAsync(
        ConversationId conversationId,
        MessageId messageId,
        UserId callerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(callerId);

        return await _db.UserMessageDeletions
            .AnyAsync(d => d.ConversationId == conversationId && d.UserId == callerId && d.MessageId == messageId, ct);
    }

    public async Task<HashSet<MessageId>> GetDeletedMessageIdsForUserAsync(
        ConversationId conversationId,
        UserId callerId,
        IEnumerable<MessageId> messageIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(messageIds);

        var idList = messageIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return [];
        }

        var result = new HashSet<MessageId>();
        foreach (var chunk in idList.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var deleted = await _db.UserMessageDeletions
                .AsNoTracking()
                .Where(d => d.ConversationId == conversationId && d.UserId == callerId && chunkList.Contains(d.MessageId))
                .Select(d => d.MessageId)
                .ToListAsync(ct);

            foreach (var id in deleted)
            {
                result.Add(id);
            }
        }

        return result;
    }

    public async Task DeleteForEveryoneTransactionalAsync(
        ConversationId conversationId,
        MessageId messageId,
        DateTimeOffset deletedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var message = await _db.Messages
                .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.Id == messageId, ct);

            if (message != null)
            {
                message.DeleteForEveryone(deletedAtUtc);

                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    _db.Entry(message).State = EntityState.Detached;
                    throw new Application.Common.ConflictException(
                        "The message was modified concurrently. Please refresh and try again.",
                        ex);
                }

                // Atomically scrub reactions after updating & X-locking Messages (consistent lock order)
                await _db.MessageReactions
                    .Where(r => r.ConversationId == conversationId && r.MessageId == messageId)
                    .ExecuteDeleteAsync(ct);
            }

            await tx.CommitAsync(ct);
        });
    }
}
