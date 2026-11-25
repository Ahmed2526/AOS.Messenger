using AOS.Messenger.Domain.Entities;

namespace AOS.Messenger.Application.Common.Interfaces;

public sealed class VerifiedMediaResult : IDisposable, IAsyncDisposable
{
    public string VerifiedContentType { get; }
    public long VerifiedSizeBytes { get; }
    public int? Width { get; }
    public int? Height { get; }
    public int? DurationMs { get; }
    public Stream? ProcessedStream { get; }
    public Stream? ThumbnailStream { get; }
    public string? ThumbnailContentType { get; }
    public long? ThumbnailSizeBytes { get; }

    public VerifiedMediaResult(
        string verifiedContentType,
        long verifiedSizeBytes,
        int? width = null,
        int? height = null,
        int? durationMs = null,
        Stream? processedStream = null,
        Stream? thumbnailStream = null,
        string? thumbnailContentType = null,
        long? thumbnailSizeBytes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedContentType);

        VerifiedContentType = verifiedContentType;
        VerifiedSizeBytes = verifiedSizeBytes;
        Width = width;
        Height = height;
        DurationMs = durationMs;
        ProcessedStream = processedStream;
        ThumbnailStream = thumbnailStream;
        ThumbnailContentType = thumbnailContentType;
        ThumbnailSizeBytes = thumbnailSizeBytes;
    }

    public void Dispose()
    {
        ProcessedStream?.Dispose();
        ThumbnailStream?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (ProcessedStream is not null)
        {
            await ProcessedStream.DisposeAsync();
        }

        if (ThumbnailStream is not null)
        {
            await ThumbnailStream.DisposeAsync();
        }
    }
}

/// <summary>
/// Validates staged object magic bytes / decoded structure and re-encodes images cleanly to strip metadata.
/// </summary>
public interface IMediaVerifier
{
    Task<VerifiedMediaResult> VerifyAndSanitizeAsync(
        Media media,
        MediaObjectMetadata stagingMetadata,
        CancellationToken ct = default);
}
