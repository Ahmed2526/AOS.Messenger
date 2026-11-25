using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Application use-case contract for profile photo staging upload, dual-variant verification,
/// removal, and thumbnail/full URL resolution.
/// </summary>
public interface IAvatarService
{
    Task<InitiateAvatarUploadResponseDto> InitiateUploadAsync(
        UserId callerId,
        InitiateAvatarUploadRequestDto dto,
        CancellationToken ct = default);

    Task<AvatarUpdatedResponseDto> CompleteUploadAsync(
        UserId callerId,
        AvatarUploadId uploadId,
        CancellationToken ct = default);

    Task RemoveAvatarAsync(
        UserId callerId,
        CancellationToken ct = default);

    Task<AvatarUrlDto> GetAvatarUrlAsync(
        UserId callerId,
        UserId targetUserId,
        AvatarSizeVariant variant = AvatarSizeVariant.Thumbnail,
        CancellationToken ct = default);
}
