using AOS.Messenger.Domain.Entities;

namespace AOS.Messenger.Application.Common.Interfaces;

public sealed class ProcessedAvatarResult : IDisposable, IAsyncDisposable
{
    public string ContentType { get; }
    public long VerifiedRawSizeBytes { get; }
    public Stream FullStream { get; }
    public long FullSizeBytes { get; }
    public Stream ThumbnailStream { get; }
    public long ThumbnailSizeBytes { get; }

    public ProcessedAvatarResult(
        string contentType,
        long verifiedRawSizeBytes,
        Stream fullStream,
        long fullSizeBytes,
        Stream thumbnailStream,
        long thumbnailSizeBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(fullStream);
        ArgumentNullException.ThrowIfNull(thumbnailStream);

        ContentType = contentType;
        VerifiedRawSizeBytes = verifiedRawSizeBytes;
        FullStream = fullStream;
        FullSizeBytes = fullSizeBytes;
        ThumbnailStream = thumbnailStream;
        ThumbnailSizeBytes = thumbnailSizeBytes;
    }

    public void Dispose()
    {
        FullStream.Dispose();
        ThumbnailStream.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await FullStream.DisposeAsync();
        await ThumbnailStream.DisposeAsync();
    }
}

/// <summary>
/// Validates staged avatar uploads via Magick.NET and produces stripped WebP full and 128x128 thumbnail streams.
/// </summary>
public interface IAvatarProcessor
{
    Task<ProcessedAvatarResult> ProcessAvatarAsync(
        PendingAvatarUpload upload,
        MediaObjectMetadata stagingMetadata,
        CancellationToken ct = default);
}
