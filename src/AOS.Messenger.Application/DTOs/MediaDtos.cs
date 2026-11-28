using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.DTOs;

/// <summary>
/// Client upload initiation request. For voice messages (<c>Kind == "voice"</c>), <see cref="DurationMs"/> is
/// client-declared advisory UI metadata bounded in domain/SQL ([500, 1_200_000]); V1 server admission relies
/// on the 25 MB size cap, daily upload quota, and bounded container verification rather than full audio decoding.
/// </summary>
public sealed record InitiateMediaUploadRequestDto(
    string FileName,
    string? ContentType,
    long SizeBytes,
    string? Kind = null,
    int? DurationMs = null,
    IReadOnlyList<int>? WaveformPeaks = null);

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
    int DisplayOrder,
    bool HasThumbnail = false,
    IReadOnlyList<int>? WaveformPeaks = null,
    DateTimeOffset? PlayedAtUtc = null);

public sealed record VoicePlayedDto(
    Guid ConversationId,
    long MessageId,
    Guid MediaId,
    Guid PlayedByUserId,
    DateTimeOffset PlayedAtUtc);

public sealed record MediaDownloadUrlDto(
    string Url,
    DateTimeOffset ExpiresAtUtc,
    string FileName,
    string ContentType,
    long SizeBytes);
