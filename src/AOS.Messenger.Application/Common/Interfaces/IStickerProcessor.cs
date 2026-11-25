using AOS.Messenger.Domain.Entities;

namespace AOS.Messenger.Application.Common.Interfaces;

public sealed class ProcessedStickerResult : IDisposable, IAsyncDisposable
{
    public string ContentType { get; }
    public long VerifiedRawSizeBytes { get; }
    public Stream ProcessedStream { get; }
    public long ProcessedSizeBytes { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsAnimated { get; }

    public ProcessedStickerResult(
        string contentType,
        long verifiedRawSizeBytes,
        Stream processedStream,
        long processedSizeBytes,
        int width,
        int height,
        bool isAnimated)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(processedStream);

        ContentType = contentType;
        VerifiedRawSizeBytes = verifiedRawSizeBytes;
        ProcessedStream = processedStream;
        ProcessedSizeBytes = processedSizeBytes;
        Width = width;
        Height = height;
        IsAnimated = isAnimated;
    }

    public void Dispose()
    {
        ProcessedStream.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        return ProcessedStream.DisposeAsync();
    }
}

/// <summary>
/// Validates staged custom sticker uploads via Magick.NET and produces stripped, normalized
/// 512x512 static or animated transparent WebP streams.
/// </summary>
public interface IStickerProcessor
{
    Task<ProcessedStickerResult> ProcessStickerAsync(
        Sticker sticker,
        MediaObjectMetadata stagingMetadata,
        CancellationToken ct = default);
}
