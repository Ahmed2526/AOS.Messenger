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
                    await RunCleanupPassAsync(stoppingToken);
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

    public async Task<MediaCleanupResult> RunCleanupPassAsync(CancellationToken ct = default)
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

        // Step 3: Delete storage objects for unpurged Failed/Deleted rows, then stamp PurgedAtUtc.
        var storagePurgedCount = await PurgeDeletedOrFailedStorageObjectsAsync(db, ct);

        // Step 4: Sweep orphaned staging and permanent objects that have exceeded the grace period.
        var orphanDeletedCount = await SweepOrphanPrefixAsync(db, MediaConstraints.PendingKeyPrefix, orphanCutoff, ct);
        orphanDeletedCount += await SweepOrphanPrefixAsync(db, MediaConstraints.MediaKeyPrefix, orphanCutoff, ct);

        // Step 5: Delete expired metadata rows already purged from storage.
        var metadataDeletedCount = await db.Media
            .Where(m => (m.State == MediaState.Failed || m.State == MediaState.Deleted)
                        && m.PurgedAtUtc != null
                        && m.PurgedAtUtc < retentionCutoff)
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
                .Select(m => new { m.Id, m.ObjectKey, m.State, m.VerifiedSizeBytes })
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
                foreach (var (mediaId, stagedBytes) in measuredStagedSizes)
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

    private async Task<int> SweepOrphanPrefixAsync(
        ApplicationDbContext db,
        string prefix,
        DateTimeOffset orphanCutoff,
        CancellationToken ct)
    {
        var deletedCount = 0;
        var chunk = new List<StoredObjectInfo>(BatchSize);

        await foreach (var item in _mediaStorage.ListObjectsAsync(prefix, ct))
        {
            if (item.LastModifiedUtc >= orphanCutoff)
            {
                continue;
            }

            chunk.Add(item);
            if (chunk.Count >= BatchSize)
            {
                deletedCount += await DeleteUnreferencedChunkAsync(db, chunk, ct);
                chunk.Clear();
            }
        }

        if (chunk.Count > 0)
        {
            deletedCount += await DeleteUnreferencedChunkAsync(db, chunk, ct);
        }

        return deletedCount;
    }

    private async Task<int> DeleteUnreferencedChunkAsync(
        ApplicationDbContext db,
        List<StoredObjectInfo> chunk,
        CancellationToken ct)
    {
        var chunkKeys = chunk.Select(c => c.ObjectKey).Distinct(StringComparer.Ordinal).ToList();
        var activeKeys = await db.Media
            .AsNoTracking()
            .Where(m => chunkKeys.Contains(m.ObjectKey) && m.PurgedAtUtc == null)
            .Select(m => m.ObjectKey)
            .ToListAsync(ct);

        var activeSet = activeKeys.ToHashSet(StringComparer.Ordinal);
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
