using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Exceptions;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Infrastructure.Storage;

/// <summary>
/// Validates staged custom sticker images via strict magic-byte sniffing, container animation bounds,
/// and <c>Magick.NET</c>, always re-encoding into EXIF-stripped 512x512 static or animated transparent WebP.
/// </summary>
public sealed class StickerProcessor : IStickerProcessor, IDisposable
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly IMediaStorage _storage;
    private readonly ImageProcessingGate _gate;
    private readonly bool _ownsGate;
    private readonly ILogger<StickerProcessor> _logger;

    [ActivatorUtilitiesConstructor]
    public StickerProcessor(
        IMediaStorage storage,
        ImageProcessingGate gate,
        ILogger<StickerProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(gate);

        _storage = storage;
        _gate = gate;
        _ownsGate = false;
        _logger = logger ?? NullLogger<StickerProcessor>.Instance;
    }

    internal StickerProcessor(
        IMediaStorage storage,
        IOptions<MediaOptions> options,
        ILogger<StickerProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);

        _storage = storage;
        _gate = new ImageProcessingGate(options);
        _ownsGate = true;
        _logger = logger ?? NullLogger<StickerProcessor>.Instance;
    }

    public async Task<ProcessedStickerResult> ProcessStickerAsync(
        Sticker sticker,
        MediaObjectMetadata stagingMetadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sticker);
        ArgumentNullException.ThrowIfNull(stagingMetadata);

        if (string.IsNullOrWhiteSpace(sticker.ObjectKey))
        {
            throw new DomainException("Custom sticker staging object key is missing.");
        }

        if (stagingMetadata.ContentLength <= 0 || stagingMetadata.ContentLength > StickerConstraints.MaxRawStickerUploadBytes)
        {
            throw new DomainException(
                $"Sticker upload size must be between 1 byte and {StickerConstraints.MaxRawStickerUploadBytes} bytes.");
        }

        await using var inputStream = CreateDeleteOnCloseTempStream();
        await _storage.DownloadToStreamAsync(
            sticker.ObjectKey,
            inputStream,
            StickerConstraints.MaxRawStickerUploadBytes,
            stagingMetadata.ETag,
            ct);
        await inputStream.FlushAsync(ct);

        if (inputStream.Length <= 0)
        {
            throw new DomainException("Uploaded sticker image is empty.");
        }

        var (sniffedMime, pinnedFormat) = SniffAllowedStickerSignature(inputStream);
        if (!string.Equals(sniffedMime, sticker.ContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(
                $"Sticker content '{sniffedMime}' does not match expected type '{sticker.ContentType}'.");
        }

        var scan = pinnedFormat switch
        {
            MagickFormat.Gif => ImageFrameCounter.ScanGif(
                inputStream,
                StickerConstraints.MaxAnimationFrames,
                StickerConstraints.MaxAnimationTotalPixels),
            MagickFormat.WebP => ImageFrameCounter.ScanWebp(
                inputStream,
                StickerConstraints.MaxAnimationFrames,
                StickerConstraints.MaxAnimationTotalPixels),
            _ => new AnimationScanResult(1, 0, 0, 0)
        };

        if (scan.FrameCount > 1)
        {
            if (scan.MaxWidth > StickerConstraints.MaxAnimationFrameDimension
                || scan.MaxHeight > StickerConstraints.MaxAnimationFrameDimension)
            {
                throw new DomainException(
                    $"Animated sticker frames must not exceed {StickerConstraints.MaxAnimationFrameDimension}x{StickerConstraints.MaxAnimationFrameDimension} pixels.");
            }

            if (scan.TotalPixels > StickerConstraints.MaxAnimationTotalPixels
                || ExceedsTotalPixelBudget(scan.MaxWidth, scan.MaxHeight, scan.FrameCount, StickerConstraints.MaxAnimationTotalPixels))
            {
                throw new DomainException("Animated sticker exceeds maximum decoded pixel budget.");
            }
        }

        var acquired = await _gate.Semaphore.WaitAsync(_gate.Timeout, ct);
        if (!acquired)
        {
            throw new ConflictException("Image processing capacity is currently busy. Please try again.");
        }

        FileStream? outputStream = null;
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        jobCts.CancelAfter(_gate.Timeout);
        try
        {
            outputStream = CreateDeleteOnCloseTempStream();

            var isAnimated = await Task.Run(
                () => ProcessStickerLocked(inputStream, outputStream, pinnedFormat, scan.FrameCount, jobCts.Token),
                jobCts.Token);

            await outputStream.FlushAsync(jobCts.Token);
            outputStream.Seek(0, SeekOrigin.Begin);

            var ownedOutput = outputStream;
            outputStream = null;

            return new ProcessedStickerResult(
                StickerConstraints.CanonicalStickerContentType,
                inputStream.Length,
                ownedOutput,
                ownedOutput.Length,
                StickerConstraints.CanonicalStickerSize,
                StickerConstraints.CanonicalStickerSize,
                isAnimated);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && jobCts.IsCancellationRequested)
        {
            _logger.LogWarning("Sticker processing timed out for sticker {StickerId}.", sticker.Id?.Value);
            throw new ConflictException("Image processing capacity is currently busy. Please try again.");
        }
        catch (MagickException ex)
        {
            _logger.LogWarning("Rejected invalid sticker image for sticker {StickerId}: {ErrorType}", sticker.Id?.Value, ex.GetType().Name);
            throw new DomainException("The uploaded file is not a valid or supported sticker image.");
        }
        finally
        {
            if (outputStream is not null)
            {
                await outputStream.DisposeAsync();
            }

            _gate.Semaphore.Release();
        }
    }

    public void Dispose()
    {
        if (_ownsGate)
        {
            _gate.Dispose();
        }
    }

    private bool ProcessStickerLocked(
        FileStream inputStream,
        FileStream outputStream,
        MagickFormat pinnedFormat,
        int scannedFrameCount,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var pingSettings = new MagickReadSettings { Format = pinnedFormat };
        var headersValidated = false;
        inputStream.Seek(0, SeekOrigin.Begin);
        var info = new MagickImageInfo(inputStream, pingSettings);
        var canvasWidth = info.Width;
        var canvasHeight = info.Height;
        ValidateImageDimensions(canvasWidth, canvasHeight);

        if (scannedFrameCount > 1)
        {
            if (canvasWidth > (ulong)StickerConstraints.MaxAnimationFrameDimension
                || canvasHeight > (ulong)StickerConstraints.MaxAnimationFrameDimension)
            {
                throw new DomainException(
                    $"Animated sticker frames must not exceed {StickerConstraints.MaxAnimationFrameDimension}x{StickerConstraints.MaxAnimationFrameDimension} pixels.");
            }

            if (ExceedsTotalPixelBudget((int)canvasWidth, (int)canvasHeight, scannedFrameCount, StickerConstraints.MaxAnimationTotalPixels))
            {
                throw new DomainException("Animated sticker exceeds maximum decoded pixel budget.");
            }
        }

        headersValidated = true;
        ct.ThrowIfCancellationRequested();
        inputStream.Seek(0, SeekOrigin.Begin);

        try
        {
            if (pinnedFormat is MagickFormat.Gif or MagickFormat.WebP)
            {
                var collectionSettings = new MagickReadSettings
                {
                    Format = pinnedFormat,
                    FrameIndex = 0,
                    FrameCount = (uint)(StickerConstraints.MaxAnimationFrames + 1)
                };

                using var collection = new MagickImageCollection(inputStream, collectionSettings);
                if (collection.Count == 0)
                {
                    throw new DomainException("Uploaded sticker contains no image frames.");
                }

                if (collection.Count > StickerConstraints.MaxAnimationFrames)
                {
                    throw new DomainException(
                        $"Animated stickers cannot exceed {StickerConstraints.MaxAnimationFrames} frames.");
                }

                if (collection.Count == 1)
                {
                    using var singleImage = new MagickImage(collection[0]);
                    ProcessStaticSticker(singleImage, outputStream, ct);
                    return false;
                }

                ProcessAnimatedSticker(collection, (int)canvasWidth, (int)canvasHeight, outputStream, ct);
                return true;
            }

            var staticSettings = new MagickReadSettings { Format = pinnedFormat };
            using var image = new MagickImage(inputStream, staticSettings);
            ProcessStaticSticker(image, outputStream, ct);
            return false;
        }
        catch (MagickException ex) when (MediaVerifier.IsTransientResourceExhaustion(ex, headersValidated))
        {
            _logger.LogWarning("Transient ImageMagick resource limit hit while processing sticker: {ErrorType}", ex.GetType().Name);
            throw new ConflictException("Image processing capacity is currently busy. Please try again.");
        }
    }

    private static void ProcessStaticSticker(
        IMagickImage<byte> image,
        FileStream outputStream,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        image.AutoOrient();
        ValidateImageDimensions(image.Width, image.Height);
        StripSingleImageMetadata(image);

        NormalizeToCanonicalCanvas(image);

        EncodeStaticWebpWithinLimit(
            image,
            outputStream,
            [85u, 72u, 60u],
            StickerConstraints.MaxStaticWebpOutputBytes,
            ct);
    }

    private static void ProcessAnimatedSticker(
        MagickImageCollection collection,
        int canvasWidth,
        int canvasHeight,
        FileStream outputStream,
        CancellationToken ct)
    {
        if (canvasWidth <= 0 || canvasHeight <= 0
            || canvasWidth > StickerConstraints.MaxAnimationFrameDimension
            || canvasHeight > StickerConstraints.MaxAnimationFrameDimension)
        {
            throw new DomainException(
                $"Animated sticker frames must not exceed {StickerConstraints.MaxAnimationFrameDimension}x{StickerConstraints.MaxAnimationFrameDimension} pixels.");
        }

        var maxCanvasWidth = canvasWidth;
        var maxCanvasHeight = canvasHeight;
        long totalDecodedPixels = 0;

        foreach (var frame in collection)
        {
            ct.ThrowIfCancellationRequested();
            if (frame.Width == 0 || frame.Height == 0
                || frame.Width > (uint)StickerConstraints.MaxAnimationFrameDimension
                || frame.Height > (uint)StickerConstraints.MaxAnimationFrameDimension
                || frame.Page.Width > (uint)StickerConstraints.MaxAnimationFrameDimension
                || frame.Page.Height > (uint)StickerConstraints.MaxAnimationFrameDimension)
            {
                throw new DomainException(
                    $"Animated sticker frames must not exceed {StickerConstraints.MaxAnimationFrameDimension}x{StickerConstraints.MaxAnimationFrameDimension} pixels.");
            }

            var w = (int)frame.Width;
            var h = (int)frame.Height;
            var pageW = frame.Page.Width > 0 ? (int)frame.Page.Width : w;
            var pageH = frame.Page.Height > 0 ? (int)frame.Page.Height : h;

            maxCanvasWidth = Math.Max(maxCanvasWidth, Math.Max(w, pageW));
            maxCanvasHeight = Math.Max(maxCanvasHeight, Math.Max(h, pageH));

            totalDecodedPixels += (long)w * h;
            if (totalDecodedPixels > StickerConstraints.MaxAnimationTotalPixels)
            {
                throw new DomainException("Animated sticker exceeds maximum decoded pixel budget.");
            }
        }

        if (ExceedsTotalPixelBudget(maxCanvasWidth, maxCanvasHeight, collection.Count, StickerConstraints.MaxAnimationTotalPixels))
        {
            throw new DomainException("Animated sticker exceeds maximum decoded pixel budget.");
        }

        collection.Coalesce();

        long totalDurationMs = 0;
        foreach (var frame in collection)
        {
            ct.ThrowIfCancellationRequested();
            StripSingleImageMetadata(frame);

            var ticksPerSec = frame.AnimationTicksPerSecond > 0 ? frame.AnimationTicksPerSecond : 100;
            var rawDelayMs = (long)Math.Round(frame.AnimationDelay * 1000.0 / ticksPerSec);
            var clampedDelayMs = rawDelayMs < StickerConstraints.MinRawDelayThresholdMs
                ? StickerConstraints.MinFrameDelayMs
                : rawDelayMs;

            totalDurationMs += clampedDelayMs;
            if (totalDurationMs > StickerConstraints.MaxAnimationDurationMs)
            {
                throw new DomainException(
                    $"Animated sticker duration cannot exceed {StickerConstraints.MaxAnimationDurationMs / 1000} seconds.");
            }

            frame.AnimationTicksPerSecond = 100;
            frame.AnimationDelay = (uint)Math.Max(1, Math.Round(clampedDelayMs / 10.0));
            frame.AnimationIterations = 0;

            NormalizeToCanonicalCanvas(frame);
            frame.GifDisposeMethod = GifDisposeMethod.Background;
        }

        collection.OptimizeTransparency();

        EncodeAnimatedWebpWithinLimit(
            collection,
            outputStream,
            [80u, 65u, 50u],
            StickerConstraints.MaxAnimatedWebpOutputBytes,
            ct);
    }

    private static void NormalizeToCanonicalCanvas(IMagickImage<byte> image)
    {
        var size = (uint)StickerConstraints.CanonicalStickerSize;
        image.BackgroundColor = MagickColors.Transparent;
        image.FilterType = FilterType.Lanczos;
        image.Resize(new MagickGeometry(size, size) { IgnoreAspectRatio = false });
        image.Extent(size, size, Gravity.Center, MagickColors.Transparent);
        image.ResetPage();
    }

    private static void EncodeStaticWebpWithinLimit(
        IMagickImage<byte> image,
        FileStream outputStream,
        uint[] qualities,
        long maxBytes,
        CancellationToken ct)
    {
        foreach (var quality in qualities)
        {
            ct.ThrowIfCancellationRequested();
            outputStream.SetLength(0);
            outputStream.Seek(0, SeekOrigin.Begin);
            image.Quality = quality;
            image.Write(outputStream, MagickFormat.WebP);

            if (outputStream.Length > 0 && outputStream.Length <= maxBytes)
            {
                return;
            }
        }

        throw new DomainException($"Processed sticker exceeds the maximum allowed {maxBytes} bytes.");
    }

    private static void EncodeAnimatedWebpWithinLimit(
        MagickImageCollection collection,
        FileStream outputStream,
        uint[] qualities,
        long maxBytes,
        CancellationToken ct)
    {
        foreach (var quality in qualities)
        {
            ct.ThrowIfCancellationRequested();
            outputStream.SetLength(0);
            outputStream.Seek(0, SeekOrigin.Begin);

            foreach (var frame in collection)
            {
                frame.Quality = quality;
            }

            collection.Write(outputStream, MagickFormat.WebP);

            if (outputStream.Length > 0 && outputStream.Length <= maxBytes)
            {
                return;
            }
        }

        throw new DomainException($"Processed animated sticker exceeds the maximum allowed {maxBytes} bytes.");
    }

    private static void StripSingleImageMetadata(IMagickImage<byte> image)
    {
        image.Strip();
        image.Comment = null;
        image.RemoveAttribute("comment");
        image.RemoveAttribute("exif");
        image.RemoveAttribute("xmp");
        image.RemoveAttribute("iptc");
    }

    private static void ValidateImageDimensions(ulong width, ulong height)
    {
        if (width == 0 || width > (ulong)MediaConstraints.MaxImageWidth
            || height == 0 || height > (ulong)MediaConstraints.MaxImageHeight)
        {
            throw new DomainException(
                $"Image dimensions must not exceed {MediaConstraints.MaxImageWidth}x{MediaConstraints.MaxImageHeight} pixels.");
        }

        if (width > (ulong)MediaConstraints.MaxImagePixels / height)
        {
            throw new DomainException(
                $"Image resolution exceeds the maximum allowed {MediaConstraints.MaxImagePixels:N0} pixels.");
        }
    }

    private static bool ExceedsTotalPixelBudget(int width, int height, int frameCount, long maxTotalPixels)
    {
        if (width <= 0 || height <= 0 || frameCount <= 0)
        {
            return true;
        }

        var perFramePixels = (long)width * height;
        return perFramePixels > maxTotalPixels / frameCount;
    }

    private static (string CanonicalMime, MagickFormat Format) SniffAllowedStickerSignature(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        Span<byte> header = stackalloc byte[16];
        var bytesRead = 0;
        while (bytesRead < header.Length)
        {
            var read = stream.Read(header[bytesRead..]);
            if (read == 0)
            {
                break;
            }

            bytesRead += read;
        }

        var span = header[..bytesRead];

        if (span.Length >= 3 && span[0] == 0xFF && span[1] == 0xD8 && span[2] == 0xFF)
        {
            return ("image/jpeg", MagickFormat.Jpeg);
        }

        if (span.Length >= 8 && span[..8].SequenceEqual(PngSignature))
        {
            return ("image/png", MagickFormat.Png);
        }

        if (span.Length >= 12 && span[..4].SequenceEqual("RIFF"u8) && span[8..12].SequenceEqual("WEBP"u8))
        {
            return ("image/webp", MagickFormat.WebP);
        }

        if (span.Length >= 6 && (span[..6].SequenceEqual("GIF87a"u8) || span[..6].SequenceEqual("GIF89a"u8)))
        {
            return ("image/gif", MagickFormat.Gif);
        }

        throw new DomainException("Stickers must be WebP, PNG, JPEG, or GIF images.");
    }

    private static FileStream CreateDeleteOnCloseTempStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aos-sticker-proc-{Guid.NewGuid():N}.tmp");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }
}
