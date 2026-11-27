using System.Data;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core persistence implementation for media upload quotas, attachment lookups, and lifecycle updates.
/// </summary>
public sealed class MediaPersistence : IMediaPersistence
{
    private readonly ApplicationDbContext _db;

    public MediaPersistence(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<Media> TryCreatePendingWithQuotaCheckAsync(
        Media media,
        int maxConcurrentPending,
        long dailyQuotaBytes,
        DateTimeOffset dailyQuotaWindowStartUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentPending);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dailyQuotaBytes);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var rcParam = new SqlParameter("@rc", SqlDbType.Int)
            {
                Direction = ParameterDirection.Output
            };
            var resParam = new SqlParameter("@res", SqlDbType.NVarChar, 255)
            {
                Value = $"media-quota:{media.UploaderId.Value:N}"
            };

            await _db.Database.ExecuteSqlRawAsync(
                "EXEC @rc = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000",
                new object[] { rcParam, resParam },
                ct);

            if (rcParam.Value is not int lockResult || lockResult < 0)
            {
                await tx.RollbackAsync(ct);
                throw new ConflictException("Too many concurrent media upload requests. Please try again.");
            }

            var mediaPendingCount = await _db.Media
                .CountAsync(m => m.UploaderId == media.UploaderId && m.State == MediaState.Pending, ct);
            var avatarPendingCount = await _db.PendingAvatarUploads
                .CountAsync(p => p.UserId == media.UploaderId && p.State == MediaState.Pending, ct);
            var stickerPendingCount = await _db.Stickers
                .CountAsync(s => s.UploaderId == media.UploaderId && s.State == StickerState.Pending, ct);

            if (mediaPendingCount + avatarPendingCount + stickerPendingCount >= maxConcurrentPending)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException(
                    $"You have reached the maximum of {maxConcurrentPending} concurrent pending uploads.");
            }

            var (usedBytes, initiatedBytes) = await QuotaAccountingQueries.GetDailyQuotaUsageAsync(
                _db,
                media.UploaderId,
                dailyQuotaWindowStartUtc,
                ct);

            if (media.DeclaredSizeBytes > dailyQuotaBytes - usedBytes)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException("Daily upload quota exceeded. Please try again later.");
            }

            var maxInitiatedBytes = dailyQuotaBytes <= long.MaxValue / 2
                ? dailyQuotaBytes * 2
                : long.MaxValue;

            if (media.DeclaredSizeBytes > maxInitiatedBytes - initiatedBytes)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException("Daily upload quota exceeded. Please try again later.");
            }

            _db.Media.Add(media);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        return media;
    }

    public async Task<Media?> GetByIdAsync(MediaId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return await _db.Media
            .FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<Media?> GetByIdNoTrackingAsync(MediaId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);

        return await _db.Media
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<AuthorizedMediaDownloadContext> GetAuthorizedDownloadContextAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        MediaId mediaId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(mediaId);

        var row = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new
            {
                IsParticipant = c.Participant1Id == callerId || c.Participant2Id == callerId,
                Media = _db.Media
                    .Where(m => m.Id == mediaId
                                && m.ConversationId == conversationId
                                && m.MessageId == messageId
                                && m.State == MediaState.Attached
                                && _db.Messages.Any(msg =>
                                    msg.ConversationId == conversationId
                                    && msg.Id == messageId
                                    && !msg.IsDeletedForEveryone
                                    && !_db.UserMessageDeletions.Any(d =>
                                        d.ConversationId == conversationId
                                        && d.MessageId == messageId
                                        && d.UserId == callerId)
                                    && !_db.UserConversationStates.Any(s =>
                                        s.ConversationId == conversationId
                                        && s.UserId == callerId
                                        && ((s.ClearedAtUtc != null && msg.CreatedAtUtc < s.ClearedAtUtc)
                                            || (s.DeletedAtUtc != null && msg.CreatedAtUtc < s.DeletedAtUtc)))))
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return new AuthorizedMediaDownloadContext(
                ConversationExists: false,
                IsParticipant: false,
                Media: null);
        }

        return new AuthorizedMediaDownloadContext(
            ConversationExists: true,
            IsParticipant: row.IsParticipant,
            Media: row.IsParticipant ? row.Media : null);
    }

    public async Task<IReadOnlyDictionary<MessageId, IReadOnlyList<Media>>> GetByMessageIdsAsync(
        ConversationId conversationId,
        IEnumerable<MessageId> messageIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageIds);

        var idList = messageIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return new Dictionary<MessageId, IReadOnlyList<Media>>();
        }

        var grouped = new Dictionary<MessageId, List<Media>>();
        foreach (var chunk in idList.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var items = await _db.Media
                .AsNoTracking()
                .Where(m => m.ConversationId == conversationId
                            && m.MessageId != null
                            && chunkList.Contains(m.MessageId!)
                            && m.State == MediaState.Attached)
                .OrderBy(m => m.DisplayOrder)
                .ToListAsync(ct);

            foreach (var item in items)
            {
                var msgId = item.MessageId!;
                if (!grouped.TryGetValue(msgId, out var list))
                {
                    list = [];
                    grouped[msgId] = list;
                }

                list.Add(item);
            }
        }

        var result = new Dictionary<MessageId, IReadOnlyList<Media>>(grouped.Count);
        foreach (var (key, list) in grouped)
        {
            result[key] = list;
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<(ConversationId ConversationId, MessageId MessageId), IReadOnlyList<Media>>> GetByMessageIdsAcrossConversationsAsync(
        IEnumerable<(ConversationId ConversationId, MessageId MessageId)> keys,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var keySet = new HashSet<(ConversationId ConversationId, MessageId MessageId)>();
        foreach (var key in keys)
        {
            ArgumentNullException.ThrowIfNull(key.ConversationId);
            ArgumentNullException.ThrowIfNull(key.MessageId);
            keySet.Add(key);
        }

        if (keySet.Count == 0)
        {
            return new Dictionary<(ConversationId ConversationId, MessageId MessageId), IReadOnlyList<Media>>();
        }

        var grouped = new Dictionary<(ConversationId, MessageId), List<Media>>();
        foreach (var keyChunk in keySet.Chunk(250))
        {
            var chunkSet = keyChunk.ToHashSet();
            var chunkConvIds = chunkSet.Select(k => k.ConversationId).Distinct().ToList();
            var chunkMsgIds = chunkSet.Select(k => k.MessageId).Distinct().ToList();

            var items = await _db.Media
                .AsNoTracking()
                .Where(m => chunkConvIds.Contains(m.ConversationId)
                            && m.MessageId != null
                            && chunkMsgIds.Contains(m.MessageId!)
                            && m.State == MediaState.Attached)
                .OrderBy(m => m.DisplayOrder)
                .ToListAsync(ct);

            foreach (var item in items)
            {
                var exactKey = (item.ConversationId, item.MessageId!);
                if (!chunkSet.Contains(exactKey))
                {
                    continue;
                }

                if (!grouped.TryGetValue(exactKey, out var list))
                {
                    list = [];
                    grouped[exactKey] = list;
                }

                list.Add(item);
            }
        }

        var result = new Dictionary<(ConversationId ConversationId, MessageId MessageId), IReadOnlyList<Media>>(grouped.Count);
        foreach (var (key, list) in grouped)
        {
            result[key] = list;
        }

        return result;
    }

    public async Task UpdateAsync(Media media, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(media);

        if (_db.Entry(media).State == EntityState.Detached)
        {
            _db.Media.Update(media);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _db.Entry(media).State = EntityState.Detached;
            throw new ConflictException(
                "The media item was modified concurrently. Please refresh and try again.",
                ex);
        }
    }

    public async Task<MarkVoicePlayedResult> TryMarkVoicePlayedAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        MediaId mediaId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(mediaId);

        var row = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Id == conversationId)
            .Select(c => new
            {
                IsParticipant = c.Participant1Id == callerId || c.Participant2Id == callerId,
                IsBlocked = _db.UserBlocks.Any(b =>
                    (b.BlockerId == c.Participant1Id && b.BlockedUserId == c.Participant2Id)
                    || (b.BlockerId == c.Participant2Id && b.BlockedUserId == c.Participant1Id)),
                MediaInfo = _db.Media
                    .Where(m => m.Id == mediaId
                                && m.ConversationId == conversationId
                                && m.MessageId == messageId
                                && m.State == MediaState.Attached
                                && _db.Messages.Any(msg =>
                                    msg.ConversationId == conversationId
                                    && msg.Id == messageId
                                    && !msg.IsDeletedForEveryone
                                    && !_db.UserMessageDeletions.Any(d =>
                                        d.ConversationId == conversationId
                                        && d.MessageId == messageId
                                        && d.UserId == callerId)
                                    && !_db.UserConversationStates.Any(s =>
                                        s.ConversationId == conversationId
                                        && s.UserId == callerId
                                        && ((s.ClearedAtUtc != null && msg.CreatedAtUtc < s.ClearedAtUtc)
                                            || (s.DeletedAtUtc != null && msg.CreatedAtUtc < s.DeletedAtUtc)))))
                    .Select(m => new
                    {
                        m.Kind,
                        m.UploaderId,
                        m.PlayedAtUtc
                    })
                    .FirstOrDefault()
            })
            .FirstOrDefaultAsync(ct);

        if (row is null)
        {
            return new MarkVoicePlayedResult(MarkVoicePlayedStatus.NotFound);
        }

        if (!row.IsParticipant)
        {
            return new MarkVoicePlayedResult(MarkVoicePlayedStatus.ForbiddenNotParticipant);
        }

        if (row.IsBlocked)
        {
            return new MarkVoicePlayedResult(MarkVoicePlayedStatus.ForbiddenBlocked);
        }

        if (row.MediaInfo is null)
        {
            return new MarkVoicePlayedResult(MarkVoicePlayedStatus.NotFound);
        }

        if (row.MediaInfo.Kind != MediaKind.Voice)
        {
            return new MarkVoicePlayedResult(MarkVoicePlayedStatus.InvalidNotVoiceMedia);
        }

        if (row.MediaInfo.UploaderId == callerId)
        {
            return new MarkVoicePlayedResult(MarkVoicePlayedStatus.ForbiddenOwnVoiceMessage);
        }

        if (row.MediaInfo.PlayedAtUtc.HasValue)
        {
            return new MarkVoicePlayedResult(
                MarkVoicePlayedStatus.AlreadyPlayed,
                row.MediaInfo.UploaderId,
                row.MediaInfo.PlayedAtUtc.Value);
        }

        var updatedRows = await _db.Media
            .Where(m => m.Id == mediaId
                        && m.ConversationId == conversationId
                        && m.MessageId == messageId
                        && m.Kind == MediaKind.Voice
                        && m.State == MediaState.Attached
                        && m.UploaderId != callerId
                        && m.PlayedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(m => m.PlayedAtUtc, (DateTimeOffset?)nowUtc), ct);

        if (updatedRows == 1)
        {
            return new MarkVoicePlayedResult(
                MarkVoicePlayedStatus.Marked,
                row.MediaInfo.UploaderId,
                nowUtc);
        }

        var reloaded = await _db.Media
            .AsNoTracking()
            .Where(m => m.Id == mediaId
                        && m.ConversationId == conversationId
                        && m.MessageId == messageId
                        && m.Kind == MediaKind.Voice
                        && m.State == MediaState.Attached)
            .Select(m => new { m.UploaderId, m.PlayedAtUtc })
            .FirstOrDefaultAsync(ct);

        if (reloaded?.PlayedAtUtc is not null)
        {
            return new MarkVoicePlayedResult(
                MarkVoicePlayedStatus.AlreadyPlayed,
                reloaded.UploaderId,
                reloaded.PlayedAtUtc.Value);
        }

        return new MarkVoicePlayedResult(MarkVoicePlayedStatus.NotFound);
    }
}
