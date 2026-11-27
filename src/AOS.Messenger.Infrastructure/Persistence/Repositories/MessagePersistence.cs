using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
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
            return ValidateExistingIdempotentMessage(existing, message);
        }

        if (message.StickerId is not null)
        {
            return await AddWithVerifiedStickerAsync(message, ct);
        }

        try
        {
            _db.Messages.Add(message);
            await _db.SaveChangesAsync(ct);
            return new PersistMessageResult(message, IsNewlyCreated: true);
        }
        catch (DbUpdateException ex)
        {
            _db.Entry(message).State = EntityState.Detached;

            var existingAfterRace = await GetByIdempotencyKeyAsync(message.SenderId, message.IdempotencyKey, ct);
            if (existingAfterRace is not null)
            {
                return ValidateExistingIdempotentMessage(existingAfterRace, message);
            }

            ThrowIfTranslatedSqlFailure(ex);
            throw;
        }
    }

    private async Task<PersistMessageResult> AddWithVerifiedStickerAsync(Message message, CancellationToken ct)
    {
        var stickerId = message.StickerId!;
        PersistMessageResult? persistedResult = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();

            var attemptMessage = Message.Create(
                message.ConversationId,
                message.SenderId,
                message.IdempotencyKey,
                message.Content,
                message.CreatedAtUtc,
                message.ParentMessageId,
                id: null,
                hasAttachments: message.HasAttachments,
                stickerId: stickerId);

            // Re-verify Ready + active pack + sender authorization under RepeatableRead so shared locks
            // prevent concurrent archive/pack-deletion from committing mid-flight before the message insert.
            await using var tx = await _db.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.RepeatableRead,
                ct);

            try
            {
                var isStickerSendable = await (
                    from s in _db.Stickers.AsNoTracking()
                    join p in _db.StickerPacks.AsNoTracking() on s.PackId equals p.Id
                    where s.Id == stickerId
                          && s.State == StickerState.Ready
                          && s.PurgedAtUtc == null
                          && (p.IsSystemPack
                              ? !p.IsDeleted
                              : (!string.IsNullOrEmpty(s.ObjectKey)
                                 && ((!p.IsDeleted && s.UploaderId == attemptMessage.SenderId)
                                     || _db.UserFavoriteStickers.Any(f => f.UserId == attemptMessage.SenderId && f.StickerId == stickerId)
                                     || (_db.UserRecentStickers.Any(r => r.UserId == attemptMessage.SenderId && r.StickerId == stickerId)
                                         && (!p.IsDeleted || s.UploaderId != attemptMessage.SenderId)))))
                    select s.Id).AnyAsync(ct);

                if (!isStickerSendable)
                {
                    await tx.RollbackAsync(ct);
                    _db.ChangeTracker.Clear();

                    var existingBeforeStickerCheck = await GetByIdempotencyKeyAsync(
                        attemptMessage.SenderId,
                        attemptMessage.IdempotencyKey,
                        ct);
                    if (existingBeforeStickerCheck is not null)
                    {
                        persistedResult = ValidateExistingIdempotentMessage(existingBeforeStickerCheck, attemptMessage);
                        return;
                    }

                    throw new NotFoundException("Sticker", stickerId.Value);
                }

                _db.Messages.Add(attemptMessage);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                persistedResult = new PersistMessageResult(attemptMessage, IsNewlyCreated: true);
            }
            catch (Exception ex) when (ex is DbUpdateException or SqlException)
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();

                if (ex is DbUpdateException)
                {
                    var existingAfterRace = await GetByIdempotencyKeyAsync(
                        attemptMessage.SenderId,
                        attemptMessage.IdempotencyKey,
                        ct);
                    if (existingAfterRace is not null)
                    {
                        persistedResult = ValidateExistingIdempotentMessage(existingAfterRace, attemptMessage);
                        return;
                    }
                }

                ThrowIfTranslatedSqlFailure(ex);
                throw;
            }
        });

        return persistedResult!;
    }

    private static PersistMessageResult ValidateExistingIdempotentMessage(Message existing, Message candidate)
    {
        if (existing.IsDeletedForEveryone)
        {
            throw new ConflictException("This message was already deleted.");
        }

        if (existing.ConversationId == candidate.ConversationId
            && existing.Content == candidate.Content
            && existing.ParentMessageId == candidate.ParentMessageId
            && existing.HasAttachments == candidate.HasAttachments
            && existing.StickerId == candidate.StickerId)
        {
            return new PersistMessageResult(existing, IsNewlyCreated: false);
        }

        throw new Application.Common.ConflictException(
            "An existing message already exists with this idempotency key in a different context or with different content or attachments.");
    }

    public async Task<PersistMessageResult> AddWithMediaAsync(
        Message message,
        IReadOnlyList<MediaId> mediaIds,
        DateTimeOffset attachedAtUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(mediaIds);

        if (mediaIds.Count > MediaConstraints.MaxAttachmentsPerMessage)
        {
            throw new DomainException(
                $"A message cannot have more than {MediaConstraints.MaxAttachmentsPerMessage} attachments.");
        }

        if (message.HasAttachments && mediaIds.Count == 0)
        {
            throw new DomainException("At least one attachment is required when HasAttachments is true.");
        }

        if (!message.HasAttachments && mediaIds.Count > 0)
        {
            throw new DomainException("Message must be marked HasAttachments when attaching media.");
        }

        var distinctIds = new HashSet<MediaId>();
        foreach (var mediaId in mediaIds)
        {
            ArgumentNullException.ThrowIfNull(mediaId);
            if (!distinctIds.Add(mediaId))
            {
                throw new DomainException("Duplicate media attachments in the same message are not allowed.");
            }
        }

        var preExisting = await ResolveExistingIdempotentMediaMessageAsync(message, mediaIds, ct);
        if (preExisting is not null)
        {
            return preExisting;
        }

        PersistMessageResult? persistedResult = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();

            var attemptMessage = Message.Create(
                message.ConversationId,
                message.SenderId,
                message.IdempotencyKey,
                message.Content,
                message.CreatedAtUtc,
                message.ParentMessageId,
                id: null,
                hasAttachments: message.HasAttachments);

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            try
            {
                _db.Messages.Add(attemptMessage);
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex)
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();

                if (IsUniqueConstraintViolation(ex))
                {
                    var resolvedAfterRace = await ResolveExistingIdempotentMediaMessageAsync(message, mediaIds, ct);
                    if (resolvedAfterRace is not null)
                    {
                        persistedResult = resolvedAfterRace;
                        return;
                    }
                }

                ThrowIfTranslatedSqlFailure(ex);
                throw;
            }

            if (mediaIds.Count > 0)
            {
                var orderedTargets = mediaIds
                    .Select((mediaId, index) => (MediaId: mediaId, DisplayOrder: index))
                    .OrderBy(item => item.MediaId.Value)
                    .ToList();

                foreach (var target in orderedTargets)
                {
                    int rowsAffected;
                    try
                    {
                        rowsAffected = await _db.Media
                            .Where(m => m.Id == target.MediaId
                                        && m.UploaderId == attemptMessage.SenderId
                                        && m.ConversationId == attemptMessage.ConversationId
                                        && m.State == MediaState.Uploaded
                                        && m.MessageId == null)
                            .ExecuteUpdateAsync(setters => setters
                                .SetProperty(m => m.State, MediaState.Attached)
                                .SetProperty(m => m.MessageId, (MessageId?)attemptMessage.Id)
                                .SetProperty(m => m.DisplayOrder, target.DisplayOrder)
                                .SetProperty(m => m.AttachedAtUtc, (DateTimeOffset?)attachedAtUtc), ct);
                    }
                    catch (Exception ex) when (ex is DbUpdateException or SqlException)
                    {
                        await tx.RollbackAsync(ct);
                        _db.ChangeTracker.Clear();
                        ThrowIfTranslatedSqlFailure(ex);
                        throw;
                    }

                    if (rowsAffected != 1)
                    {
                        var isOwnedInConversation = await _db.Media
                            .AsNoTracking()
                            .AnyAsync(m => m.Id == target.MediaId
                                           && m.UploaderId == attemptMessage.SenderId
                                           && m.ConversationId == attemptMessage.ConversationId, ct);

                        await tx.RollbackAsync(ct);
                        _db.ChangeTracker.Clear();

                        if (!isOwnedInConversation)
                        {
                            throw new ForbiddenException("One or more media attachments are not accessible.");
                        }

                        throw new ConflictException(
                            "One or more media attachments are not ready or were already attached to another message.");
                    }
                }
            }

            var attachedMedia = mediaIds.Count == 0
                ? (IReadOnlyList<Media>)[]
                : await _db.Media
                    .AsNoTracking()
                    .Where(m => m.ConversationId == attemptMessage.ConversationId
                                && m.MessageId == attemptMessage.Id
                                && m.State == MediaState.Attached)
                    .OrderBy(m => m.DisplayOrder)
                    .ToListAsync(ct);

            await tx.CommitAsync(ct);
            persistedResult = new PersistMessageResult(attemptMessage, IsNewlyCreated: true, attachedMedia);
        });

        return persistedResult!;
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
        int pageSize = Application.Common.PaginationDefaults.DefaultMessagePageSize,
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
        int pageSize = Application.Common.PaginationDefaults.DefaultMessagePageSize,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        if (pageSize <= 0)
        {
            pageSize = Application.Common.PaginationDefaults.DefaultMessagePageSize;
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
            query = query.Where(m => m.CreatedAtUtc <= cursorTime
                                     && (m.CreatedAtUtc < cursorTime
                                         || (m.CreatedAtUtc == cursorTime && m.Id < cId)));
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
            _db.ChangeTracker.Clear();
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
                    throw new ConflictException(
                        "The message was modified concurrently. Please refresh and try again.",
                        ex);
                }

                // Atomically scrub reactions and attached media after updating & X-locking Messages (consistent lock order)
                await _db.MessageReactions
                    .Where(r => r.ConversationId == conversationId && r.MessageId == messageId)
                    .ExecuteDeleteAsync(ct);

                await _db.Media
                    .Where(m => m.ConversationId == conversationId
                                && m.MessageId == messageId
                                && m.State == MediaState.Attached)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(m => m.State, MediaState.Deleted)
                        .SetProperty(m => m.DeletedAtUtc, (DateTimeOffset?)deletedAtUtc), ct);
            }

            await tx.CommitAsync(ct);
        });
    }

    private async Task<PersistMessageResult?> ResolveExistingIdempotentMediaMessageAsync(
        Message candidate,
        IReadOnlyList<MediaId> requestedMediaIds,
        CancellationToken ct)
    {
        var trimmedKey = candidate.IdempotencyKey.Trim();
        var existing = await _db.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.SenderId == candidate.SenderId && m.IdempotencyKey == trimmedKey, ct);

        if (existing is null)
        {
            return null;
        }

        if (existing.IsDeletedForEveryone)
        {
            throw new ConflictException("This message was already deleted.");
        }

        var existingMedia = existing.HasAttachments
            ? await _db.Media
                .AsNoTracking()
                .Where(m => m.ConversationId == existing.ConversationId
                            && m.MessageId == existing.Id
                            && m.State == MediaState.Attached)
                .OrderBy(m => m.DisplayOrder)
                .ToListAsync(ct)
            : [];

        var matches = existing.ConversationId == candidate.ConversationId
            && existing.Content == candidate.Content
            && existing.ParentMessageId == candidate.ParentMessageId
            && existing.HasAttachments == candidate.HasAttachments
            && existing.StickerId == candidate.StickerId
            && existingMedia.Count == requestedMediaIds.Count
            && existingMedia.Select(m => m.Id).SequenceEqual(requestedMediaIds);

        if (!matches)
        {
            throw new ConflictException(
                "An existing message already exists with this idempotency key in a different context or with different content or attachments.");
        }

        return new PersistMessageResult(existing, IsNewlyCreated: false, existingMedia);
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (Exception? current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlEx && sqlEx.Number is 2601 or 2627)
            {
                return true;
            }
        }

        return false;
    }

    private static void ThrowIfTranslatedSqlFailure(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlEx)
            {
                if (sqlEx.Number == 1205)
                {
                    throw new ConflictException("A concurrent database conflict occurred. Please try again.", ex);
                }

                if (sqlEx.Number == 547)
                {
                    throw new ConflictException("The referenced conversation, sticker, or reply target was modified concurrently. Please try again.", ex);
                }
            }
        }
    }
}
