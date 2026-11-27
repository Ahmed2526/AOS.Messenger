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

            var (totalUsedBytes, totalInitiatedBytes) = await QuotaAccountingQueries.GetDailyQuotaUsageAsync(
                _db,
                upload.UserId,
                dailyQuotaWindowStartUtc,
                ct);

            if (upload.DeclaredSizeBytes > dailyQuotaBytes - totalUsedBytes)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException("Daily upload quota exceeded. Please try again later.");
            }

            var maxInitiatedBytes = dailyQuotaBytes <= long.MaxValue / 2
                ? dailyQuotaBytes * 2
                : long.MaxValue;

            if (upload.DeclaredSizeBytes > maxInitiatedBytes - totalInitiatedBytes)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException("Daily upload quota exceeded. Please try again later.");
            }

            var latestAvatarUpdatedAtUtc = await _db.Users
                .AsNoTracking()
                .Where(u => u.Id == upload.UserId.Value)
                .Select(u => u.AvatarUpdatedAtUtc)
                .FirstOrDefaultAsync(ct);

            if (latestAvatarUpdatedAtUtc.HasValue)
            {
                upload.EnsureCreatedAfter(latestAvatarUpdatedAtUtc.Value);
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
        string? firstAttemptPrevFull = null;
        string? firstAttemptPrevThumb = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        var commitAttempted = false;
        try
        {
            await strategy.ExecuteAsync(CommitAsync);
        }
        catch (Exception ex) when (ex is DbUpdateException or SqlException)
        {
            if (commitAttempted)
            {
                try
                {
                    _db.ChangeTracker.Clear();
                    var trackedUpload = await _db.PendingAvatarUploads
                        .AsNoTracking()
                        .FirstOrDefaultAsync(p => p.Id == upload.Id, CancellationToken.None);
                    var committedUser = await _db.Users
                        .AsNoTracking()
                        .FirstOrDefaultAsync(u => u.Id == upload.UserId.Value, CancellationToken.None);

                    if (trackedUpload?.State == MediaState.Uploaded
                        && committedUser is not null
                        && !committedUser.IsDeleted
                        && committedUser.EmailConfirmed)
                    {
                        var appliedByThisUpload =
                            string.Equals(committedUser.AvatarObjectKey, fullObjectKey, StringComparison.Ordinal)
                            && string.Equals(committedUser.AvatarThumbnailObjectKey, thumbObjectKey, StringComparison.Ordinal);

                        return new AvatarCommitResult(
                            Applied: appliedByThisUpload,
                            DisplayName: committedUser.DisplayName,
                            CurrentAvatarVersion: committedUser.AvatarVersion,
                            PreviousFullObjectKey: appliedByThisUpload ? firstAttemptPrevFull : null,
                            PreviousThumbObjectKey: appliedByThisUpload ? firstAttemptPrevThumb : null);
                    }
                }
                catch (Exception recoveryEx) when (recoveryEx is DbUpdateException or SqlException)
                {
                    // Fall through to ConflictException without leaking raw SQL exceptions.
                }
            }

            throw new ConflictException("The avatar commit could not be confirmed due to a concurrent update or database error. Please try again.", ex);
        }

        return result!;

        async Task CommitAsync()
        {
            _db.ChangeTracker.Clear();

            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var trackedUpload = await _db.PendingAvatarUploads
                .FirstOrDefaultAsync(p => p.Id == upload.Id, ct);

            if (trackedUpload is null)
            {
                throw new NotFoundException("AvatarUpload", upload.Id.Value);
            }

            if (trackedUpload.State == MediaState.Uploaded && commitAttempted)
            {
                var committedUser = await _db.Users
                    .AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == trackedUpload.UserId.Value, ct);

                if (committedUser is null || committedUser.IsDeleted || !committedUser.EmailConfirmed)
                {
                    throw new NotFoundException("User", trackedUpload.UserId.Value);
                }

                var appliedByThisUpload =
                    string.Equals(committedUser.AvatarObjectKey, fullObjectKey, StringComparison.Ordinal)
                    && string.Equals(committedUser.AvatarThumbnailObjectKey, thumbObjectKey, StringComparison.Ordinal);

                await tx.CommitAsync(ct);

                result = new AvatarCommitResult(
                    Applied: appliedByThisUpload,
                    DisplayName: committedUser.DisplayName,
                    CurrentAvatarVersion: committedUser.AvatarVersion,
                    PreviousFullObjectKey: appliedByThisUpload ? firstAttemptPrevFull : null,
                    PreviousThumbObjectKey: appliedByThisUpload ? firstAttemptPrevThumb : null);
                return;
            }

            if (trackedUpload.State == MediaState.Failed)
            {
                throw new DomainException(trackedUpload.FailureReason ?? "Profile photo verification failed.");
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

                commitAttempted = true;
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
            firstAttemptPrevFull ??= prevFull;
            firstAttemptPrevThumb ??= prevThumb;

            user.AvatarObjectKey = fullObjectKey;
            user.AvatarThumbnailObjectKey = thumbObjectKey;
            user.AvatarContentType = contentType;
            user.AvatarSizeBytes = fullSizeBytes;
            user.AvatarThumbnailSizeBytes = thumbSizeBytes;
            user.AvatarVersion = avatarVersion;
            user.AvatarUpdatedAtUtc = nowUtc >= trackedUpload.CreatedAtUtc ? nowUtc : trackedUpload.CreatedAtUtc;
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

            commitAttempted = true;
            await tx.CommitAsync(ct);

            result = new AvatarCommitResult(
                Applied: true,
                DisplayName: user.DisplayName,
                CurrentAvatarVersion: user.AvatarVersion,
                PreviousFullObjectKey: prevFull,
                PreviousThumbObjectKey: prevThumb);
        }
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
            user.AvatarUpdatedAtUtc = user.AvatarUpdatedAtUtc.HasValue && user.AvatarUpdatedAtUtc.Value > nowUtc
                ? user.AvatarUpdatedAtUtc.Value.AddTicks(1)
                : nowUtc;
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
}
