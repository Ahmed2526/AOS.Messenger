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

    public VerifiedMediaResult(
        string verifiedContentType,
        long verifiedSizeBytes,
        int? width = null,
        int? height = null,
        int? durationMs = null,
        Stream? processedStream = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedContentType);

        VerifiedContentType = verifiedContentType;
        VerifiedSizeBytes = verifiedSizeBytes;
        Width = width;
        Height = height;
        DurationMs = durationMs;
        ProcessedStream = processedStream;
    }

    public void Dispose()
    {
        ProcessedStream?.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return ProcessedStream is not null
            ? ProcessedStream.DisposeAsync()
            : ValueTask.CompletedTask;
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
