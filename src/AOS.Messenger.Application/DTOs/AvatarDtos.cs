using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.DTOs;

public sealed record InitiateAvatarUploadRequestDto(
    string FileName,
    string? ContentType,
    long SizeBytes);

public sealed record InitiateAvatarUploadResponseDto(
    AvatarUploadId UploadId,
    string UploadUrl,
    DateTimeOffset ExpiresAtUtc,
    string RequiredContentType,
    long RequiredContentLength);

public sealed record AvatarUpdatedResponseDto(
    string? AvatarVersion,
    DateTimeOffset UpdatedAtUtc);

public sealed record AvatarUrlDto(
    string Url,
    DateTimeOffset ExpiresAtUtc,
    string Version,
    string Size);

public sealed record UserProfileUpdatedDto(
    UserId UserId,
    string DisplayName,
    string? AvatarVersion);
