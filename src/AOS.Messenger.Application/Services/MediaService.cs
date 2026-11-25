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
/// Application service orchestrating presigned media uploads, malware/bomb-safe verification,
/// crash-safe staging promotion, upload cancellation, and authorized download URL generation.
/// </summary>
public sealed class MediaService : IMediaService
{
    private readonly IMediaPersistence _mediaPersistence;
    private readonly IMediaStorage _mediaStorage;
    private readonly IMediaVerifier _mediaVerifier;
    private readonly IConversationPersistence _conversationPersistence;
    private readonly IMessagePersistence _messagePersistence;
    private readonly IBlockPersistence _blockPersistence;
    private readonly ILogger<MediaService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly MediaOptions _options;

    public MediaService(
        IMediaPersistence mediaPersistence,
        IMediaStorage mediaStorage,
        IMediaVerifier mediaVerifier,
        IConversationPersistence conversationPersistence,
        IMessagePersistence messagePersistence,
        IBlockPersistence blockPersistence,
        ILogger<MediaService> logger,
        IOptions<MediaOptions>? options = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(mediaPersistence);
        ArgumentNullException.ThrowIfNull(mediaStorage);
        ArgumentNullException.ThrowIfNull(mediaVerifier);
        ArgumentNullException.ThrowIfNull(conversationPersistence);
        ArgumentNullException.ThrowIfNull(messagePersistence);
        ArgumentNullException.ThrowIfNull(blockPersistence);
        ArgumentNullException.ThrowIfNull(logger);

        _mediaPersistence = mediaPersistence;
        _mediaStorage = mediaStorage;
        _mediaVerifier = mediaVerifier;
        _conversationPersistence = conversationPersistence;
        _messagePersistence = messagePersistence;
        _blockPersistence = blockPersistence;
        _logger = logger;
        _options = options?.Value ?? new MediaOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<InitiateMediaUploadResponseDto> InitiateUploadAsync(
        UserId callerId,
        ConversationId conversationId,
        InitiateMediaUploadRequestDto dto,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(dto);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var otherParticipantId = conversation.GetOtherParticipant(callerId);
        if (await _blockPersistence.IsBlockedBidirectionalAsync(callerId, otherParticipantId, ct))
        {
            throw new ForbiddenException("Cannot upload media in a blocked conversation.");
        }

        var sanitizedFileName = MediaConstraints.SanitizeFileName(dto.FileName, dto.ContentType);
        var (kind, canonicalMime, maxSizeBytes) = MediaConstraints.ResolveAllowedMedia(sanitizedFileName, dto.ContentType);

        if (kind == MediaKind.Voice)
        {
            throw new DomainException("Voice messages are not supported yet.");
        }

        if (dto.SizeBytes <= 0 || dto.SizeBytes > maxSizeBytes)
        {
            throw new DomainException($"File size must be between 1 byte and {maxSizeBytes} bytes for {kind}.");
        }

        var nowUtc = _timeProvider.GetUtcNow();
        var pendingMedia = Media.CreatePending(
            callerId,
            conversationId,
            kind,
            sanitizedFileName,
            canonicalMime,
            dto.SizeBytes,
            nowUtc);

        var created = await _mediaPersistence.TryCreatePendingWithQuotaCheckAsync(
            pendingMedia,
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
            "Initiated media upload {MediaId} in conversation {ConversationId} by user {UserId} ({Kind}, {SizeBytes} bytes).",
            created.Id.Value,
            conversationId.Value,
            callerId.Value,
            created.Kind,
            created.DeclaredSizeBytes);

        return new InitiateMediaUploadResponseDto(
            created.Id,
            uploadUrl,
            nowUtc + expiresIn,
            canonicalMime,
            created.DeclaredSizeBytes,
            created.SanitizedFileName,
            created.Kind.ToApiString());
    }

    public async Task<MediaAttachmentDto> CompleteUploadAsync(
        UserId callerId,
        ConversationId conversationId,
        MediaId mediaId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(mediaId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var media = await _mediaPersistence.GetByIdAsync(mediaId, ct);
        if (media is null || media.ConversationId != conversationId)
        {
            throw new NotFoundException("Media", mediaId.Value);
        }

        if (media.UploaderId != callerId)
        {
            throw new ForbiddenException("Only the uploader can complete this media upload.");
        }

        var otherParticipantId = conversation.GetOtherParticipant(callerId);
        if (await _blockPersistence.IsBlockedBidirectionalAsync(callerId, otherParticipantId, ct))
        {
            if (media.State == MediaState.Pending)
            {
                long? stagedBytes = null;
                try
                {
                    var blockedMeta = await _mediaStorage.GetObjectMetadataAsync(media.ObjectKey, ct);
                    if (blockedMeta is not null && blockedMeta.ContentLength > 0)
                    {
                        stagedBytes = blockedMeta.ContentLength;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Charge DeclaredSizeBytes conservatively if staging metadata lookup transiently fails
                    // so blocked-upload cleanup can never refund uploaded bytes.
                    stagedBytes = media.DeclaredSizeBytes;
                }

                await FailAndCleanStagingAsync(
                    media,
                    mediaId,
                    media.ObjectKey,
                    "Blocked conversation.",
                    verifiedSizeBytes: stagedBytes,
                    ct);
            }

            throw new ForbiddenException("Cannot complete media upload in a blocked conversation.");
        }

        if (media.State == MediaState.Uploaded)
        {
            return ToAttachmentDto(media);
        }

        if (media.State == MediaState.Failed)
        {
            throw new DomainException(media.FailureReason ?? "Media upload verification failed.");
        }

        if (media.State is MediaState.Attached or MediaState.Deleted)
        {
            throw new ConflictException($"Media cannot be completed from state '{media.State}'.");
        }

        var stagingKey = media.ObjectKey;
        var stagingMeta = await _mediaStorage.GetObjectMetadataAsync(stagingKey, ct);
        if (stagingMeta is null)
        {
            var nowUtc = _timeProvider.GetUtcNow();
            media.MarkFailed("Uploaded object was not found in staging storage.", nowUtc);
            media.MarkPurged(nowUtc);

            try
            {
                await _mediaPersistence.UpdateAsync(media, ct);
            }
            catch (ConflictException)
            {
                var reloaded = await _mediaPersistence.GetByIdNoTrackingAsync(mediaId, ct);
                if (reloaded is not null && reloaded.State == MediaState.Uploaded)
                {
                    return ToAttachmentDto(reloaded);
                }

                throw;
            }

            throw new DomainException("Uploaded file was not found in staging storage. Please upload again.");
        }

        var maxAllowedBytes = MediaConstraints.GetMaxSizeBytes(media.Kind);
        if (stagingMeta.ContentLength <= 0
            || stagingMeta.ContentLength > maxAllowedBytes
            || stagingMeta.ContentLength != media.DeclaredSizeBytes)
        {
            var failureMessage = stagingMeta.ContentLength <= 0
                ? "Uploaded file cannot be empty."
                : stagingMeta.ContentLength != media.DeclaredSizeBytes
                    ? "Uploaded file size does not match the initiated upload size."
                    : $"Uploaded file size exceeds maximum allowed {maxAllowedBytes} bytes.";

            var recoveredDto = await FailAndCleanStagingAsync(
                media,
                mediaId,
                stagingKey,
                failureMessage,
                stagingMeta.ContentLength > 0 ? stagingMeta.ContentLength : null,
                ct);

            if (recoveredDto is not null)
            {
                return recoveredDto;
            }

            throw new DomainException(failureMessage);
        }

        VerifiedMediaResult verified;
        try
        {
            verified = await _mediaVerifier.VerifyAndSanitizeAsync(media, stagingMeta, ct);
        }
        catch (DomainException ex)
        {
            var recoveredDto = await FailAndCleanStagingAsync(
                media,
                mediaId,
                stagingKey,
                ex.Message,
                stagingMeta.ContentLength,
                ct);

            if (recoveredDto is not null)
            {
                return recoveredDto;
            }

            throw;
        }
        catch (Exception ex) when (ex is NotFoundException or ConflictException)
        {
            return await HandleConcurrentCompletionConflictAsync(
                mediaId,
                ct,
                ex is ConflictException ? ex.Message : null);
        }

        // Generate a fresh, per-attempt permanent key so concurrent/retried completions never overwrite committed objects.
        var permanentKey = $"{MediaConstraints.MediaKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";
        string? thumbnailKey = null;
        string? thumbnailContentType = null;
        long? thumbnailSizeBytes = null;
        var contentDisposition = MediaConstraints.BuildContentDisposition(
            media.SanitizedFileName,
            isAttachment: media.Kind == MediaKind.Document);

        try
        {
            await using (verified)
            {
                if (verified.ProcessedStream is not null)
                {
                    await _mediaStorage.PromoteFromStreamAsync(
                        stagingKey,
                        permanentKey,
                        verified.ProcessedStream,
                        verified.VerifiedSizeBytes,
                        verified.VerifiedContentType,
                        contentDisposition,
                        ct);
                }
                else
                {
                    await _mediaStorage.PromoteByCopyAsync(
                        stagingKey,
                        permanentKey,
                        verified.VerifiedContentType,
                        contentDisposition,
                        stagingMeta.ETag,
                        ct);
                }

                if (verified.ThumbnailStream is not null
                    && !string.IsNullOrWhiteSpace(verified.ThumbnailContentType)
                    && verified.ThumbnailSizeBytes is > 0)
                {
                    thumbnailKey = $"{MediaConstraints.ThumbnailKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";
                    thumbnailContentType = verified.ThumbnailContentType;
                    thumbnailSizeBytes = verified.ThumbnailSizeBytes.Value;
                    await _mediaStorage.PromoteFromStreamAsync(
                        stagingKey,
                        thumbnailKey,
                        verified.ThumbnailStream,
                        thumbnailSizeBytes.Value,
                        thumbnailContentType,
                        contentDisposition,
                        ct);
                }
            }
        }
        catch (Exception ex) when (ex is NotFoundException or ConflictException)
        {
            await BestEffortDeleteStorageAsync(permanentKey, mediaId, CancellationToken.None);
            if (thumbnailKey is not null)
            {
                await BestEffortDeleteStorageAsync(thumbnailKey, mediaId, CancellationToken.None);
            }
            return await HandleConcurrentCompletionConflictAsync(mediaId, ct);
        }
        catch
        {
            await BestEffortDeleteStorageAsync(permanentKey, mediaId, CancellationToken.None);
            if (thumbnailKey is not null)
            {
                await BestEffortDeleteStorageAsync(thumbnailKey, mediaId, CancellationToken.None);
            }
            throw;
        }

        var uploadedAtUtc = _timeProvider.GetUtcNow();
        media.MarkUploaded(
            permanentKey,
            verified.VerifiedContentType,
            verified.VerifiedSizeBytes,
            verified.Width,
            verified.Height,
            verified.DurationMs,
            uploadedAtUtc,
            thumbnailKey,
            thumbnailContentType,
            thumbnailSizeBytes);

        try
        {
            await _mediaPersistence.UpdateAsync(media, ct);
        }
        catch (ConflictException)
        {
            // Delete this losing attempt's disjoint permanent object; never touch the winning permanent object.
            await BestEffortDeleteStorageAsync(permanentKey, mediaId, CancellationToken.None);
            if (thumbnailKey is not null)
            {
                await BestEffortDeleteStorageAsync(thumbnailKey, mediaId, CancellationToken.None);
            }

            var reloaded = await _mediaPersistence.GetByIdNoTrackingAsync(mediaId, ct);
            if (reloaded is not null && reloaded.State == MediaState.Uploaded)
            {
                await BestEffortDeleteStorageAsync(stagingKey, mediaId, CancellationToken.None);
                return ToAttachmentDto(reloaded);
            }

            await BestEffortDeleteStorageAsync(stagingKey, mediaId, CancellationToken.None);
            throw;
        }

        await BestEffortDeleteStorageAsync(stagingKey, mediaId, CancellationToken.None);

        _logger.LogInformation(
            "Completed media upload {MediaId} in conversation {ConversationId} for user {UserId} ({Kind}, {SizeBytes} bytes).",
            media.Id.Value,
            conversationId.Value,
            callerId.Value,
            media.Kind,
            media.VerifiedSizeBytes);

        return ToAttachmentDto(media);
    }

    public async Task CancelUploadAsync(
        UserId callerId,
        ConversationId conversationId,
        MediaId mediaId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(mediaId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var media = await _mediaPersistence.GetByIdAsync(mediaId, ct);
        if (media is null || media.ConversationId != conversationId)
        {
            throw new NotFoundException("Media", mediaId.Value);
        }

        if (media.UploaderId != callerId)
        {
            throw new ForbiddenException("Only the uploader can cancel this media upload.");
        }

        if (media.State == MediaState.Attached)
        {
            throw new ConflictException("Cannot cancel media that is already attached to a message.");
        }

        if (media.State == MediaState.Deleted)
        {
            return;
        }

        var objectKeyToDelete = media.ObjectKey;
        var thumbKeyToDelete = media.ThumbnailObjectKey;

        if (media.State == MediaState.Failed)
        {
            if (media.PurgedAtUtc is null && await BestEffortDeleteStorageAsync(objectKeyToDelete, mediaId, ct))
            {
                if (thumbKeyToDelete is not null)
                {
                    await BestEffortDeleteStorageAsync(thumbKeyToDelete, mediaId, ct);
                }
                media.MarkPurged(_timeProvider.GetUtcNow());
                try
                {
                    await _mediaPersistence.UpdateAsync(media, ct);
                }
                catch (ConflictException)
                {
                    // Cleanup or concurrent purge won cleanly.
                }
            }

            return;
        }

        var nowUtc = _timeProvider.GetUtcNow();
        media.MarkDeleted(nowUtc);

        try
        {
            await _mediaPersistence.UpdateAsync(media, ct);
        }
        catch (ConflictException)
        {
            var reloaded = await _mediaPersistence.GetByIdNoTrackingAsync(mediaId, ct);
            if (reloaded is not null && reloaded.State == MediaState.Deleted)
            {
                return;
            }

            throw;
        }

        var deleted = await BestEffortDeleteStorageAsync(objectKeyToDelete, mediaId, ct);
        if (thumbKeyToDelete is not null)
        {
            await BestEffortDeleteStorageAsync(thumbKeyToDelete, mediaId, ct);
        }
        if (deleted)
        {
            media.MarkPurged(_timeProvider.GetUtcNow());
            try
            {
                await _mediaPersistence.UpdateAsync(media, ct);
            }
            catch (ConflictException)
            {
                // Cleanup or concurrent purge won cleanly.
            }
        }

        _logger.LogInformation(
            "Cancelled media upload {MediaId} in conversation {ConversationId} for user {UserId}.",
            mediaId.Value,
            conversationId.Value,
            callerId.Value);
    }

    public async Task<MediaDownloadUrlDto> GetDownloadUrlAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        MediaId mediaId,
        bool forceDownload = false,
        bool thumbnail = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(mediaId);

        var conversation = await _conversationPersistence.GetByIdAsync(conversationId, ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation", conversationId.Value);
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("User is not a participant in this conversation.");
        }

        var message = await _messagePersistence.GetByIdAsync(conversationId, messageId, ct);
        if (message is null || message.IsDeletedForEveryone)
        {
            throw new NotFoundException("Media", mediaId.Value);
        }

        var cutoff = await GetEffectiveCutoffUtcAsync(conversationId, callerId, ct);
        if (cutoff.HasValue && message.CreatedAtUtc < cutoff.Value)
        {
            throw new NotFoundException("Media", mediaId.Value);
        }

        var isHiddenForCaller = await _messagePersistence.IsMessageDeletedForUserAsync(
            conversationId,
            messageId,
            callerId,
            ct);
        if (isHiddenForCaller)
        {
            throw new NotFoundException("Media", mediaId.Value);
        }

        var media = await _mediaPersistence.GetByIdNoTrackingAsync(mediaId, ct);
        if (media is null
            || media.ConversationId != conversationId
            || media.MessageId != messageId
            || media.State != MediaState.Attached)
        {
            throw new NotFoundException("Media", mediaId.Value);
        }

        var useThumbnail = thumbnail && !forceDownload && !string.IsNullOrWhiteSpace(media.ThumbnailObjectKey);
        var isAttachment = forceDownload || media.Kind == MediaKind.Document;
        var contentDisposition = MediaConstraints.BuildContentDisposition(media.SanitizedFileName, isAttachment);
        var targetKey = useThumbnail ? media.ThumbnailObjectKey! : media.ObjectKey;
        var contentType = useThumbnail
            ? (media.ThumbnailContentType ?? "image/webp")
            : (media.VerifiedContentType ?? media.DeclaredContentType);
        var sizeBytes = useThumbnail
            ? (media.ThumbnailSizeBytes ?? media.VerifiedSizeBytes ?? media.DeclaredSizeBytes)
            : (media.VerifiedSizeBytes ?? media.DeclaredSizeBytes);

        var nowUtc = _timeProvider.GetUtcNow();
        var expiresIn = TimeSpan.FromMinutes(Math.Max(1, _options.PresignedGetTtlMinutes));
        var downloadUrl = _mediaStorage.GeneratePresignedGetUrl(
            targetKey,
            contentType,
            contentDisposition,
            expiresIn,
            nowUtc);

        _logger.LogDebug(
            "Generated download URL for media {MediaId} in conversation {ConversationId} for user {UserId}.",
            mediaId.Value,
            conversationId.Value,
            callerId.Value);

        return new MediaDownloadUrlDto(
            downloadUrl,
            nowUtc + expiresIn,
            media.SanitizedFileName,
            contentType,
            sizeBytes);
    }

    private async Task<MediaAttachmentDto?> FailAndCleanStagingAsync(
        Media media,
        MediaId mediaId,
        string stagingKey,
        string failureReason,
        long? verifiedSizeBytes,
        CancellationToken ct)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        media.MarkFailed(failureReason, nowUtc, verifiedSizeBytes);

        try
        {
            await _mediaPersistence.UpdateAsync(media, ct);
        }
        catch (ConflictException)
        {
            var reloaded = await _mediaPersistence.GetByIdNoTrackingAsync(mediaId, ct);
            if (reloaded is not null && reloaded.State == MediaState.Uploaded)
            {
                return ToAttachmentDto(reloaded);
            }

            return null;
        }

        if (await BestEffortDeleteStorageAsync(stagingKey, mediaId, CancellationToken.None))
        {
            media.MarkPurged(_timeProvider.GetUtcNow());
            try
            {
                await _mediaPersistence.UpdateAsync(media, CancellationToken.None);
            }
            catch (ConflictException)
            {
                // Cleanup or concurrent purge won cleanly.
            }
        }

        return null;
    }

    private async Task<MediaAttachmentDto> HandleConcurrentCompletionConflictAsync(
        MediaId mediaId,
        CancellationToken ct,
        string? fallbackConflictMessage = null)
    {
        var reloaded = await _mediaPersistence.GetByIdNoTrackingAsync(mediaId, ct);
        if (reloaded is not null && reloaded.State == MediaState.Uploaded)
        {
            return ToAttachmentDto(reloaded);
        }

        if (reloaded is not null && reloaded.State == MediaState.Failed)
        {
            throw new DomainException(reloaded.FailureReason ?? "Media upload verification failed.");
        }

        if (reloaded is not null && reloaded.State is MediaState.Attached or MediaState.Deleted)
        {
            throw new ConflictException($"Media cannot be completed from state '{reloaded.State}'.");
        }

        throw new ConflictException(
            string.IsNullOrWhiteSpace(fallbackConflictMessage)
                ? "The staged media object was modified or removed concurrently. Please try again."
                : fallbackConflictMessage);
    }

    private async Task<bool> BestEffortDeleteStorageAsync(
        string objectKey,
        MediaId mediaId,
        CancellationToken ct)
    {
        try
        {
            await _mediaStorage.DeleteObjectAsync(objectKey, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Best-effort storage cleanup failed for media {MediaId}.", mediaId.Value);
            return false;
        }
    }

    private async Task<DateTimeOffset?> GetEffectiveCutoffUtcAsync(
        ConversationId conversationId,
        UserId callerId,
        CancellationToken ct)
    {
        var userState = await _conversationPersistence.GetUserStateAsync(conversationId, callerId, ct);
        if (userState is null)
        {
            return null;
        }

        DateTimeOffset? cutoff = userState.ClearedAtUtc;
        if (userState.DeletedAtUtc.HasValue &&
            (!cutoff.HasValue || userState.DeletedAtUtc.Value > cutoff.Value))
        {
            cutoff = userState.DeletedAtUtc.Value;
        }

        return cutoff;
    }

    private static MediaAttachmentDto ToAttachmentDto(Media media)
    {
        return new MediaAttachmentDto(
            media.Id,
            media.Kind.ToApiString(),
            media.SanitizedFileName,
            media.VerifiedContentType ?? media.DeclaredContentType,
            media.VerifiedSizeBytes ?? media.DeclaredSizeBytes,
            media.Width,
            media.Height,
            media.DurationMs,
            media.DisplayOrder,
            HasThumbnail: !string.IsNullOrWhiteSpace(media.ThumbnailObjectKey));
    }
}
