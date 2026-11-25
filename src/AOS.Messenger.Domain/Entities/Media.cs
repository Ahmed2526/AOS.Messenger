using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Represents a media attachment uploaded to private Cloudflare R2 storage and bound to a conversation/message.
/// </summary>
public sealed class Media : Entity<MediaId>
{
    public string StorageObjectId { get; private set; } = null!;
    public UserId UploaderId { get; private set; } = null!;
    public ConversationId ConversationId { get; private set; } = null!;
    public MessageId? MessageId { get; private set; }
    public int DisplayOrder { get; private set; }
    public MediaKind Kind { get; private set; }
    public MediaState State { get; private set; }
    public string ObjectKey { get; private set; } = null!;
    public string? ThumbnailObjectKey { get; private set; }
    public string? ThumbnailContentType { get; private set; }
    public long? ThumbnailSizeBytes { get; private set; }
    public string SanitizedFileName { get; private set; } = null!;
    public string DeclaredContentType { get; private set; } = null!;
    public string? VerifiedContentType { get; private set; }
    public long DeclaredSizeBytes { get; private set; }
    public long? VerifiedSizeBytes { get; private set; }
    public int? Width { get; private set; }
    public int? Height { get; private set; }
    public int? DurationMs { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UploadedAtUtc { get; private set; }
    public DateTimeOffset? AttachedAtUtc { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public DateTimeOffset? PurgedAtUtc { get; private set; }
    public string? FailureReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private Media()
    {
    }

    public static Media CreatePending(
        UserId uploaderId,
        ConversationId conversationId,
        MediaKind kind,
        string sanitizedFileName,
        string declaredContentType,
        long declaredSizeBytes,
        DateTimeOffset nowUtc,
        string? storageObjectId = null,
        MediaId? id = null)
    {
        ArgumentNullException.ThrowIfNull(uploaderId);
        ArgumentNullException.ThrowIfNull(conversationId);

        if (string.IsNullOrWhiteSpace(sanitizedFileName))
        {
            throw new DomainException("Sanitized file name cannot be empty.");
        }

        var trimmedName = sanitizedFileName.Trim();
        if (trimmedName.Length > 255)
        {
            throw new DomainException("Sanitized file name cannot exceed 255 characters.");
        }

        if (string.IsNullOrWhiteSpace(declaredContentType))
        {
            throw new DomainException("Declared content type cannot be empty.");
        }

        var trimmedMime = declaredContentType.Trim();
        if (trimmedMime.Length > 128)
        {
            throw new DomainException("Declared content type cannot exceed 128 characters.");
        }

        var maxBytes = MediaConstraints.GetMaxSizeBytes(kind);
        if (declaredSizeBytes <= 0 || declaredSizeBytes > maxBytes)
        {
            throw new DomainException($"File size must be between 1 byte and {maxBytes} bytes for {kind}.");
        }

        var objId = storageObjectId ?? MediaConstraints.GenerateStorageObjectId();
        if (!MediaConstraints.IsValidStorageObjectId(objId))
        {
            throw new DomainException("StorageObjectId must be a 32-character lowercase hexadecimal string.");
        }

        return new Media
        {
            Id = id!,
            StorageObjectId = objId,
            UploaderId = uploaderId,
            ConversationId = conversationId,
            MessageId = null,
            DisplayOrder = 0,
            Kind = kind,
            State = MediaState.Pending,
            ObjectKey = $"{MediaConstraints.PendingKeyPrefix}{objId}",
            SanitizedFileName = trimmedName,
            DeclaredContentType = trimmedMime,
            DeclaredSizeBytes = declaredSizeBytes,
            CreatedAtUtc = nowUtc,
            RowVersion = []
        };
    }

    public void MarkUploaded(
        string promotedObjectKey,
        string verifiedContentType,
        long verifiedSizeBytes,
        int? width,
        int? height,
        int? durationMs,
        DateTimeOffset nowUtc,
        string? thumbnailObjectKey = null,
        string? thumbnailContentType = null,
        long? thumbnailSizeBytes = null)
    {
        if (State != MediaState.Pending)
        {
            throw new DomainException($"Cannot mark media as uploaded from state '{State}'.");
        }

        if (string.IsNullOrWhiteSpace(promotedObjectKey)
            || !promotedObjectKey.StartsWith(MediaConstraints.MediaKeyPrefix, StringComparison.Ordinal))
        {
            throw new DomainException($"Promoted object key must start with '{MediaConstraints.MediaKeyPrefix}'.");
        }

        var suffix = promotedObjectKey[MediaConstraints.MediaKeyPrefix.Length..];
        if (!MediaConstraints.IsValidStorageObjectId(suffix))
        {
            throw new DomainException("Promoted object key must have a valid 32-character lowercase hexadecimal identifier.");
        }

        if (string.IsNullOrWhiteSpace(verifiedContentType))
        {
            throw new DomainException("Verified content type cannot be empty.");
        }

        var trimmedMime = verifiedContentType.Trim();
        if (trimmedMime.Length > 128)
        {
            throw new DomainException("Verified content type cannot exceed 128 characters.");
        }

        var maxBytes = MediaConstraints.GetMaxSizeBytes(Kind);
        if (verifiedSizeBytes <= 0 || verifiedSizeBytes > maxBytes)
        {
            throw new DomainException($"Verified size must be between 1 byte and {maxBytes} bytes for {Kind}.");
        }

        if (width.HasValue && (width.Value <= 0 || width.Value > MediaConstraints.MaxImageWidth))
        {
            throw new DomainException($"Image width must be between 1 and {MediaConstraints.MaxImageWidth} pixels.");
        }

        if (height.HasValue && (height.Value <= 0 || height.Value > MediaConstraints.MaxImageHeight))
        {
            throw new DomainException($"Image height must be between 1 and {MediaConstraints.MaxImageHeight} pixels.");
        }

        if (durationMs.HasValue && durationMs.Value <= 0)
        {
            throw new DomainException("Media duration must be positive when specified.");
        }

        string? validatedThumbKey = null;
        string? validatedThumbMime = null;
        long? validatedThumbSize = null;
        if (!string.IsNullOrWhiteSpace(thumbnailObjectKey))
        {
            if (!thumbnailObjectKey.StartsWith(MediaConstraints.ThumbnailKeyPrefix, StringComparison.Ordinal))
            {
                throw new DomainException($"Thumbnail object key must start with '{MediaConstraints.ThumbnailKeyPrefix}'.");
            }

            var thumbSuffix = thumbnailObjectKey[MediaConstraints.ThumbnailKeyPrefix.Length..];
            if (!MediaConstraints.IsValidStorageObjectId(thumbSuffix))
            {
                throw new DomainException("Thumbnail object key must have a valid 32-character lowercase hexadecimal identifier.");
            }

            if (string.IsNullOrWhiteSpace(thumbnailContentType) || thumbnailContentType.Trim().Length > 64)
            {
                throw new DomainException("Thumbnail content type must be between 1 and 64 characters.");
            }

            if (!thumbnailSizeBytes.HasValue || thumbnailSizeBytes.Value <= 0 || thumbnailSizeBytes.Value > MediaConstraints.MaxChatImageThumbnailBytes)
            {
                throw new DomainException($"Thumbnail size must be between 1 and {MediaConstraints.MaxChatImageThumbnailBytes} bytes.");
            }

            validatedThumbKey = thumbnailObjectKey;
            validatedThumbMime = thumbnailContentType.Trim();
            validatedThumbSize = thumbnailSizeBytes.Value;
        }

        State = MediaState.Uploaded;
        ObjectKey = promotedObjectKey;
        ThumbnailObjectKey = validatedThumbKey;
        ThumbnailContentType = validatedThumbMime;
        ThumbnailSizeBytes = validatedThumbSize;
        VerifiedContentType = trimmedMime;
        VerifiedSizeBytes = verifiedSizeBytes;
        Width = width;
        Height = height;
        DurationMs = durationMs;
        UploadedAtUtc = nowUtc;
    }

    public void MarkAttached(MessageId messageId, int displayOrder, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(messageId);

        if (State != MediaState.Uploaded)
        {
            throw new DomainException($"Cannot attach media from state '{State}'.");
        }

        if (displayOrder < 0 || displayOrder >= MediaConstraints.MaxAttachmentsPerMessage)
        {
            throw new DomainException($"DisplayOrder must be between 0 and {MediaConstraints.MaxAttachmentsPerMessage - 1}.");
        }

        State = MediaState.Attached;
        MessageId = messageId;
        DisplayOrder = displayOrder;
        AttachedAtUtc = nowUtc;
    }

    public void MarkFailed(string reason, DateTimeOffset nowUtc, long? verifiedSizeBytes = null)
    {
        if (State != MediaState.Pending)
        {
            throw new DomainException($"Cannot mark media as failed from state '{State}'.");
        }

        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Verification failed."
            : reason.Trim();

        if (normalizedReason.Length > MediaConstraints.MaxFailureReasonLength)
        {
            normalizedReason = normalizedReason[..MediaConstraints.MaxFailureReasonLength];
        }

        if (verifiedSizeBytes.HasValue && verifiedSizeBytes.Value > 0)
        {
            VerifiedSizeBytes = verifiedSizeBytes.Value;
        }

        State = MediaState.Failed;
        FailureReason = normalizedReason;
        _ = nowUtc;
    }

    public void MarkDeleted(DateTimeOffset nowUtc)
    {
        if (State == MediaState.Deleted)
        {
            return;
        }

        if (State == MediaState.Failed)
        {
            throw new DomainException("Cannot mark failed media as deleted.");
        }

        State = MediaState.Deleted;
        DeletedAtUtc = nowUtc;
    }

    public void MarkPurged(DateTimeOffset nowUtc)
    {
        if (State is not (MediaState.Failed or MediaState.Deleted))
        {
            throw new DomainException($"Cannot mark media as purged from state '{State}'.");
        }

        PurgedAtUtc = nowUtc;
    }
}
