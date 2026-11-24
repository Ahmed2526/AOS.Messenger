using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.DTOs;

public sealed record InitiateMediaUploadRequestDto(
    string FileName,
    string? ContentType,
    long SizeBytes);

public sealed record InitiateMediaUploadResponseDto(
    MediaId MediaId,
    string UploadUrl,
    DateTimeOffset ExpiresAtUtc,
    string RequiredContentType,
    long RequiredContentLength,
    string SanitizedFileName,
    string Kind);

public sealed record MediaAttachmentDto(
    MediaId Id,
    string Kind,
    string FileName,
    string ContentType,
    long SizeBytes,
    int? Width,
    int? Height,
    int? DurationMs,
    int DisplayOrder);

public sealed record MediaDownloadUrlDto(
    string Url,
    DateTimeOffset ExpiresAtUtc,
    string FileName,
    string ContentType,
    long SizeBytes);
