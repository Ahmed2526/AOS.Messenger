using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Represents a reusable built-in or custom R2 WebP sticker asset (512x512 static or animated).
/// </summary>
public sealed class Sticker : Entity<StickerId>
{
    public StickerPackId PackId { get; private set; } = null!;
    public UserId? UploaderId { get; private set; }
    public StickerState State { get; private set; }
    public string? StorageObjectId { get; private set; }
    public string? ObjectKey { get; private set; }
    public string? StaticAssetPath { get; private set; }
    public string ContentType { get; private set; } = string.Empty;
    public long DeclaredSizeBytes { get; private set; }
    public long? VerifiedSizeBytes { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public bool IsAnimated { get; private set; }
    public string EmojiTags { get; private set; } = string.Empty;
    public int DisplayOrder { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? UploadedAtUtc { get; private set; }
    public DateTimeOffset? DeletedAtUtc { get; private set; }
    public DateTimeOffset? PurgedAtUtc { get; private set; }
    public string? FailureReason { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private Sticker()
    {
    }

    public static Sticker CreateSystem(
        StickerPackId packId,
        string staticAssetPath,
        string emojiTags,
        int displayOrder,
        long fileSizeBytes,
        DateTimeOffset nowUtc,
        bool isAnimated = false,
        StickerId? id = null)
    {
        ArgumentNullException.ThrowIfNull(packId);
        if (!StickerConstraints.IsValidStaticAssetPath(staticAssetPath))
        {
            throw new DomainException($"Invalid system sticker static asset path '{staticAssetPath}'.");
        }

        if (fileSizeBytes <= 0)
        {
            throw new DomainException("Sticker asset size must be positive.");
        }

        return new Sticker
        {
            Id = id!,
            PackId = packId,
            UploaderId = null,
            State = StickerState.Ready,
            StorageObjectId = null,
            ObjectKey = null,
            StaticAssetPath = staticAssetPath,
            ContentType = StickerConstraints.CanonicalStickerContentType,
            DeclaredSizeBytes = fileSizeBytes,
            VerifiedSizeBytes = fileSizeBytes,
            Width = StickerConstraints.CanonicalStickerSize,
            Height = StickerConstraints.CanonicalStickerSize,
            IsAnimated = isAnimated,
            EmojiTags = StickerConstraints.NormalizeEmojiTags(emojiTags),
            DisplayOrder = displayOrder,
            CreatedAtUtc = nowUtc,
            UploadedAtUtc = nowUtc,
            RowVersion = []
        };
    }

    public static Sticker CreatePendingCustom(
        StickerPackId packId,
        UserId uploaderId,
        string declaredContentType,
        long declaredSizeBytes,
        string? emojiTags,
        int displayOrder,
        DateTimeOffset nowUtc,
        StickerId? id = null)
    {
        ArgumentNullException.ThrowIfNull(packId);
        ArgumentNullException.ThrowIfNull(uploaderId);

        if (string.IsNullOrWhiteSpace(declaredContentType))
        {
            throw new DomainException("Content type cannot be empty.");
        }

        if (declaredSizeBytes <= 0 || declaredSizeBytes > StickerConstraints.MaxRawStickerUploadBytes)
        {
            throw new DomainException(
                $"Sticker upload size must be between 1 byte and {StickerConstraints.MaxRawStickerUploadBytes} bytes.");
        }

        var objectId = MediaConstraints.GenerateStorageObjectId();
        return new Sticker
        {
            Id = id!,
            PackId = packId,
            UploaderId = uploaderId,
            State = StickerState.Pending,
            StorageObjectId = objectId,
            ObjectKey = $"{MediaConstraints.PendingKeyPrefix}{objectId}",
            StaticAssetPath = null,
            ContentType = declaredContentType.Trim(),
            DeclaredSizeBytes = declaredSizeBytes,
            Width = StickerConstraints.CanonicalStickerSize,
            Height = StickerConstraints.CanonicalStickerSize,
            IsAnimated = false,
            EmojiTags = StickerConstraints.NormalizeEmojiTags(emojiTags),
            DisplayOrder = displayOrder,
            CreatedAtUtc = nowUtc,
            RowVersion = []
        };
    }

    public void UpdateSystemMetadata(string emojiTags, int displayOrder, long fileSizeBytes, bool isAnimated)
    {
        if (UploaderId is not null)
        {
            throw new DomainException("Cannot update system metadata on a custom sticker.");
        }

        if (fileSizeBytes <= 0)
        {
            throw new DomainException("Sticker asset size must be positive.");
        }

        EmojiTags = StickerConstraints.NormalizeEmojiTags(emojiTags);
        DisplayOrder = displayOrder;
        DeclaredSizeBytes = fileSizeBytes;
        VerifiedSizeBytes = fileSizeBytes;
        IsAnimated = isAnimated;
        State = StickerState.Ready;
    }

    public void MarkReady(
        string permanentObjectId,
        string permanentObjectKey,
        long verifiedSizeBytes,
        int width,
        int height,
        bool isAnimated,
        DateTimeOffset nowUtc)
    {
        if (UploaderId is null)
        {
            throw new DomainException("System stickers cannot transition through custom upload completion.");
        }

        if (State != StickerState.Pending)
        {
            throw new DomainException($"Cannot mark sticker ready from state '{State}'.");
        }

        if (!MediaConstraints.IsValidStorageObjectId(permanentObjectId)
            || string.IsNullOrWhiteSpace(permanentObjectKey)
            || !permanentObjectKey.StartsWith(MediaConstraints.StickerKeyPrefix, StringComparison.Ordinal))
        {
            throw new DomainException("Invalid permanent sticker storage key.");
        }

        if (verifiedSizeBytes <= 0)
        {
            throw new DomainException("Verified sticker size must be positive.");
        }

        State = StickerState.Ready;
        StorageObjectId = permanentObjectId;
        ObjectKey = permanentObjectKey;
        ContentType = StickerConstraints.CanonicalStickerContentType;
        VerifiedSizeBytes = verifiedSizeBytes;
        Width = width;
        Height = height;
        IsAnimated = isAnimated;
        UploadedAtUtc = nowUtc;
        FailureReason = null;
    }

    public void MarkFailed(string failureReason, DateTimeOffset nowUtc, long? verifiedSizeBytes = null)
    {
        if (UploaderId is null)
        {
            throw new DomainException("System stickers cannot fail.");
        }

        if (State != StickerState.Pending)
        {
            return;
        }

        var trimmed = string.IsNullOrWhiteSpace(failureReason)
            ? "Sticker verification failed."
            : failureReason.Trim();
        if (trimmed.Length > MediaConstraints.MaxFailureReasonLength)
        {
            trimmed = trimmed[..MediaConstraints.MaxFailureReasonLength];
        }

        State = StickerState.Failed;
        FailureReason = trimmed;
        DeletedAtUtc = nowUtc;
        if (verifiedSizeBytes is > 0)
        {
            VerifiedSizeBytes = verifiedSizeBytes;
        }
    }

    public void MarkArchived(DateTimeOffset nowUtc)
    {
        if (UploaderId is null)
        {
            throw new DomainException("Built-in starter stickers cannot be deleted.");
        }

        if (State == StickerState.Archived)
        {
            return;
        }

        if (State != StickerState.Ready)
        {
            throw new DomainException($"Only ready custom stickers can be archived cleanly from '{State}'.");
        }

        State = StickerState.Archived;
        DeletedAtUtc = nowUtc;
    }

    public void MarkPurged(DateTimeOffset purgedAtUtc)
    {
        PurgedAtUtc = purgedAtUtc;
    }
}
