using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Infrastructure.Storage;

public sealed record MediaCleanupResult(
    int TimedOutPendingCount,
    int ExpiredUploadedCount,
    int StorageObjectsPurgedCount,
    int OrphanObjectsDeletedCount,
    int MetadataRowsDeletedCount);

/// <summary>
/// Periodic background cleanup service executing SQL-first bulk state transitions and storage orphan sweeps.
/// Uses ExecuteUpdateAsync/ExecuteDeleteAsync across all steps to avoid RowVersion batch-abort conflicts.
/// </summary>
public sealed class MediaCleanupService : BackgroundService
{
    private const int BatchSize = 500;
    private const int MaxFailedDeletesPerPass = 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMediaStorage _mediaStorage;
    private readonly MediaOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MediaCleanupService> _logger;
    private DateTimeOffset? _lastPermanentOrphanSweepUtc;

    public MediaCleanupService(
        IServiceScopeFactory scopeFactory,
        IMediaStorage mediaStorage,
        IOptions<MediaOptions>? options = null,
        TimeProvider? timeProvider = null,
        ILogger<MediaCleanupService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(mediaStorage);

        _scopeFactory = scopeFactory;
        _mediaStorage = mediaStorage;
        _options = options?.Value ?? new MediaOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<MediaCleanupService>.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromMinutes(Math.Max(1, _options.CleanupIntervalMinutes));
        using var timer = new PeriodicTimer(period, _timeProvider);

        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var nowUtc = _timeProvider.GetUtcNow();
                    var sweepInterval = TimeSpan.FromHours(Math.Max(1, _options.PermanentOrphanSweepIntervalHours));
                    var includePermanentOrphanSweep = _lastPermanentOrphanSweepUtc is null
                        || nowUtc - _lastPermanentOrphanSweepUtc.Value >= sweepInterval;

                    await RunCleanupPassAsync(includePermanentOrphanSweep, stoppingToken);

                    if (includePermanentOrphanSweep)
                    {
                        _lastPermanentOrphanSweepUtc = nowUtc;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred during media cleanup pass.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful cancellation
        }
    }

    public Task<MediaCleanupResult> RunCleanupPassAsync(CancellationToken ct = default) =>
        RunCleanupPassAsync(includePermanentOrphanSweep: true, ct);

    public async Task<MediaCleanupResult> RunCleanupPassAsync(bool includePermanentOrphanSweep, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var nowUtc = _timeProvider.GetUtcNow();
        var pendingCutoff = nowUtc.AddMinutes(-Math.Max(1, _options.StalePendingMinutes));
        var uploadedCutoff = nowUtc.AddHours(-Math.Max(1, _options.UnattachedUploadedHours));
        var orphanCutoff = nowUtc.AddHours(-Math.Max(1, _options.OrphanGracePeriodHours));
        var retentionCutoff = nowUtc.AddHours(-Math.Max(1, _options.PurgedMetadataRetentionHours));

        // Step 1: Transition stale Pending uploads -> Failed atomically before deleting any staging objects.
        var timedOutPendingCount = await db.Media
            .Where(m => m.State == MediaState.Pending && m.CreatedAtUtc < pendingCutoff)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(m => m.State, MediaState.Failed)
                    .SetProperty(m => m.FailureReason, "Upload timed out before completion."),
                ct);

        timedOutPendingCount += await db.PendingAvatarUploads
            .Where(p => p.State == MediaState.Pending && p.CreatedAtUtc < pendingCutoff)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(p => p.State, MediaState.Failed)
                    .SetProperty(p => p.FailureReason, "Upload timed out before completion."),
                ct);

        timedOutPendingCount += await db.Stickers
            .Where(st => st.UploaderId != null && st.State == StickerState.Pending && st.CreatedAtUtc < pendingCutoff)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(st => st.State, StickerState.Failed)
                    .SetProperty(st => st.FailureReason, "Upload timed out before completion.")
                    .SetProperty(st => st.DeletedAtUtc, nowUtc),
                ct);

        // Step 2: Transition stale unattached Uploaded rows -> Deleted atomically before touching media/ objects.
        var expiredUploadedCount = await db.Media
            .Where(m => m.State == MediaState.Uploaded
                        && m.MessageId == null
                        && m.UploadedAtUtc != null
                        && m.UploadedAtUtc < uploadedCutoff)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(m => m.State, MediaState.Deleted)
                    .SetProperty(m => m.DeletedAtUtc, nowUtc),
                ct);

        // Step 2b: Atomically retire unreferenced Ready stickers belonging to soft-deleted custom packs
        // BEFORE deleting any R2 objects, so concurrent RepeatableRead message sends or favorite
        // additions can never attach to a sticker whose binary is being deleted.
        await db.Stickers
            .Where(s => s.UploaderId != null
                        && s.State == StickerState.Ready
                        && s.PurgedAtUtc == null
                        && db.StickerPacks.Any(p => p.Id == s.PackId && !p.IsSystemPack && p.IsDeleted)
                        && !db.Messages.Any(m => m.StickerId == s.Id && !m.IsDeletedForEveryone)
                        && !db.UserFavoriteStickers.Any(f => f.StickerId == s.Id)
                        && !db.UserRecentStickers.Any(r => r.StickerId == s.Id && r.UserId != s.UploaderId))
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(st => st.State, StickerState.Archived)
                    .SetProperty(st => st.DeletedAtUtc, st => st.DeletedAtUtc ?? nowUtc),
                ct);

        // Step 3: Delete storage objects for unpurged Failed/Deleted/Archived rows, then stamp PurgedAtUtc.
        var storagePurgedCount = await PurgeDeletedOrFailedStorageObjectsAsync(db, ct);
        storagePurgedCount += await PurgeDeletedOrFailedAvatarUploadsAsync(db, ct);
        storagePurgedCount += await PurgeFailedAndUnreferencedArchivedStickersAsync(db, ct);

        // Step 4: Sweep orphaned staging and permanent objects that have exceeded the grace period.
        var orphanDeletedCount = await SweepOrphanPrefixAsync(db, MediaConstraints.PendingKeyPrefix, orphanCutoff, ct);
        if (includePermanentOrphanSweep)
        {
            orphanDeletedCount += await SweepOrphanPrefixAsync(db, MediaConstraints.MediaKeyPrefix, orphanCutoff, ct);
            orphanDeletedCount += await SweepOrphanPrefixAsync(db, MediaConstraints.ThumbnailKeyPrefix, orphanCutoff, ct);
            orphanDeletedCount += await SweepOrphanPrefixAsync(db, MediaConstraints.AvatarKeyPrefix, orphanCutoff, ct);
            orphanDeletedCount += await SweepOrphanPrefixAsync(db, MediaConstraints.StickerKeyPrefix, orphanCutoff, ct);
        }

        // Step 5: Delete expired metadata rows already purged from storage.
        var metadataDeletedCount = await db.Media
            .Where(m => (m.State == MediaState.Failed || m.State == MediaState.Deleted)
                        && m.PurgedAtUtc != null
                        && m.PurgedAtUtc < retentionCutoff)
            .ExecuteDeleteAsync(ct);

        metadataDeletedCount += await db.PendingAvatarUploads
            .Where(p => ((p.State == MediaState.Failed || p.State == MediaState.Deleted)
                         && p.PurgedAtUtc != null
                         && p.PurgedAtUtc < retentionCutoff)
                        || (p.State == MediaState.Uploaded
                            && (p.CompletedAtUtc ?? p.CreatedAtUtc) < retentionCutoff))
            .ExecuteDeleteAsync(ct);

        var deletableExpiredStickerIds = db.Stickers
            .Where(st => st.UploaderId != null
                         && (st.State == StickerState.Failed || st.State == StickerState.Archived)
                         && st.PurgedAtUtc != null
                         && st.PurgedAtUtc < retentionCutoff
                         && !db.Messages.Any(m => m.StickerId == st.Id))
            .Select(st => st.Id);

        await db.UserFavoriteStickers
            .Where(f => deletableExpiredStickerIds.Contains(f.StickerId))
            .ExecuteDeleteAsync(ct);

        await db.UserRecentStickers
            .Where(r => deletableExpiredStickerIds.Contains(r.StickerId))
            .ExecuteDeleteAsync(ct);

        metadataDeletedCount += await db.Stickers
            .Where(st => st.UploaderId != null
                         && (st.State == StickerState.Failed || st.State == StickerState.Archived)
                         && st.PurgedAtUtc != null
                         && st.PurgedAtUtc < retentionCutoff
                         && !db.Messages.Any(m => m.StickerId == st.Id)
                         && !db.UserFavoriteStickers.Any(f => f.StickerId == st.Id)
                         && !db.UserRecentStickers.Any(r => r.StickerId == st.Id))
            .ExecuteDeleteAsync(ct);

        if (timedOutPendingCount > 0
            || expiredUploadedCount > 0
            || storagePurgedCount > 0
            || orphanDeletedCount > 0
            || metadataDeletedCount > 0)
        {
            _logger.LogInformation(
                "Media cleanup pass completed: {TimedOutPending} timed-out pending, {ExpiredUploaded} expired uploaded, {StoragePurged} storage purged, {OrphansDeleted} orphans deleted, {MetadataDeleted} metadata rows deleted.",
                timedOutPendingCount,
                expiredUploadedCount,
                storagePurgedCount,
                orphanDeletedCount,
                metadataDeletedCount);
        }

        return new MediaCleanupResult(
            timedOutPendingCount,
            expiredUploadedCount,
            storagePurgedCount,
            orphanDeletedCount,
            metadataDeletedCount);
    }

    private async Task<int> PurgeDeletedOrFailedStorageObjectsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var totalPurged = 0;
        var failedIds = new HashSet<MediaId>();

        while (!ct.IsCancellationRequested && failedIds.Count < MaxFailedDeletesPerPass)
        {
            var batch = await db.Media
                .AsNoTracking()
                .Where(m => (m.State == MediaState.Deleted || m.State == MediaState.Failed)
                            && m.PurgedAtUtc == null
                            && !failedIds.Contains(m.Id))
                .OrderBy(m => m.Id)
                .Take(BatchSize)
                .Select(m => new { m.Id, m.ObjectKey, m.ThumbnailObjectKey, m.State, m.VerifiedSizeBytes })
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            var deletedIds = new List<MediaId>(batch.Count);
            var measuredStagedSizes = new Dictionary<MediaId, long>();
            foreach (var item in batch)
            {
                try
                {
                    if (item.State == MediaState.Failed
                        && item.VerifiedSizeBytes is null
                        && item.ObjectKey.StartsWith(MediaConstraints.PendingKeyPrefix, StringComparison.Ordinal))
                    {
                        var stagedMeta = await _mediaStorage.GetObjectMetadataAsync(item.ObjectKey, ct);
                        if (stagedMeta is not null)
                        {
                            if (stagedMeta.ContentLength > 0)
                            {
                                measuredStagedSizes[item.Id] = stagedMeta.ContentLength;
                            }

                            await _mediaStorage.DeleteObjectAsync(item.ObjectKey, ct);
                        }
                    }
                    else
                    {
                        await _mediaStorage.DeleteObjectAsync(item.ObjectKey, ct);
                    }

                    if (!string.IsNullOrWhiteSpace(item.ThumbnailObjectKey))
                    {
                        await _mediaStorage.DeleteObjectAsync(item.ThumbnailObjectKey, ct);
                    }

                    deletedIds.Add(item.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedIds.Add(item.Id);
                    _logger.LogWarning(
                        ex,
                        "Failed to delete storage object for media {MediaId} during cleanup.",
                        item.Id.Value);
                }
            }

            if (deletedIds.Count > 0)
            {
                var purgeTime = _timeProvider.GetUtcNow();
                foreach (var (mediaId, stagedBytes) in measuredStagedSizes.Where(pair => deletedIds.Contains(pair.Key)))
                {
                    var updatedMeasured = await db.Media
                        .Where(m => m.Id == mediaId
                                    && m.State == MediaState.Failed
                                    && m.PurgedAtUtc == null)
                        .ExecuteUpdateAsync(
                            s => s
                                .SetProperty(m => m.VerifiedSizeBytes, m => m.VerifiedSizeBytes ?? stagedBytes)
                                .SetProperty(m => m.PurgedAtUtc, purgeTime),
                            ct);

                    totalPurged += updatedMeasured;
                }

                var remainingIds = measuredStagedSizes.Count == 0
                    ? deletedIds
                    : deletedIds.Where(id => !measuredStagedSizes.ContainsKey(id)).ToList();

                if (remainingIds.Count > 0)
                {
                    var updated = await db.Media
                        .Where(m => remainingIds.Contains(m.Id)
                                    && (m.State == MediaState.Deleted || m.State == MediaState.Failed)
                                    && m.PurgedAtUtc == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(m => m.PurgedAtUtc, purgeTime), ct);

                    totalPurged += updated;
                }
            }

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        return totalPurged;
    }

    private async Task<int> PurgeDeletedOrFailedAvatarUploadsAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var totalPurged = 0;
        var failedIds = new HashSet<AvatarUploadId>();

        while (!ct.IsCancellationRequested && failedIds.Count < MaxFailedDeletesPerPass)
        {
            var batch = await db.PendingAvatarUploads
                .AsNoTracking()
                .Where(p => (p.State == MediaState.Deleted || p.State == MediaState.Failed)
                            && p.PurgedAtUtc == null
                            && !failedIds.Contains(p.Id))
                .OrderBy(p => p.Id)
                .Take(BatchSize)
                .Select(p => new { p.Id, p.ObjectKey, p.State, p.VerifiedSizeBytes })
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            var deletedIds = new List<AvatarUploadId>(batch.Count);
            var measuredStagedSizes = new Dictionary<AvatarUploadId, long>();
            foreach (var item in batch)
            {
                try
                {
                    if (item.State == MediaState.Failed
                        && item.VerifiedSizeBytes is null
                        && item.ObjectKey.StartsWith(MediaConstraints.PendingKeyPrefix, StringComparison.Ordinal))
                    {
                        var stagedMeta = await _mediaStorage.GetObjectMetadataAsync(item.ObjectKey, ct);
                        if (stagedMeta is not null)
                        {
                            if (stagedMeta.ContentLength > 0)
                            {
                                measuredStagedSizes[item.Id] = stagedMeta.ContentLength;
                            }

                            await _mediaStorage.DeleteObjectAsync(item.ObjectKey, ct);
                        }
                    }
                    else
                    {
                        await _mediaStorage.DeleteObjectAsync(item.ObjectKey, ct);
                    }

                    deletedIds.Add(item.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedIds.Add(item.Id);
                    _logger.LogWarning(
                        ex,
                        "Failed to delete staging object for avatar upload {UploadId} during cleanup.",
                        item.Id.Value);
                }
            }

            if (deletedIds.Count > 0)
            {
                var purgeTime = _timeProvider.GetUtcNow();
                foreach (var (uploadId, stagedBytes) in measuredStagedSizes.Where(pair => deletedIds.Contains(pair.Key)))
                {
                    totalPurged += await db.PendingAvatarUploads
                        .Where(p => p.Id == uploadId && p.State == MediaState.Failed && p.PurgedAtUtc == null)
                        .ExecuteUpdateAsync(
                            s => s
                                .SetProperty(p => p.VerifiedSizeBytes, p => p.VerifiedSizeBytes ?? stagedBytes)
                                .SetProperty(p => p.PurgedAtUtc, purgeTime),
                            ct);
                }

                var remainingIds = measuredStagedSizes.Count == 0
                    ? deletedIds
                    : deletedIds.Where(id => !measuredStagedSizes.ContainsKey(id)).ToList();

                if (remainingIds.Count > 0)
                {
                    totalPurged += await db.PendingAvatarUploads
                        .Where(p => remainingIds.Contains(p.Id)
                                    && (p.State == MediaState.Deleted || p.State == MediaState.Failed)
                                    && p.PurgedAtUtc == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(p => p.PurgedAtUtc, purgeTime), ct);
                }
            }

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        return totalPurged;
    }

    private async Task<int> PurgeFailedAndUnreferencedArchivedStickersAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var totalPurged = 0;
        var failedIds = new HashSet<StickerId>();

        while (!ct.IsCancellationRequested && failedIds.Count < MaxFailedDeletesPerPass)
        {
            var batch = await db.Stickers
                .AsNoTracking()
                .Where(s => s.UploaderId != null
                            && s.ObjectKey != null
                            && s.PurgedAtUtc == null
                            && !failedIds.Contains(s.Id)
                            && (s.State == StickerState.Failed
                                || (s.State == StickerState.Archived
                                    && !db.Messages.Any(m => m.StickerId == s.Id && !m.IsDeletedForEveryone)
                                    && !db.UserFavoriteStickers.Any(f => f.StickerId == s.Id))))
                .OrderBy(s => s.Id)
                .Take(BatchSize)
                .Select(s => new { s.Id, s.ObjectKey, s.State, s.VerifiedSizeBytes })
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            var deletedIds = new List<StickerId>(batch.Count);
            var measuredStagedSizes = new Dictionary<StickerId, long>();
            foreach (var item in batch)
            {
                try
                {
                    var key = item.ObjectKey!;
                    if (item.State == StickerState.Failed
                        && item.VerifiedSizeBytes is null
                        && key.StartsWith(MediaConstraints.PendingKeyPrefix, StringComparison.Ordinal))
                    {
                        var stagedMeta = await _mediaStorage.GetObjectMetadataAsync(key, ct);
                        if (stagedMeta is not null)
                        {
                            if (stagedMeta.ContentLength > 0)
                            {
                                measuredStagedSizes[item.Id] = stagedMeta.ContentLength;
                            }

                            await _mediaStorage.DeleteObjectAsync(key, ct);
                        }
                    }
                    else
                    {
                        await _mediaStorage.DeleteObjectAsync(key, ct);
                    }

                    deletedIds.Add(item.Id);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failedIds.Add(item.Id);
                    _logger.LogWarning(
                        ex,
                        "Failed to delete storage object for sticker {StickerId} during cleanup.",
                        item.Id.Value);
                }
            }

            if (deletedIds.Count > 0)
            {
                var purgeTime = _timeProvider.GetUtcNow();
                foreach (var (stickerId, stagedBytes) in measuredStagedSizes.Where(pair => deletedIds.Contains(pair.Key)))
                {
                    totalPurged += await db.Stickers
                        .Where(s => s.Id == stickerId && s.State == StickerState.Failed && s.PurgedAtUtc == null)
                        .ExecuteUpdateAsync(
                            s => s
                                .SetProperty(st => st.VerifiedSizeBytes, st => st.VerifiedSizeBytes ?? stagedBytes)
                                .SetProperty(st => st.PurgedAtUtc, purgeTime),
                            ct);
                }

                var remainingIds = measuredStagedSizes.Count == 0
                    ? deletedIds
                    : deletedIds.Where(id => !measuredStagedSizes.ContainsKey(id)).ToList();

                if (remainingIds.Count > 0)
                {
                    totalPurged += await db.Stickers
                        .Where(s => remainingIds.Contains(s.Id)
                                    && (s.State == StickerState.Failed || s.State == StickerState.Archived)
                                    && s.PurgedAtUtc == null)
                        .ExecuteUpdateAsync(
                            s => s
                                .SetProperty(st => st.DeletedAtUtc, st => st.DeletedAtUtc ?? purgeTime)
                                .SetProperty(st => st.PurgedAtUtc, purgeTime),
                            ct);

                    await db.UserRecentStickers
                        .Where(r => remainingIds.Contains(r.StickerId)
                                    && db.Stickers.Any(s => s.Id == r.StickerId && s.PurgedAtUtc != null))
                        .ExecuteDeleteAsync(ct);
                }
            }

            if (batch.Count < BatchSize)
            {
                break;
            }
        }

        return totalPurged;
    }

    private async Task<int> SweepOrphanPrefixAsync(
        ApplicationDbContext db,
        string prefix,
        DateTimeOffset orphanCutoff,
        CancellationToken ct)
    {
        var deletedCount = 0;
        var scannedCount = 0;
        var maxScanPerPrefix = Math.Max(1, _options.MaxOrphanSweepObjectsPerPrefixPerPass);
        var chunk = new List<StoredObjectInfo>(BatchSize);

        await foreach (var item in _mediaStorage.ListObjectsAsync(prefix, ct))
        {
            scannedCount++;
            if (scannedCount > maxScanPerPrefix)
            {
                _logger.LogInformation(
                    "Reached per-pass orphan scan cap ({MaxObjects}) for prefix {Prefix}; remaining objects deferred to next sweep.",
                    maxScanPerPrefix,
                    prefix);
                break;
            }

            if (item.LastModifiedUtc >= orphanCutoff)
            {
                continue;
            }

            chunk.Add(item);
            if (chunk.Count >= BatchSize)
            {
                deletedCount += await DeleteUnreferencedChunkAsync(db, prefix, chunk, ct);
                chunk.Clear();
            }
        }

        if (chunk.Count > 0)
        {
            deletedCount += await DeleteUnreferencedChunkAsync(db, prefix, chunk, ct);
        }

        return deletedCount;
    }

    private async Task<int> DeleteUnreferencedChunkAsync(
        ApplicationDbContext db,
        string prefix,
        List<StoredObjectInfo> chunk,
        CancellationToken ct)
    {
        var chunkKeys = chunk.Select(c => c.ObjectKey).Distinct(StringComparer.Ordinal).ToList();
        var activeSet = new HashSet<string>(StringComparer.Ordinal);

        if (string.Equals(prefix, MediaConstraints.PendingKeyPrefix, StringComparison.Ordinal))
        {
            var activeMedia = await db.Media
                .AsNoTracking()
                .Where(m => chunkKeys.Contains(m.ObjectKey) && m.PurgedAtUtc == null)
                .Select(m => m.ObjectKey)
                .ToListAsync(ct);
            var activeAvatars = await db.PendingAvatarUploads
                .AsNoTracking()
                .Where(p => chunkKeys.Contains(p.ObjectKey) && p.PurgedAtUtc == null && p.State != MediaState.Uploaded)
                .Select(p => p.ObjectKey)
                .ToListAsync(ct);
            var activeStickers = await db.Stickers
                .AsNoTracking()
                .Where(s => s.ObjectKey != null && chunkKeys.Contains(s.ObjectKey) && s.PurgedAtUtc == null)
                .Select(s => s.ObjectKey!)
                .ToListAsync(ct);

            foreach (var k in activeMedia) activeSet.Add(k);
            foreach (var k in activeAvatars) activeSet.Add(k);
            foreach (var k in activeStickers) activeSet.Add(k);
        }
        else if (string.Equals(prefix, MediaConstraints.MediaKeyPrefix, StringComparison.Ordinal))
        {
            var activeMedia = await db.Media
                .AsNoTracking()
                .Where(m => chunkKeys.Contains(m.ObjectKey) && m.PurgedAtUtc == null)
                .Select(m => m.ObjectKey)
                .ToListAsync(ct);
            foreach (var k in activeMedia) activeSet.Add(k);
        }
        else if (string.Equals(prefix, MediaConstraints.ThumbnailKeyPrefix, StringComparison.Ordinal))
        {
            var activeThumbs = await db.Media
                .AsNoTracking()
                .Where(m => m.ThumbnailObjectKey != null && chunkKeys.Contains(m.ThumbnailObjectKey) && m.PurgedAtUtc == null)
                .Select(m => m.ThumbnailObjectKey!)
                .ToListAsync(ct);
            foreach (var k in activeThumbs) activeSet.Add(k);
        }
        else if (string.Equals(prefix, MediaConstraints.AvatarKeyPrefix, StringComparison.Ordinal))
        {
            // Intentionally query ALL users including soft-deleted accounts within the 30-day recovery window.
            var activePairs = await db.Users
                .AsNoTracking()
                .Where(u => (u.AvatarObjectKey != null && chunkKeys.Contains(u.AvatarObjectKey))
                            || (u.AvatarThumbnailObjectKey != null && chunkKeys.Contains(u.AvatarThumbnailObjectKey)))
                .Select(u => new { u.AvatarObjectKey, u.AvatarThumbnailObjectKey })
                .ToListAsync(ct);

            foreach (var pair in activePairs)
            {
                if (!string.IsNullOrEmpty(pair.AvatarObjectKey)) activeSet.Add(pair.AvatarObjectKey);
                if (!string.IsNullOrEmpty(pair.AvatarThumbnailObjectKey)) activeSet.Add(pair.AvatarThumbnailObjectKey);
            }
        }
        else if (string.Equals(prefix, MediaConstraints.StickerKeyPrefix, StringComparison.Ordinal))
        {
            var activeStickers = await db.Stickers
                .AsNoTracking()
                .Where(s => s.ObjectKey != null && chunkKeys.Contains(s.ObjectKey) && s.PurgedAtUtc == null)
                .Select(s => s.ObjectKey!)
                .ToListAsync(ct);

            foreach (var k in activeStickers) activeSet.Add(k);
        }

        var deletedCount = 0;

        foreach (var item in chunk)
        {
            if (activeSet.Contains(item.ObjectKey))
            {
                continue;
            }

            try
            {
                await _mediaStorage.DeleteObjectAsync(item.ObjectKey, ct);
                deletedCount++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete orphaned storage object during cleanup.");
            }
        }

        return deletedCount;
    }
}
