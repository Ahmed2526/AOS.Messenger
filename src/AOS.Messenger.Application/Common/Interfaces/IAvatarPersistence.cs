using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public sealed record AvatarCommitResult(
    bool Applied,
    string? DisplayName,
    string? CurrentAvatarVersion,
    string? PreviousFullObjectKey,
    string? PreviousThumbObjectKey);

public sealed record AvatarRemoveResult(
    bool HadAvatar,
    string? DisplayName,
    string? PreviousFullObjectKey,
    string? PreviousThumbObjectKey);

public sealed record UserAvatarMetadata(
    bool IsDeleted,
    bool EmailConfirmed,
    string? FullObjectKey,
    string? ThumbObjectKey,
    string? ContentType,
    string? Version);

/// <summary>
/// Persistence contract for staged avatar uploads, atomic dual-variant profile commits,
/// and avatar authorization metadata queries.
/// </summary>
public interface IAvatarPersistence
{
    Task<PendingAvatarUpload> TryCreatePendingWithQuotaCheckAsync(
        PendingAvatarUpload upload,
        int maxConcurrentPending,
        long dailyQuotaBytes,
        DateTimeOffset dailyQuotaWindowStartUtc,
        CancellationToken ct = default);

    Task<PendingAvatarUpload?> GetPendingByIdAsync(AvatarUploadId id, CancellationToken ct = default);

    Task<PendingAvatarUpload?> GetPendingByIdNoTrackingAsync(AvatarUploadId id, CancellationToken ct = default);

    Task UpdatePendingAsync(PendingAvatarUpload upload, CancellationToken ct = default);

    /// <remarks>
    /// NotFoundException and ConflictException indicate rejection before commit was attempted.
    /// Other exceptions may leave the outcome unknown; callers must retain the new objects.
    /// </remarks>
    Task<AvatarCommitResult> CommitUserAvatarAsync(
        PendingAvatarUpload upload,
        string fullObjectKey,
        string thumbObjectKey,
        string contentType,
        long fullSizeBytes,
        long thumbSizeBytes,
        string avatarVersion,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);

    Task<AvatarRemoveResult> RemoveUserAvatarAsync(
        UserId userId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);

    Task<UserAvatarMetadata?> GetUserAvatarMetadataAsync(UserId targetUserId, CancellationToken ct = default);

    Task<IReadOnlyList<UserId>> GetNotifyPeerUserIdsAsync(UserId userId, CancellationToken ct = default);
}
