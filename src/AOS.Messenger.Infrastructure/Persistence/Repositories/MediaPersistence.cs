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

            var mediaUsedBytes = await _db.Media
                .Where(m => m.UploaderId == media.UploaderId && m.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(m => m.State == MediaState.Failed
                    ? (long?)(m.VerifiedSizeBytes ?? 0L)
                    : (long?)(m.VerifiedSizeBytes ?? m.DeclaredSizeBytes), ct) ?? 0L;

            var avatarUsedBytes = await _db.PendingAvatarUploads
                .Where(p => p.UserId == media.UploaderId && p.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(p => p.State == MediaState.Failed
                    ? (long?)(p.VerifiedSizeBytes ?? 0L)
                    : (long?)(p.VerifiedSizeBytes ?? p.DeclaredSizeBytes), ct) ?? 0L;

            var stickerUsedBytes = await _db.Stickers
                .Where(s => s.UploaderId == media.UploaderId && s.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(s => s.State == StickerState.Failed
                    ? (long?)(s.VerifiedSizeBytes ?? 0L)
                    : s.State == StickerState.Ready || s.State == StickerState.Archived
                        ? (long?)s.DeclaredSizeBytes
                        : (long?)(s.VerifiedSizeBytes ?? s.DeclaredSizeBytes), ct) ?? 0L;

            var usedBytes = mediaUsedBytes + avatarUsedBytes + stickerUsedBytes;

            if (media.DeclaredSizeBytes > dailyQuotaBytes - usedBytes)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException("Daily upload quota exceeded. Please try again later.");
            }

            var mediaInitiatedBytes = await _db.Media
                .Where(m => m.UploaderId == media.UploaderId && m.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(m => (long?)m.DeclaredSizeBytes, ct) ?? 0L;

            var avatarInitiatedBytes = await _db.PendingAvatarUploads
                .Where(p => p.UserId == media.UploaderId && p.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(p => (long?)p.DeclaredSizeBytes, ct) ?? 0L;

            var stickerInitiatedBytes = await _db.Stickers
                .Where(s => s.UploaderId == media.UploaderId && s.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(s => (long?)s.DeclaredSizeBytes, ct) ?? 0L;

            var initiatedBytes = mediaInitiatedBytes + avatarInitiatedBytes + stickerInitiatedBytes;

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
        var convIds = new HashSet<ConversationId>();
        var msgIds = new HashSet<MessageId>();

        foreach (var key in keys)
        {
            ArgumentNullException.ThrowIfNull(key.ConversationId);
            ArgumentNullException.ThrowIfNull(key.MessageId);

            if (keySet.Add(key))
            {
                convIds.Add(key.ConversationId);
                msgIds.Add(key.MessageId);
            }
        }

        if (keySet.Count == 0)
        {
            return new Dictionary<(ConversationId ConversationId, MessageId MessageId), IReadOnlyList<Media>>();
        }

        var grouped = new Dictionary<(ConversationId, MessageId), List<Media>>();
        foreach (var convChunk in convIds.Chunk(200))
        {
            var convChunkList = convChunk.ToList();
            foreach (var msgChunk in msgIds.Chunk(500))
            {
                var msgChunkList = msgChunk.ToList();
                var items = await _db.Media
                    .AsNoTracking()
                    .Where(m => convChunkList.Contains(m.ConversationId)
                                && m.MessageId != null
                                && msgChunkList.Contains(m.MessageId!)
                                && m.State == MediaState.Attached)
                    .OrderBy(m => m.DisplayOrder)
                    .ToListAsync(ct);

                foreach (var item in items)
                {
                    var exactKey = (item.ConversationId, item.MessageId!);
                    if (!keySet.Contains(exactKey))
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
}
