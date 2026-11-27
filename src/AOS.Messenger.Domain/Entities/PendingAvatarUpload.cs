using AOS.Messenger.Domain.Common;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Domain.Entities;

/// <summary>
/// Tracks staged user avatar uploads prior to server-side Magick.NET verification and dual-variant promotion.
/// </summary>
public sealed class PendingAvatarUpload : Entity<AvatarUploadId>
{
    public UserId UserId { get; private set; } = null!;
    public string StorageObjectId { get; private set; } = null!;
    public string ObjectKey { get; private set; } = null!;
    public MediaState State { get; private set; }
    public string DeclaredContentType { get; private set; } = null!;
    public long DeclaredSizeBytes { get; private set; }
    public long? VerifiedSizeBytes { get; private set; }
    public string? FailureReason { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset? PurgedAtUtc { get; private set; }
    public byte[] RowVersion { get; private set; } = [];

    private PendingAvatarUpload()
    {
    }

    public static PendingAvatarUpload CreatePending(
        UserId userId,
        string declaredContentType,
        long declaredSizeBytes,
        DateTimeOffset nowUtc,
        string? storageObjectId = null,
        AvatarUploadId? id = null)
    {
        ArgumentNullException.ThrowIfNull(userId);

        if (string.IsNullOrWhiteSpace(declaredContentType))
        {
            throw new DomainException("Declared content type cannot be empty.");
        }

        var trimmedMime = declaredContentType.Trim();
        if (trimmedMime.Length > 128)
        {
            throw new DomainException("Declared content type cannot exceed 128 characters.");
        }

        if (declaredSizeBytes <= 0 || declaredSizeBytes > AvatarConstraints.MaxRawAvatarUploadBytes)
        {
            throw new DomainException(
                $"Profile photo size must be between 1 byte and {AvatarConstraints.MaxRawAvatarUploadBytes} bytes.");
        }

        var objId = storageObjectId ?? MediaConstraints.GenerateStorageObjectId();
        if (!MediaConstraints.IsValidStorageObjectId(objId))
        {
            throw new DomainException("StorageObjectId must be a 32-character lowercase hexadecimal string.");
        }

        return new PendingAvatarUpload
        {
            Id = id!,
            UserId = userId,
            StorageObjectId = objId,
            ObjectKey = $"{MediaConstraints.PendingKeyPrefix}{objId}",
            State = MediaState.Pending,
            DeclaredContentType = trimmedMime,
            DeclaredSizeBytes = declaredSizeBytes,
            CreatedAtUtc = nowUtc,
            RowVersion = []
        };
    }

    public void EnsureCreatedAfter(DateTimeOffset minExclusiveUtc)
    {
        if (CreatedAtUtc <= minExclusiveUtc)
        {
            CreatedAtUtc = minExclusiveUtc.AddTicks(1);
        }
    }

    public void MarkCompleted(long verifiedSizeBytes, DateTimeOffset nowUtc)
    {
        if (State != MediaState.Pending)
        {
            throw new DomainException($"Cannot mark avatar upload completed from state '{State}'.");
        }

        if (verifiedSizeBytes <= 0 || verifiedSizeBytes > AvatarConstraints.MaxRawAvatarUploadBytes)
        {
            throw new DomainException(
                $"Verified profile photo size must be between 1 byte and {AvatarConstraints.MaxRawAvatarUploadBytes} bytes.");
        }

        State = MediaState.Uploaded;
        VerifiedSizeBytes = verifiedSizeBytes;
        CompletedAtUtc = nowUtc;
    }

    public void MarkFailed(string reason, DateTimeOffset nowUtc, long? verifiedSizeBytes = null)
    {
        if (State != MediaState.Pending)
        {
            throw new DomainException($"Cannot mark avatar upload failed from state '{State}'.");
        }

        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "Avatar verification failed."
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

    public void MarkPurged(DateTimeOffset nowUtc)
    {
        if (State is not (MediaState.Failed or MediaState.Deleted))
        {
            throw new DomainException($"Cannot mark avatar upload purged from state '{State}'.");
        }

        PurgedAtUtc = nowUtc;
    }
}
