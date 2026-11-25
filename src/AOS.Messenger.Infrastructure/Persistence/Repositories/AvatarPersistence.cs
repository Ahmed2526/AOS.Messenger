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
/// EF Core persistence implementation for staged avatar uploads, shared daily upload quotas,
/// atomic dual-variant profile photo commits/removals, and unblocked peer lookup.
/// </summary>
public sealed class AvatarPersistence : IAvatarPersistence
{
    private readonly ApplicationDbContext _db;

    public AvatarPersistence(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<PendingAvatarUpload> TryCreatePendingWithQuotaCheckAsync(
        PendingAvatarUpload upload,
        int maxConcurrentPending,
        long dailyQuotaBytes,
        DateTimeOffset dailyQuotaWindowStartUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
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
                Value = $"media-quota:{upload.UserId.Value:N}"
            };

            await _db.Database.ExecuteSqlRawAsync(
                "EXEC @rc = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000",
                new object[] { rcParam, resParam },
                ct);

            if (rcParam.Value is not int lockResult || lockResult < 0)
            {
                await tx.RollbackAsync(ct);
                throw new ConflictException("Too many concurrent upload requests. Please try again.");
            }

            var mediaPendingCount = await _db.Media
                .CountAsync(m => m.UploaderId == upload.UserId && m.State == MediaState.Pending, ct);
            var avatarPendingCount = await _db.PendingAvatarUploads
                .CountAsync(p => p.UserId == upload.UserId && p.State == MediaState.Pending, ct);
            var stickerPendingCount = await _db.Stickers
                .CountAsync(s => s.UploaderId == upload.UserId && s.State == StickerState.Pending, ct);

            if (mediaPendingCount + avatarPendingCount + stickerPendingCount >= maxConcurrentPending)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException(
                    $"You have reached the maximum of {maxConcurrentPending} concurrent pending uploads.");
            }

            var mediaUsedBytes = await _db.Media
                .Where(m => m.UploaderId == upload.UserId && m.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(m => m.State == MediaState.Failed
                    ? (long?)(m.VerifiedSizeBytes ?? 0L)
                    : (long?)(m.VerifiedSizeBytes ?? m.DeclaredSizeBytes), ct) ?? 0L;

            var avatarUsedBytes = await _db.PendingAvatarUploads
                .Where(p => p.UserId == upload.UserId && p.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(p => p.State == MediaState.Failed
                    ? (long?)(p.VerifiedSizeBytes ?? 0L)
                    : (long?)(p.VerifiedSizeBytes ?? p.DeclaredSizeBytes), ct) ?? 0L;

            var stickerUsedBytes = await _db.Stickers
                .Where(s => s.UploaderId == upload.UserId && s.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(s => s.State == StickerState.Failed
                    ? (long?)(s.VerifiedSizeBytes ?? 0L)
                    : (long?)(s.VerifiedSizeBytes ?? s.DeclaredSizeBytes), ct) ?? 0L;

            var totalUsedBytes = mediaUsedBytes + avatarUsedBytes + stickerUsedBytes;
            if (upload.DeclaredSizeBytes > dailyQuotaBytes - totalUsedBytes)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException("Daily upload quota exceeded. Please try again later.");
            }

            var mediaInitiatedBytes = await _db.Media
                .Where(m => m.UploaderId == upload.UserId && m.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(m => (long?)m.DeclaredSizeBytes, ct) ?? 0L;

            var avatarInitiatedBytes = await _db.PendingAvatarUploads
                .Where(p => p.UserId == upload.UserId && p.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(p => (long?)p.DeclaredSizeBytes, ct) ?? 0L;

            var stickerInitiatedBytes = await _db.Stickers
                .Where(s => s.UploaderId == upload.UserId && s.CreatedAtUtc >= dailyQuotaWindowStartUtc)
                .SumAsync(s => (long?)s.DeclaredSizeBytes, ct) ?? 0L;

            var maxInitiatedBytes = dailyQuotaBytes <= long.MaxValue / 2
                ? dailyQuotaBytes * 2
                : long.MaxValue;

            if (upload.DeclaredSizeBytes > maxInitiatedBytes - (mediaInitiatedBytes + avatarInitiatedBytes + stickerInitiatedBytes))
            {
                await tx.RollbackAsync(ct);
                throw new DomainException("Daily upload quota exceeded. Please try again later.");
            }

            _db.PendingAvatarUploads.Add(upload);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        return upload;
    }

    public Task<PendingAvatarUpload?> GetPendingByIdAsync(AvatarUploadId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _db.PendingAvatarUploads.FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public Task<PendingAvatarUpload?> GetPendingByIdNoTrackingAsync(AvatarUploadId id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(id);
        return _db.PendingAvatarUploads.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task UpdatePendingAsync(PendingAvatarUpload upload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        if (_db.Entry(upload).State == EntityState.Detached)
        {
            _db.PendingAvatarUploads.Update(upload);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _db.Entry(upload).State = EntityState.Detached;
            throw new ConflictException(
                "The avatar upload was modified concurrently. Please try again.",
                ex);
        }
    }

    public async Task<AvatarCommitResult> CommitUserAvatarAsync(
        PendingAvatarUpload upload,
        string fullObjectKey,
        string thumbObjectKey,
        string contentType,
        long fullSizeBytes,
        long thumbSizeBytes,
        string avatarVersion,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);

        AvatarCommitResult? result = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var trackedUpload = await _db.PendingAvatarUploads
                .FirstOrDefaultAsync(p => p.Id == upload.Id, ct);

            if (trackedUpload is null)
            {
                throw new NotFoundException("AvatarUpload", upload.Id.Value);
            }

            if (trackedUpload.State != MediaState.Pending)
            {
                throw new ConflictException($"Avatar upload cannot be completed from state '{trackedUpload.State}'.");
            }

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Id == trackedUpload.UserId.Value, ct);

            if (user is null || user.IsDeleted || !user.EmailConfirmed)
            {
                throw new NotFoundException("User", trackedUpload.UserId.Value);
            }

            trackedUpload.MarkCompleted(upload.VerifiedSizeBytes ?? trackedUpload.DeclaredSizeBytes, nowUtc);

            // Protect against newer upload or explicit RemoveAvatar completing while Magick.NET was processing
            if (user.AvatarUpdatedAtUtc.HasValue && user.AvatarUpdatedAtUtc.Value >= trackedUpload.CreatedAtUtc)
            {
                try
                {
                    await _db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    throw new ConflictException(
                        "The avatar upload was modified concurrently. Please try again.",
                        ex);
                }

                await tx.CommitAsync(ct);

                result = new AvatarCommitResult(
                    Applied: false,
                    DisplayName: user.DisplayName,
                    CurrentAvatarVersion: user.AvatarVersion,
                    PreviousFullObjectKey: null,
                    PreviousThumbObjectKey: null);
                return;
            }

            var prevFull = user.AvatarObjectKey;
            var prevThumb = user.AvatarThumbnailObjectKey;

            user.AvatarObjectKey = fullObjectKey;
            user.AvatarThumbnailObjectKey = thumbObjectKey;
            user.AvatarContentType = contentType;
            user.AvatarSizeBytes = fullSizeBytes;
            user.AvatarThumbnailSizeBytes = thumbSizeBytes;
            user.AvatarVersion = avatarVersion;
            user.AvatarUpdatedAtUtc = nowUtc;
            user.ConcurrencyStamp = Guid.NewGuid().ToString();

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                throw new ConflictException(
                    "Profile photo was updated concurrently. Please try again.",
                    ex);
            }

            await tx.CommitAsync(ct);

            result = new AvatarCommitResult(
                Applied: true,
                DisplayName: user.DisplayName,
                CurrentAvatarVersion: user.AvatarVersion,
                PreviousFullObjectKey: prevFull,
                PreviousThumbObjectKey: prevThumb);
        });

        return result!;
    }

    public async Task<AvatarRemoveResult> RemoveUserAvatarAsync(
        UserId userId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        AvatarRemoveResult? result = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();

            var user = await _db.Users
                .FirstOrDefaultAsync(u => u.Id == userId.Value, ct);

            if (user is null || user.IsDeleted || !user.EmailConfirmed)
            {
                throw new NotFoundException("User", userId.Value);
            }

            var prevFull = user.AvatarObjectKey;
            var prevThumb = user.AvatarThumbnailObjectKey;
            var hadAvatar = prevFull is not null || prevThumb is not null || user.AvatarVersion is not null;

            user.AvatarObjectKey = null;
            user.AvatarThumbnailObjectKey = null;
            user.AvatarContentType = null;
            user.AvatarSizeBytes = null;
            user.AvatarThumbnailSizeBytes = null;
            user.AvatarVersion = null;
            user.AvatarUpdatedAtUtc = nowUtc;
            user.ConcurrencyStamp = Guid.NewGuid().ToString();

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                throw new ConflictException(
                    "Profile photo was modified concurrently. Please try again.",
                    ex);
            }

            result = new AvatarRemoveResult(hadAvatar, user.DisplayName, prevFull, prevThumb);
        });

        return result!;
    }

    public async Task<UserAvatarMetadata?> GetUserAvatarMetadataAsync(UserId targetUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targetUserId);

        return await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == targetUserId.Value)
            .Select(u => new UserAvatarMetadata(
                u.IsDeleted,
                u.EmailConfirmed,
                u.AvatarObjectKey,
                u.AvatarThumbnailObjectKey,
                u.AvatarContentType,
                u.AvatarVersion))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<UserId>> GetNotifyPeerUserIdsAsync(UserId userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var peerIds = await _db.Conversations
            .AsNoTracking()
            .Where(c => c.Participant1Id == userId || c.Participant2Id == userId)
            .Select(c => c.Participant1Id == userId ? c.Participant2Id : c.Participant1Id)
            .Distinct()
            .ToListAsync(ct);

        if (peerIds.Count == 0)
        {
            return [];
        }

        var blockedIds = await _db.UserBlocks
            .AsNoTracking()
            .Where(b => b.BlockerId == userId || b.BlockedUserId == userId)
            .Select(b => b.BlockerId == userId ? b.BlockedUserId : b.BlockerId)
            .Distinct()
            .ToListAsync(ct);

        var blockedSet = blockedIds.ToHashSet();
        return peerIds.Where(id => !blockedSet.Contains(id)).ToList();
    }
}
