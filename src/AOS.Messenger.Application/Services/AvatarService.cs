using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Application.Services;

/// <summary>
/// Orchestrates presigned avatar uploads, dual-variant Magick.NET processing,
/// atomic replacement/removal, strict block/soft-delete authorization, and live profile sync.
/// </summary>
public sealed class AvatarService : IAvatarService
{
    private readonly IAvatarPersistence _avatarPersistence;
    private readonly IMediaStorage _mediaStorage;
    private readonly IAvatarProcessor _avatarProcessor;
    private readonly IUserReader _userReader;
    private readonly IBlockPersistence _blockPersistence;
    private readonly IRealtimeNotifier _realtimeNotifier;
    private readonly ILogger<AvatarService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly MediaOptions _options;

    public AvatarService(
        IAvatarPersistence avatarPersistence,
        IMediaStorage mediaStorage,
        IAvatarProcessor avatarProcessor,
        IUserReader userReader,
        IBlockPersistence blockPersistence,
        IRealtimeNotifier realtimeNotifier,
        ILogger<AvatarService> logger,
        IOptions<MediaOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(avatarPersistence);
        ArgumentNullException.ThrowIfNull(mediaStorage);
        ArgumentNullException.ThrowIfNull(avatarProcessor);
        ArgumentNullException.ThrowIfNull(userReader);
        ArgumentNullException.ThrowIfNull(blockPersistence);
        ArgumentNullException.ThrowIfNull(realtimeNotifier);
        ArgumentNullException.ThrowIfNull(logger);

        _avatarPersistence = avatarPersistence;
        _mediaStorage = mediaStorage;
        _avatarProcessor = avatarProcessor;
        _userReader = userReader;
        _blockPersistence = blockPersistence;
        _realtimeNotifier = realtimeNotifier;
        _logger = logger;
        _options = options?.Value ?? new MediaOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<InitiateAvatarUploadResponseDto> InitiateUploadAsync(
        UserId callerId,
        InitiateAvatarUploadRequestDto dto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(dto);

        if (!await _userReader.ExistsAsync(callerId, ct))
        {
            throw new NotFoundException("User", callerId.Value);
        }

        var canonicalMime = AvatarConstraints.ResolveAllowedAvatarUpload(dto.FileName, dto.ContentType);

        if (dto.SizeBytes <= 0 || dto.SizeBytes > AvatarConstraints.MaxRawAvatarUploadBytes)
        {
            throw new DomainException(
                $"Profile photo size must be between 1 byte and {AvatarConstraints.MaxRawAvatarUploadBytes} bytes.");
        }

        var nowUtc = _timeProvider.GetUtcNow();
        var pending = PendingAvatarUpload.CreatePending(callerId, canonicalMime, dto.SizeBytes, nowUtc);

        var created = await _avatarPersistence.TryCreatePendingWithQuotaCheckAsync(
            pending,
            _options.MaxConcurrentPendingPerUser,
            _options.DailyUploadQuotaBytes,
            nowUtc.AddHours(-24),
            ct);

        var expiresIn = TimeSpan.FromMinutes(Math.Max(1, _options.PresignedPutTtlMinutes));
        var uploadUrl = _mediaStorage.GeneratePresignedPutUrl(
            created.ObjectKey,
            canonicalMime,
            created.DeclaredSizeBytes,
            expiresIn,
            nowUtc);

        _logger.LogInformation(
            "Initiated avatar upload {UploadId} for user {UserId} ({SizeBytes} bytes).",
            created.Id.Value,
            callerId.Value,
            created.DeclaredSizeBytes);

        return new InitiateAvatarUploadResponseDto(
            created.Id,
            uploadUrl,
            nowUtc + expiresIn,
            canonicalMime,
            created.DeclaredSizeBytes);
    }

    public async Task<AvatarUpdatedResponseDto?> GetUploadStatusAsync(
        UserId callerId,
        AvatarUploadId uploadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(uploadId);

        var upload = await _avatarPersistence.GetPendingByIdNoTrackingAsync(uploadId, ct);
        if (upload is null || upload.UserId != callerId)
        {
            throw new NotFoundException("AvatarUpload", uploadId.Value);
        }

        if (upload.State == MediaState.Failed)
        {
            throw new DomainException(upload.FailureReason ?? "Profile photo verification failed.");
        }

        if (upload.State == MediaState.Deleted)
        {
            throw new ConflictException("Avatar upload was already cancelled.");
        }

        if (upload.State != MediaState.Uploaded)
        {
            return null;
        }

        var profile = await _userReader.GetByIdAsync(callerId, ct);
        return new AvatarUpdatedResponseDto(profile?.AvatarVersion, upload.CompletedAtUtc ?? _timeProvider.GetUtcNow());
    }

    public async Task<AvatarUpdatedResponseDto> CompleteUploadAsync(
        UserId callerId,
        AvatarUploadId uploadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(uploadId);

        var upload = await _avatarPersistence.GetPendingByIdAsync(uploadId, ct);
        if (upload is null)
        {
            throw new NotFoundException("AvatarUpload", uploadId.Value);
        }

        if (upload.UserId != callerId)
        {
            throw new ForbiddenException("Only the owner can complete this avatar upload.");
        }

        if (upload.State == MediaState.Uploaded)
        {
            var profile = await _userReader.GetByIdAsync(callerId, ct);
            return new AvatarUpdatedResponseDto(profile?.AvatarVersion, _timeProvider.GetUtcNow());
        }

        if (upload.State == MediaState.Failed)
        {
            throw new DomainException(upload.FailureReason ?? "Profile photo verification failed.");
        }

        if (upload.State == MediaState.Deleted)
        {
            throw new ConflictException("Avatar upload was already cancelled.");
        }

        var stagingKey = upload.ObjectKey;
        var stagingMeta = await _mediaStorage.GetObjectMetadataAsync(stagingKey, ct);
        if (stagingMeta is null)
        {
            var nowUtc = _timeProvider.GetUtcNow();
            upload.MarkFailed("Uploaded avatar object was not found in staging storage.", nowUtc);
            upload.MarkPurged(nowUtc);
            await _avatarPersistence.UpdatePendingAsync(upload, ct);
            throw new DomainException("Uploaded file was not found in staging storage. Please upload again.");
        }

        if (stagingMeta.ContentLength <= 0
            || stagingMeta.ContentLength > AvatarConstraints.MaxRawAvatarUploadBytes
            || stagingMeta.ContentLength != upload.DeclaredSizeBytes)
        {
            var failureMessage = stagingMeta.ContentLength <= 0
                ? "Uploaded file cannot be empty."
                : stagingMeta.ContentLength != upload.DeclaredSizeBytes
                    ? "Uploaded file size does not match the initiated upload size."
                    : $"Profile photo size exceeds maximum allowed {AvatarConstraints.MaxRawAvatarUploadBytes} bytes.";

            await FailAndCleanStagingAsync(
                upload,
                stagingKey,
                failureMessage,
                stagingMeta.ContentLength > 0 ? stagingMeta.ContentLength : null,
                ct);
            throw new DomainException(failureMessage);
        }

        ProcessedAvatarResult processed;
        try
        {
            processed = await _avatarProcessor.ProcessAvatarAsync(upload, stagingMeta, ct);
        }
        catch (DomainException ex)
        {
            await FailAndCleanStagingAsync(upload, stagingKey, ex.Message, stagingMeta.ContentLength, ct);
            throw;
        }
        catch (Exception ex) when (ex is NotFoundException or ConflictException)
        {
            return await HandleConcurrentConflictAsync(uploadId, callerId, ex.Message, ct);
        }

        var fullKey = $"{MediaConstraints.AvatarKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";
        var thumbKey = $"{MediaConstraints.AvatarKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";
        const string disposition = "inline; filename=\"avatar.webp\"";

        long verifiedRawSize;
        long fullSize;
        long thumbSize;
        try
        {
            await using (processed)
            {
                verifiedRawSize = processed.VerifiedRawSizeBytes;
                fullSize = processed.FullSizeBytes;
                thumbSize = processed.ThumbnailSizeBytes;

                await _mediaStorage.PromoteFromStreamAsync(
                    stagingKey,
                    fullKey,
                    processed.FullStream,
                    fullSize,
                    processed.ContentType,
                    disposition,
                    ct);

                await _mediaStorage.PromoteFromStreamAsync(
                    stagingKey,
                    thumbKey,
                    processed.ThumbnailStream,
                    thumbSize,
                    processed.ContentType,
                    disposition,
                    ct);
            }
        }
        catch (Exception ex)
        {
            await BestEffortDeleteStorageAsync(fullKey, CancellationToken.None);
            await BestEffortDeleteStorageAsync(thumbKey, CancellationToken.None);
            if (ex is NotFoundException or ConflictException)
            {
                return await HandleConcurrentConflictAsync(uploadId, callerId, ex.Message, ct);
            }

            throw;
        }

        var committedAtUtc = _timeProvider.GetUtcNow();
        var newVersion = AvatarConstraints.GenerateAvatarVersion();
        upload.MarkCompleted(verifiedRawSize, committedAtUtc);

        AvatarCommitResult commitResult;
        try
        {
            commitResult = await _avatarPersistence.CommitUserAvatarAsync(
                upload,
                fullKey,
                thumbKey,
                AvatarConstraints.CanonicalAvatarContentType,
                fullSize,
                thumbSize,
                newVersion,
                committedAtUtc,
                ct);
        }
        catch (Exception ex) when (ex is NotFoundException or ConflictException)
        {
            try
            {
                return await HandleConcurrentConflictAsync(uploadId, callerId, ex.Message, ct);
            }
            finally
            {
                await BestEffortDeleteStorageAsync(fullKey, CancellationToken.None);
                await BestEffortDeleteStorageAsync(thumbKey, CancellationToken.None);
                await BestEffortDeleteStorageAsync(stagingKey, CancellationToken.None);
            }
        }
        // Other persistence errors can leave the commit outcome unknown. Keep staging for retry
        // and let the orphan sweep remove any permanent objects that are not referenced.

        await BestEffortDeleteStorageAsync(stagingKey, CancellationToken.None);

        if (!commitResult.Applied)
        {
            // A newer upload or removal won while this upload was encoding; clean up orphaned new objects.
            await BestEffortDeleteStorageAsync(fullKey, CancellationToken.None);
            await BestEffortDeleteStorageAsync(thumbKey, CancellationToken.None);
            return new AvatarUpdatedResponseDto(commitResult.CurrentAvatarVersion, committedAtUtc);
        }

        if (!string.IsNullOrWhiteSpace(commitResult.PreviousFullObjectKey))
        {
            await BestEffortDeleteStorageAsync(commitResult.PreviousFullObjectKey, CancellationToken.None);
        }

        if (!string.IsNullOrWhiteSpace(commitResult.PreviousThumbObjectKey))
        {
            await BestEffortDeleteStorageAsync(commitResult.PreviousThumbObjectKey, CancellationToken.None);
        }

        await BestEffortBroadcastProfileUpdatedAsync(
            callerId,
            commitResult.DisplayName ?? string.Empty,
            commitResult.CurrentAvatarVersion,
            ct);

        _logger.LogInformation(
            "Updated profile photo for user {UserId} (full {FullBytes} B, thumb {ThumbBytes} B).",
            callerId.Value,
            fullSize,
            thumbSize);

        return new AvatarUpdatedResponseDto(commitResult.CurrentAvatarVersion, committedAtUtc);
    }

    public async Task RemoveAvatarAsync(UserId callerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);

        var nowUtc = _timeProvider.GetUtcNow();
        var removed = await _avatarPersistence.RemoveUserAvatarAsync(callerId, nowUtc, ct);

        if (!string.IsNullOrWhiteSpace(removed.PreviousFullObjectKey))
        {
            await BestEffortDeleteStorageAsync(removed.PreviousFullObjectKey, CancellationToken.None);
        }

        if (!string.IsNullOrWhiteSpace(removed.PreviousThumbObjectKey))
        {
            await BestEffortDeleteStorageAsync(removed.PreviousThumbObjectKey, CancellationToken.None);
        }

        if (removed.HadAvatar)
        {
            await BestEffortBroadcastProfileUpdatedAsync(
                callerId,
                removed.DisplayName ?? string.Empty,
                avatarVersion: null,
                ct);
        }

        _logger.LogInformation("Removed profile photo for user {UserId}.", callerId.Value);
    }

    public async Task<AvatarUrlDto> GetAvatarUrlAsync(
        UserId callerId,
        UserId targetUserId,
        AvatarSizeVariant variant = AvatarSizeVariant.Thumbnail,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(targetUserId);

        var metadata = await _avatarPersistence.GetUserAvatarMetadataAsync(targetUserId, ct);
        if (metadata is null || metadata.IsDeleted || !metadata.EmailConfirmed)
        {
            throw new NotFoundException("UserAvatar", targetUserId.Value);
        }

        if (callerId != targetUserId &&
            await _blockPersistence.IsBlockedBidirectionalAsync(callerId, targetUserId, ct))
        {
            throw new NotFoundException("UserAvatar", targetUserId.Value);
        }

        var objectKey = variant == AvatarSizeVariant.Full
            ? metadata.FullObjectKey
            : (metadata.ThumbObjectKey ?? metadata.FullObjectKey);

        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new NotFoundException("UserAvatar", targetUserId.Value);
        }

        var nowUtc = _timeProvider.GetUtcNow();
        var expiresIn = TimeSpan.FromMinutes(AvatarConstraints.AvatarPresignedGetTtlMinutes);
        var contentType = string.IsNullOrWhiteSpace(metadata.ContentType)
            ? AvatarConstraints.CanonicalAvatarContentType
            : metadata.ContentType;

        var url = _mediaStorage.GeneratePresignedGetUrl(
            objectKey,
            contentType,
            "inline; filename=\"avatar.webp\"",
            expiresIn,
            nowUtc);

        return new AvatarUrlDto(
            url,
            nowUtc + expiresIn,
            metadata.Version ?? string.Empty,
            variant == AvatarSizeVariant.Full ? "full" : "thumb");
    }

    private async Task FailAndCleanStagingAsync(
        PendingAvatarUpload upload,
        string stagingKey,
        string failureReason,
        long? verifiedSizeBytes,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        upload.MarkFailed(failureReason, nowUtc, verifiedSizeBytes);

        try
        {
            await _avatarPersistence.UpdatePendingAsync(upload, ct);
        }
        catch (ConflictException)
        {
            return;
        }

        if (await BestEffortDeleteStorageAsync(stagingKey, CancellationToken.None))
        {
            upload.MarkPurged(_timeProvider.GetUtcNow());
            try
            {
                await _avatarPersistence.UpdatePendingAsync(upload, CancellationToken.None);
            }
            catch (ConflictException)
            {
            }
        }
    }

    private async Task<AvatarUpdatedResponseDto> HandleConcurrentConflictAsync(
        AvatarUploadId uploadId,
        UserId callerId,
        string conflictMessage,
        CancellationToken ct)
    {
        var reloaded = await _avatarPersistence.GetPendingByIdNoTrackingAsync(uploadId, ct);
        if (reloaded is not null && reloaded.State == MediaState.Uploaded)
        {
            var profile = await _userReader.GetByIdAsync(callerId, ct);
            return new AvatarUpdatedResponseDto(profile?.AvatarVersion, _timeProvider.GetUtcNow());
        }

        if (reloaded is not null && reloaded.State == MediaState.Failed)
        {
            throw new DomainException(reloaded.FailureReason ?? "Profile photo verification failed.");
        }

        throw new ConflictException(
            string.IsNullOrWhiteSpace(conflictMessage)
                ? "The staged profile photo was modified concurrently. Please try again."
                : conflictMessage);
    }

    private async Task BestEffortBroadcastProfileUpdatedAsync(
        UserId userId,
        string displayName,
        string? avatarVersion,
        CancellationToken ct)
    {
        try
        {
            await _realtimeNotifier.NotifyUserProfileUpdatedAsync(
                userId,
                new UserProfileUpdatedDto(userId, displayName, avatarVersion),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to broadcast profile update notification for user {UserId}.", userId.Value);
        }
    }

    private async Task<bool> BestEffortDeleteStorageAsync(string objectKey, CancellationToken ct)
    {
        try
        {
            await _mediaStorage.DeleteObjectAsync(objectKey, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Best-effort avatar storage cleanup failed for key {ObjectKey}.", objectKey);
            return false;
        }
    }
}
