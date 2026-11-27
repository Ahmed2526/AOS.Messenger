using System.Buffers.Binary;
using System.Text;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using ImageMagick;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Infrastructure.Storage;

/// <summary>
/// Performs decompression-bomb-safe image decoding/re-encoding (stripping EXIF/XMP/IPTC/text/comments
/// and normalizing orientation via <c>Magick.NET</c>) plus bounded magic-byte and metadata-container inspection
/// for videos, documents, and voice messages.
/// </summary>
public sealed class MediaVerifier : IMediaVerifier, IDisposable
{
    private const int HeaderInspectBytes = 4096;
    private const int MaxContainerRangeReads = 16;
    private const int MaxContainerMetadataBytes = 256 * 1024;
    private const int MaxContainerBoxesVisited = 128;
    private const int MaxContainerDepth = 6;

    private static readonly HashSet<string> RejectedMp4Brands = new(StringComparer.Ordinal)
    {
        "M4A ", "M4B ", "M4P ", "heic", "heix", "hevc", "hevx", "mif1", "msf1", "avif", "avis", "qt  "
    };

    private static readonly HashSet<string> RejectedMovFtypBrands = new(StringComparer.Ordinal)
    {
        "M4A ", "M4B ", "M4P ", "heic", "heix", "hevc", "hevx", "mif1", "msf1", "avif", "avis"
    };

    private static readonly HashSet<string> AllowedMovRootBoxes = new(StringComparer.Ordinal)
    {
        "ftyp", "moov", "mdat", "wide", "free", "skip", "pnot"
    };

    private static readonly HashSet<string> AllowedVoiceMp4Brands = new(StringComparer.Ordinal)
    {
        "M4A ", "M4B ", "mp42", "isom", "iso2", "iso5", "iso6", "mp41", "dash", "qt  "
    };

    private static readonly HashSet<string> RejectedImageIsoBmffBrands = new(StringComparer.Ordinal)
    {
        "heic", "heix", "hevc", "hevx", "mif1", "msf1", "avif", "avis"
    };

    private static readonly byte[] OggSignature = [0x4F, 0x67, 0x67, 0x53];
    private const uint WebmSegmentElementIdValue = 0x18538067u;
    private const uint WebmSeekHeadElementIdValue = 0x114D9B74u;
    private const uint WebmSeekElementIdValue = 0x4DBBu;
    private const uint WebmSeekIdElementIdValue = 0x53ABu;
    private const uint WebmSeekPositionElementIdValue = 0x53ACu;
    private const uint WebmInfoElementIdValue = 0x1549A966u;
    private const uint WebmTracksElementIdValue = 0x1654AE6Bu;
    private const uint WebmCuesElementIdValue = 0x1C53BB6Bu;
    private const uint WebmTagsElementIdValue = 0x1254C367u;
    private const uint WebmClusterElementIdValue = 0x1F43B675u;
    private const uint WebmTrackEntryElementIdValue = 0xAEu;
    private const uint WebmTrackNumberElementIdValue = 0xD7u;
    private const uint WebmTrackTypeElementIdValue = 0x83u;
    private const uint WebmSimpleBlockElementIdValue = 0xA3u;
    private const uint WebmBlockGroupElementIdValue = 0xA0u;
    private const uint WebmBlockElementIdValue = 0xA1u;

    private static readonly string[] ActiveHtmlXmlPrefixes =
    [
        "<!doctype", "<html", "<head", "<body", "<svg", "<script", "<?xml", "<iframe", "<object", "<embed", "<meta"
    ];

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Ole2Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] PkLocalHeader = [0x50, 0x4B, 0x03, 0x04];
    private static readonly byte[] PkEmptyArchive = [0x50, 0x4B, 0x05, 0x06];
    private static readonly byte[] WebmSignature = [0x1A, 0x45, 0xDF, 0xA3];
    private static readonly byte[] EbmlDocTypeElementId = [0x42, 0x82];
    private static readonly byte[] ElfSignature = [0x7F, 0x45, 0x4C, 0x46];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IMediaStorage _storage;
    private readonly MediaOptions _options;
    private readonly ILogger<MediaVerifier> _logger;
    private readonly ImageProcessingGate _imageGate;
    private readonly bool _ownsImageGate;
    private readonly SemaphoreSlim _imageSemaphore;
    private readonly long _maxEncodedImageBytes;

    [ActivatorUtilitiesConstructor]
    public MediaVerifier(
        IMediaStorage storage,
        IOptions<MediaOptions> options,
        ILogger<MediaVerifier>? logger = null,
        ImageProcessingGate? sharedGate = null)
        : this(storage, options, logger ?? NullLogger<MediaVerifier>.Instance, MediaConstraints.MaxImageBytes, sharedGate)
    {
    }

    internal MediaVerifier(
        IMediaStorage storage,
        IOptions<MediaOptions> options,
        ILogger<MediaVerifier> logger,
        long maxEncodedImageBytes,
        ImageProcessingGate? sharedGate = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEncodedImageBytes);

        _storage = storage;
        _options = options.Value;
        _logger = logger;
        _maxEncodedImageBytes = maxEncodedImageBytes;
        _ownsImageGate = sharedGate is null;
        _imageGate = sharedGate ?? new ImageProcessingGate(options);
        _imageSemaphore = _imageGate.Semaphore;
    }

    public async Task<VerifiedMediaResult> VerifyAndSanitizeAsync(
        Media media,
        MediaObjectMetadata stagingMetadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(stagingMetadata);

        var (expectedKind, canonicalMime, maxSizeBytes) = media.Kind == MediaKind.Voice
            ? MediaConstraints.ResolveAllowedVoiceMedia(media.SanitizedFileName, media.DeclaredContentType)
            : MediaConstraints.ResolveAllowedMedia(media.SanitizedFileName, media.DeclaredContentType);

        if (expectedKind != media.Kind)
        {
            throw new DomainException("Uploaded media kind does not match the allowed file type.");
        }

        if (stagingMetadata.ContentLength <= 0 || stagingMetadata.ContentLength > maxSizeBytes)
        {
            throw new DomainException(
                $"Uploaded file size must be between 1 byte and {maxSizeBytes} bytes for {media.Kind}.");
        }

        return media.Kind switch
        {
            MediaKind.Image => await VerifyAndSanitizeImageAsync(media, stagingMetadata, canonicalMime, ct),
            MediaKind.Video => await VerifyVideoHeaderAsync(media, stagingMetadata, canonicalMime, ct),
            MediaKind.Document => await VerifyDocumentHeaderAsync(media, stagingMetadata, canonicalMime, ct),
            MediaKind.Voice => await VerifyVoiceHeaderAsync(media, stagingMetadata, canonicalMime, ct),
            _ => throw new DomainException($"Unsupported media kind '{media.Kind}'.")
        };
    }

    public void Dispose()
    {
        if (_ownsImageGate)
        {
            _imageGate.Dispose();
        }
    }

    private async Task<VerifiedMediaResult> VerifyAndSanitizeImageAsync(
        Media media,
        MediaObjectMetadata stagingMetadata,
        string expectedCanonicalMime,
        CancellationToken ct)
    {
        await using var inputStream = CreateDeleteOnCloseTempStream();
        await _storage.DownloadToStreamAsync(
            media.ObjectKey,
            inputStream,
            MediaConstraints.MaxImageBytes,
            stagingMetadata.ETag,
            ct);
        await inputStream.FlushAsync(ct);

        if (inputStream.Length <= 0)
        {
            throw new DomainException("Uploaded image file is empty.");
        }

        // Strict pre-Magick magic-byte sniffing prevents native coder auto-probing (SVG/MVG/PDF/PS/BMP/TIFF).
        var (sniffedMime, expectedFormat) = SniffAllowedImageSignature(inputStream);
        if (!string.Equals(sniffedMime, expectedCanonicalMime, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(
                $"Image content format '{sniffedMime}' does not match expected type '{expectedCanonicalMime}'.");
        }

        var maxFrames = Math.Max(1, _options.MaxAnimationFrames);
        var maxTotalPixels = Math.Max(MediaConstraints.MaxImagePixels, _options.MaxTotalDecodedImagePixels);
        var animationScan = expectedFormat switch
        {
            MagickFormat.Gif => ImageFrameCounter.ScanGif(inputStream, maxFrames, maxTotalPixels),
            MagickFormat.WebP => ImageFrameCounter.ScanWebp(inputStream, maxFrames, maxTotalPixels),
            _ => new AnimationScanResult(1, 0, 0, 0)
        };

        var timeout = _imageGate.Timeout;
        var acquired = await _imageSemaphore.WaitAsync(timeout, ct);
        if (!acquired)
        {
            throw new MediaProcessingBusyException("Image processing capacity is currently busy. Please try again.");
        }

        FileStream? outputStream = null;
        FileStream? thumbStream = null;
        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        jobCts.CancelAfter(timeout);
        try
        {
            outputStream = CreateDeleteOnCloseTempStream();
            thumbStream = CreateDeleteOnCloseTempStream();
            var (finalWidth, finalHeight) = await Task.Run(
                () => ProcessImageLocked(
                    inputStream,
                    outputStream,
                    thumbStream,
                    expectedFormat,
                    animationScan,
                    maxFrames,
                    jobCts.Token),
                jobCts.Token);

            await outputStream.FlushAsync(jobCts.Token);
            outputStream.Seek(0, SeekOrigin.Begin);

            FileStream? ownershipThumb = null;
            if (thumbStream.Length > 0)
            {
                await thumbStream.FlushAsync(jobCts.Token);
                thumbStream.Seek(0, SeekOrigin.Begin);
                ownershipThumb = thumbStream;
                thumbStream = null;
            }

            var ownershipStream = outputStream;
            outputStream = null;

            return new VerifiedMediaResult(
                sniffedMime,
                ownershipStream.Length,
                finalWidth,
                finalHeight,
                durationMs: null,
                processedStream: ownershipStream,
                thumbnailStream: ownershipThumb,
                thumbnailContentType: ownershipThumb is not null ? "image/webp" : null,
                thumbnailSizeBytes: ownershipThumb?.Length);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && jobCts.IsCancellationRequested)
        {
            _logger.LogWarning("Image processing timed out for media {MediaId}.", media.Id);
            throw new MediaProcessingBusyException("Image processing capacity is currently busy. Please try again.");
        }
        catch (MagickException ex)
        {
            _logger.LogWarning("Rejected invalid or unsupported image during verification for media {MediaId}: {ErrorType}", media.Id, ex.GetType().Name);
            throw new DomainException("The uploaded file is not a valid or supported image.");
        }
        finally
        {
            if (outputStream is not null)
            {
                await outputStream.DisposeAsync();
            }

            if (thumbStream is not null)
            {
                await thumbStream.DisposeAsync();
            }

            // Release semaphore only after all native CPU work inside Task.Run has completed.
            _imageSemaphore.Release();
        }
    }

    private (int Width, int Height) ProcessImageLocked(
        FileStream inputStream,
        FileStream outputStream,
        FileStream thumbStream,
        MagickFormat pinnedFormat,
        AnimationScanResult animationScan,
        int maxFrames,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var pingSettings = new MagickReadSettings
        {
            Format = pinnedFormat
        };

        var headersValidated = false;
        inputStream.Seek(0, SeekOrigin.Begin);
        var info = new MagickImageInfo(inputStream, pingSettings);
        ValidateImageDimensions(info.Width, info.Height);

        long estimatedTotalPixels = Math.Max(
            (long)info.Width * info.Height * animationScan.FrameCount,
            animationScan.TotalPixels);
        if (estimatedTotalPixels > _options.MaxTotalDecodedImagePixels)
        {
            throw new DomainException("Total decoded animation pixel count exceeds the maximum allowed budget.");
        }

        headersValidated = true;
        ct.ThrowIfCancellationRequested();
        inputStream.Seek(0, SeekOrigin.Begin);
        var decodeSettings = new MagickReadSettings
        {
            Format = pinnedFormat
        };

        try
        {
            if (pinnedFormat is MagickFormat.Gif or MagickFormat.WebP)
            {
                decodeSettings.FrameIndex = 0;
                decodeSettings.FrameCount = (uint)(maxFrames + 1);
                return ProcessAnimationCollectionLocked(inputStream, outputStream, thumbStream, pinnedFormat, decodeSettings, maxFrames, ct);
            }

            using var image = new MagickImage(inputStream, decodeSettings);
            ct.ThrowIfCancellationRequested();
            image.AutoOrient();

            // Read upright dimensions strictly AFTER AutoOrient()
            ValidateImageDimensions(image.Width, image.Height);
            var finalWidth = (int)image.Width;
            var finalHeight = (int)image.Height;

            StripSingleImageMetadata(image);
            EncodeSingleImageWithinLimit(image, pinnedFormat, outputStream, ct);
            TryGenerateChatThumbnail(image, finalWidth, finalHeight, outputStream.Length, thumbStream, ct);
            return (finalWidth, finalHeight);
        }
        catch (MagickException ex) when (IsTransientResourceExhaustion(ex, headersValidated))
        {
            _logger.LogWarning(
                "Transient ImageMagick resource limit hit while processing valid-header image: {ErrorType}",
                ex.GetType().Name);
            throw new MediaProcessingBusyException("Image processing capacity is currently busy. Please try again.");
        }
    }

    internal static bool IsTransientResourceExhaustion(MagickException ex, bool headersValidated)
    {
        if (!headersValidated)
        {
            return false;
        }

        if (ex is MagickCacheErrorException)
        {
            return true;
        }

        if (ex is MagickResourceLimitErrorException)
        {
            var msg = ex.Message ?? string.Empty;
            if (msg.Contains("width", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("height", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("geometry", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("list length", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        return false;
    }

    private (int Width, int Height) ProcessAnimationCollectionLocked(
        FileStream inputStream,
        FileStream outputStream,
        FileStream thumbStream,
        MagickFormat pinnedFormat,
        MagickReadSettings decodeSettings,
        int maxFrames,
        CancellationToken ct)
    {
        using var collection = new MagickImageCollection(inputStream, decodeSettings);
        if (collection.Count == 0 || collection.Count > maxFrames)
        {
            throw new DomainException($"Image frame count must be between 1 and {maxFrames}.");
        }

        long actualTotalPixels = 0;
        var maxWidth = 0;
        var maxHeight = 0;

        foreach (var frame in collection)
        {
            ct.ThrowIfCancellationRequested();

            ValidateImageDimensions(frame.Width, frame.Height);
            var rawWidth = (int)frame.Width;
            var rawHeight = (int)frame.Height;

            actualTotalPixels += (long)rawWidth * rawHeight;
            if (actualTotalPixels > _options.MaxTotalDecodedImagePixels)
            {
                throw new DomainException("Total decoded animation pixel count exceeds the maximum allowed budget.");
            }

            frame.AutoOrient();

            ValidateImageDimensions(frame.Width, frame.Height);
            var uprightWidth = (int)frame.Width;
            var uprightHeight = (int)frame.Height;
            maxWidth = Math.Max(maxWidth, uprightWidth);
            maxHeight = Math.Max(maxHeight, uprightHeight);

            StripSingleImageMetadata(frame);
        }

        ValidateImageDimensions((ulong)maxWidth, (ulong)maxHeight);

        EncodeCollectionWithinLimit(collection, pinnedFormat, outputStream, ct);

        if (pinnedFormat == MagickFormat.WebP && collection.Count == 1)
        {
            TryGenerateChatThumbnail(collection[0], maxWidth, maxHeight, outputStream.Length, thumbStream, ct);
        }

        return (maxWidth, maxHeight);
    }

    private static void TryGenerateChatThumbnail(
        IMagickImage<byte> strippedUprightImage,
        int width,
        int height,
        long fullOutputLength,
        FileStream thumbStream,
        CancellationToken ct)
    {
        if (width <= MediaConstraints.MaxChatImageThumbnailEdge
            && height <= MediaConstraints.MaxChatImageThumbnailEdge
            && fullOutputLength <= MediaConstraints.ChatImageThumbnailThresholdBytes)
        {
            return;
        }

        try
        {
            using var clone = strippedUprightImage.Clone();
            clone.FilterType = FilterType.Lanczos;
            if (width > MediaConstraints.MaxChatImageThumbnailEdge || height > MediaConstraints.MaxChatImageThumbnailEdge)
            {
                clone.Resize(new MagickGeometry(
                    (uint)MediaConstraints.MaxChatImageThumbnailEdge,
                    (uint)MediaConstraints.MaxChatImageThumbnailEdge)
                {
                    Greater = true
                });
            }

            foreach (var quality in new uint[] { 75, 60 })
            {
                ct.ThrowIfCancellationRequested();
                thumbStream.SetLength(0);
                thumbStream.Seek(0, SeekOrigin.Begin);
                clone.Quality = quality;
                clone.Write(thumbStream, MagickFormat.WebP);

                if (thumbStream.Length > 0 && thumbStream.Length <= MediaConstraints.MaxChatImageThumbnailBytes)
                {
                    return;
                }
            }

            thumbStream.SetLength(0);
        }
        catch (MagickException)
        {
            thumbStream.SetLength(0);
        }
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

    private void EncodeSingleImageWithinLimit(IMagickImage<byte> image, MagickFormat format, FileStream outputStream, CancellationToken ct)
    {
        if (format == MagickFormat.Jpeg)
        {
            foreach (var quality in new uint[] { 88, 75, 60 })
            {
                ct.ThrowIfCancellationRequested();
                outputStream.SetLength(0);
                outputStream.Seek(0, SeekOrigin.Begin);
                image.Quality = quality;
                image.Write(outputStream, MagickFormat.Jpeg);

                if (outputStream.Length > 0 && outputStream.Length <= _maxEncodedImageBytes)
                {
                    return;
                }
            }
        }
        else if (format == MagickFormat.Png)
        {
            foreach (var level in new[] { "6", "9" })
            {
                ct.ThrowIfCancellationRequested();
                outputStream.SetLength(0);
                outputStream.Seek(0, SeekOrigin.Begin);
                image.Settings.SetDefine(MagickFormat.Png, "compression-level", level);
                image.Settings.SetDefine(MagickFormat.Png, "exclude-chunk", "all");
                image.Write(outputStream, MagickFormat.Png);

                if (outputStream.Length > 0 && outputStream.Length <= _maxEncodedImageBytes)
                {
                    return;
                }
            }
        }
        else
        {
            throw new DomainException($"Unsupported single-frame format '{format}'.");
        }

        throw new DomainException(
            $"Re-encoded image exceeds the maximum allowed {_maxEncodedImageBytes} bytes.");
    }

    private void EncodeCollectionWithinLimit(MagickImageCollection collection, MagickFormat format, FileStream outputStream, CancellationToken ct)
    {
        if (format == MagickFormat.WebP)
        {
            foreach (var quality in new uint[] { 85, 70, 55 })
            {
                ct.ThrowIfCancellationRequested();
                outputStream.SetLength(0);
                outputStream.Seek(0, SeekOrigin.Begin);
                foreach (var frame in collection)
                {
                    frame.Quality = quality;
                }

                collection.Write(outputStream, MagickFormat.WebP);

                if (outputStream.Length > 0 && outputStream.Length <= _maxEncodedImageBytes)
                {
                    return;
                }
            }
        }
        else if (format == MagickFormat.Gif)
        {
            ct.ThrowIfCancellationRequested();
            outputStream.SetLength(0);
            outputStream.Seek(0, SeekOrigin.Begin);
            collection.Write(outputStream, MagickFormat.Gif);

            if (outputStream.Length > 0 && outputStream.Length <= _maxEncodedImageBytes)
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            outputStream.SetLength(0);
            outputStream.Seek(0, SeekOrigin.Begin);
            collection.Quantize(new QuantizeSettings
            {
                Colors = 128,
                DitherMethod = DitherMethod.No
            });
            collection.OptimizeTransparency();
            collection.Write(outputStream, MagickFormat.Gif);

            if (outputStream.Length > 0 && outputStream.Length <= _maxEncodedImageBytes)
            {
                return;
            }
        }
        else
        {
            throw new DomainException($"Unsupported animation format '{format}'.");
        }

        throw new DomainException(
            $"Re-encoded image exceeds the maximum allowed {_maxEncodedImageBytes} bytes.");
    }

    private static (string CanonicalMime, MagickFormat Format) SniffAllowedImageSignature(Stream stream)
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

        throw new DomainException("The uploaded file is not a valid or supported image.");
    }

    private async Task<VerifiedMediaResult> VerifyVideoHeaderAsync(
        Media media,
        MediaObjectMetadata stagingMetadata,
        string canonicalMime,
        CancellationToken ct)
    {
        var header = await _storage.ReadByteRangeAsync(
            media.ObjectKey,
            0,
            HeaderInspectBytes,
            stagingMetadata.ETag,
            ct);

        if (header.Length < 8)
        {
            throw new DomainException("Video file header is too small or corrupt.");
        }

        switch (canonicalMime)
        {
            case "video/mp4":
                ValidateMp4Header(header, stagingMetadata.ContentLength);
                break;

            case "video/webm":
                ValidateWebmHeader(header);
                break;

            case "video/quicktime":
                ValidateMovHeader(header, stagingMetadata.ContentLength);
                break;

            default:
                throw new DomainException($"Unsupported video type '{canonicalMime}'.");
        }

        return new VerifiedMediaResult(canonicalMime, stagingMetadata.ContentLength);
    }

    /// <summary>
    /// Verifies bounded audio container structure for voice uploads. <c>media.DurationMs</c> is forwarded as
    /// client-declared advisory UI metadata; V1 does not perform full-file audio decoding or transcoding.
    /// </summary>
    private async Task<VerifiedMediaResult> VerifyVoiceHeaderAsync(
        Media media,
        MediaObjectMetadata stagingMetadata,
        string canonicalMime,
        CancellationToken ct)
    {
        var header = await _storage.ReadByteRangeAsync(
            media.ObjectKey,
            0,
            HeaderInspectBytes,
            stagingMetadata.ETag,
            ct);

        if (header.Length == 0)
        {
            throw new DomainException("Uploaded voice recording is empty.");
        }

        switch (canonicalMime)
        {
            case "audio/mp4":
                var mp4Reader = new BoundedStorageReader(
                    _storage,
                    media.ObjectKey,
                    stagingMetadata.ContentLength,
                    stagingMetadata.ETag,
                    header);
                await ValidateVoiceMp4Async(mp4Reader, ct);
                break;

            case "audio/webm":
                var segmentStartOffset = ValidateWebmHeader(header, stagingMetadata.ContentLength);
                var webmReader = new BoundedStorageReader(
                    _storage,
                    media.ObjectKey,
                    stagingMetadata.ContentLength,
                    stagingMetadata.ETag,
                    header);
                await ValidateWebmAudioTracksAsync(webmReader, segmentStartOffset, ct);
                break;

            case "audio/ogg":
                ValidateOggAudioHeader(header);
                break;

            default:
                throw new DomainException($"Unsupported voice media type '{canonicalMime}'.");
        }

        return new VerifiedMediaResult(canonicalMime, stagingMetadata.ContentLength, durationMs: media.DurationMs);
    }

    private async Task<VerifiedMediaResult> VerifyDocumentHeaderAsync(
        Media media,
        MediaObjectMetadata stagingMetadata,
        string canonicalMime,
        CancellationToken ct)
    {
        var header = await _storage.ReadByteRangeAsync(
            media.ObjectKey,
            0,
            HeaderInspectBytes,
            stagingMetadata.ETag,
            ct);

        if (header.Length == 0)
        {
            throw new DomainException("Uploaded document is empty.");
        }

        var ext = Path.GetExtension(media.SanitizedFileName).ToLowerInvariant();
        switch (ext)
        {
            case ".pdf":
                if (!header.AsSpan().StartsWith("%PDF-"u8))
                {
                    throw new DomainException("Invalid PDF file signature.");
                }

                break;

            case ".doc":
            case ".xls":
            case ".ppt":
                if (!header.AsSpan().StartsWith(Ole2Signature))
                {
                    throw new DomainException("Invalid Microsoft Office legacy document signature.");
                }

                break;

            case ".docx":
            case ".xlsx":
            case ".pptx":
                if (!header.AsSpan().StartsWith(PkLocalHeader))
                {
                    throw new DomainException("Invalid OOXML Office document signature.");
                }

                break;

            case ".zip":
                if (!header.AsSpan().StartsWith(PkLocalHeader) && !header.AsSpan().StartsWith(PkEmptyArchive))
                {
                    throw new DomainException("Invalid ZIP archive signature.");
                }

                break;

            case ".txt":
            case ".csv":
                ValidatePlainTextHeader(header, stagingMetadata.ContentLength);
                break;

            default:
                throw new DomainException($"Unsupported document extension '{ext}'.");
        }

        return new VerifiedMediaResult(canonicalMime, stagingMetadata.ContentLength);
    }

    private static void ValidateImageDimensions(ulong width, ulong height)
    {
        if (width == 0 || width > (ulong)MediaConstraints.MaxImageWidth ||
            height == 0 || height > (ulong)MediaConstraints.MaxImageHeight)
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

    private static void ValidateMp4Header(ReadOnlySpan<byte> header, long totalContentLength)
    {
        if (!TryValidateIsoBmffBox(header, totalContentLength, out var boxType, out var payloadOffset, out var boxSize))
        {
            throw new DomainException("Invalid MP4 container box structure.");
        }

        if (!string.Equals(boxType, "ftyp", StringComparison.Ordinal) || boxSize == 0)
        {
            throw new DomainException("MP4 file must begin with an ISO BMFF 'ftyp' box.");
        }

        if (boxSize < payloadOffset + 4 || header.Length < payloadOffset + 4)
        {
            throw new DomainException("Invalid MP4 'ftyp' box size.");
        }

        var brandSpan = header.Slice(payloadOffset, 4);
        if (!IsPrintableAscii(brandSpan))
        {
            throw new DomainException("Invalid MP4 major brand identifier.");
        }

        var majorBrand = Encoding.ASCII.GetString(brandSpan);
        if (RejectedMp4Brands.Contains(majorBrand))
        {
            throw new DomainException($"Media container brand '{majorBrand}' is not permitted as MP4.");
        }
    }

    private static async Task ValidateVoiceMp4Async(BoundedStorageReader reader, CancellationToken ct)
    {
        var counter = new BoxCounter(1);
        var ftyp = await TryReadIsoBmffBoxHeaderAsync(
            reader,
            offset: 0,
            limitEnd: reader.TotalLength,
            allowZeroSizeToEnd: false,
            ct);

        if (ftyp is null || !string.Equals(ftyp.Value.BoxType, "ftyp", StringComparison.Ordinal))
        {
            throw new DomainException("MP4 audio file must begin with an ISO BMFF 'ftyp' box.");
        }

        var ftypHeader = ftyp.Value;
        if (ftypHeader.PayloadLength < 4 || ftypHeader.EndOffset > reader.TotalLength)
        {
            throw new DomainException("Invalid MP4 audio 'ftyp' box size.");
        }

        var brandBytes = new byte[4];
        if (!await reader.ReadExactAsync(ftypHeader.PayloadOffset, brandBytes, ct) || !IsPrintableAscii(brandBytes))
        {
            throw new DomainException("Invalid MP4 audio major brand identifier.");
        }

        var majorBrand = Encoding.ASCII.GetString(brandBytes);
        if (RejectedImageIsoBmffBrands.Contains(majorBrand) || !AllowedVoiceMp4Brands.Contains(majorBrand))
        {
            throw new DomainException($"Media container brand '{majorBrand}' is not permitted for voice audio.");
        }

        var offset = ftypHeader.EndOffset;
        var foundMoov = false;
        var hasAudioHandler = false;
        var hasNonEmptyMdat = false;
        var hasMvex = false;
        var hasRegularAudioSamples = false;
        var audioTrackIds = new HashSet<uint>();
        var fragmentSampleTrackIds = new HashSet<uint>();

        while (offset < reader.TotalLength)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("MP4 audio container exceeds maximum inspectable box count.");
            }

            var box = await TryReadIsoBmffBoxHeaderAsync(
                reader,
                offset,
                limitEnd: reader.TotalLength,
                allowZeroSizeToEnd: true,
                ct) ?? throw new DomainException("Invalid MP4 audio container box structure.");

            if (string.Equals(box.BoxType, "mdat", StringComparison.Ordinal))
            {
                if (box.PayloadLength > 0)
                {
                    hasNonEmptyMdat = true;
                }
            }
            else if (string.Equals(box.BoxType, "moov", StringComparison.Ordinal))
            {
                foundMoov = true;
                var moovInfo = await ScanMp4MoovForVoiceAsync(
                    reader,
                    box.PayloadOffset,
                    box.EndOffset,
                    depth: 1,
                    counter,
                    audioTrackIds,
                    ct);
                hasAudioHandler |= moovInfo.HasAudioHandler;
                hasMvex |= moovInfo.HasMvex;
                hasRegularAudioSamples |= moovInfo.HasRegularAudioSamples;
            }
            else if (string.Equals(box.BoxType, "moof", StringComparison.Ordinal))
            {
                if (box.EndOffset > box.PayloadOffset)
                {
                    await ScanMp4MoofForSamplesAsync(
                        reader,
                        box.PayloadOffset,
                        box.EndOffset,
                        depth: 1,
                        counter,
                        fragmentSampleTrackIds,
                        ct);
                }
            }

            if (box.EndOffset <= offset)
            {
                throw new DomainException("Invalid MP4 audio container box progression.");
            }

            offset = box.EndOffset;
        }

        if (!foundMoov || !hasAudioHandler)
        {
            throw new DomainException("MP4 voice recording must contain an audio track.");
        }

        var hasFragmentedAudioSamples = hasMvex && audioTrackIds.Overlaps(fragmentSampleTrackIds);
        if (!hasNonEmptyMdat || (!hasRegularAudioSamples && !hasFragmentedAudioSamples))
        {
            throw new DomainException("MP4 voice recording must contain audio samples.");
        }
    }

    private sealed class BoxCounter(int initial)
    {
        public int Count { get; set; } = initial;
    }

    private readonly record struct IsoBmffBoxHeader(
        string BoxType,
        long PayloadOffset,
        long EndOffset)
    {
        public long PayloadLength => EndOffset - PayloadOffset;
    }

    private readonly record struct Mp4MoovVoiceInfo(
        bool HasAudioHandler,
        bool HasMvex,
        bool HasRegularAudioSamples);

    private sealed class Mp4TrackVoiceState
    {
        public uint TrackId { get; set; }
        public bool HasAudioHandler { get; set; }
        public bool HasRegularSamples { get; set; }
    }

    private static async Task<Mp4MoovVoiceInfo> ScanMp4MoovForVoiceAsync(
        BoundedStorageReader reader,
        long startOffset,
        long endOffset,
        int depth,
        BoxCounter counter,
        HashSet<uint> audioTrackIds,
        CancellationToken ct)
    {
        if (depth > MaxContainerDepth)
        {
            throw new DomainException("MP4 container nesting exceeds maximum allowed depth.");
        }

        var hasAudioHandler = false;
        var hasMvex = false;
        var hasRegularAudioSamples = false;
        var offset = startOffset;

        while (offset < endOffset)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("MP4 container exceeds maximum inspectable box count.");
            }

            var box = await TryReadIsoBmffBoxHeaderAsync(
                reader,
                offset,
                endOffset,
                allowZeroSizeToEnd: false,
                ct) ?? throw new DomainException("Invalid MP4 audio container box structure.");

            if (string.Equals(box.BoxType, "mvex", StringComparison.Ordinal))
            {
                hasMvex = true;
            }
            else if (string.Equals(box.BoxType, "trak", StringComparison.Ordinal))
            {
                if (box.EndOffset > box.PayloadOffset)
                {
                    var trackState = new Mp4TrackVoiceState();
                    await ScanMp4TrackTreeAsync(
                        reader,
                        box.PayloadOffset,
                        box.EndOffset,
                        depth + 1,
                        inMdia: false,
                        counter,
                        trackState,
                        ct);
                    if (trackState.HasAudioHandler)
                    {
                        hasAudioHandler = true;
                        if (trackState.TrackId > 0)
                        {
                            audioTrackIds.Add(trackState.TrackId);
                        }

                        if (trackState.HasRegularSamples)
                        {
                            hasRegularAudioSamples = true;
                        }
                    }
                }
            }
            else if (string.Equals(box.BoxType, "moov", StringComparison.Ordinal))
            {
                if (box.EndOffset > box.PayloadOffset)
                {
                    var nested = await ScanMp4MoovForVoiceAsync(
                        reader,
                        box.PayloadOffset,
                        box.EndOffset,
                        depth + 1,
                        counter,
                        audioTrackIds,
                        ct);
                    hasAudioHandler |= nested.HasAudioHandler;
                    hasMvex |= nested.HasMvex;
                    hasRegularAudioSamples |= nested.HasRegularAudioSamples;
                }
            }

            if (box.EndOffset <= offset)
            {
                throw new DomainException("Invalid MP4 audio container box progression.");
            }

            offset = box.EndOffset;
        }

        return new Mp4MoovVoiceInfo(hasAudioHandler, hasMvex, hasRegularAudioSamples);
    }

    private static async Task ScanMp4TrackTreeAsync(
        BoundedStorageReader reader,
        long startOffset,
        long endOffset,
        int depth,
        bool inMdia,
        BoxCounter counter,
        Mp4TrackVoiceState trackState,
        CancellationToken ct)
    {
        if (depth > MaxContainerDepth)
        {
            throw new DomainException("MP4 container nesting exceeds maximum allowed depth.");
        }

        var offset = startOffset;

        while (offset < endOffset)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("MP4 container exceeds maximum inspectable box count.");
            }

            var box = await TryReadIsoBmffBoxHeaderAsync(
                reader,
                offset,
                endOffset,
                allowZeroSizeToEnd: false,
                ct) ?? throw new DomainException("Invalid MP4 audio container box structure.");

            if (string.Equals(box.BoxType, "tkhd", StringComparison.Ordinal))
            {
                if (box.PayloadLength >= 16)
                {
                    var versionBuf = new byte[1];
                    if (!await reader.ReadExactAsync(box.PayloadOffset, versionBuf, ct))
                    {
                        throw new DomainException("Truncated MP4 track header box.");
                    }

                    var idOffset = versionBuf[0] == 1 ? 20L : 12L;
                    if (box.PayloadLength >= idOffset + 4)
                    {
                        var idBuf = new byte[4];
                        if (!await reader.ReadExactAsync(box.PayloadOffset + idOffset, idBuf, ct))
                        {
                            throw new DomainException("Truncated MP4 track header box.");
                        }

                        var parsedId = BinaryPrimitives.ReadUInt32BigEndian(idBuf);
                        if (parsedId > 0)
                        {
                            trackState.TrackId = parsedId;
                        }
                    }
                }
            }
            else if (string.Equals(box.BoxType, "hdlr", StringComparison.Ordinal))
            {
                // FullBox (4 bytes version/flags) + pre_defined (4 bytes) + handler_type (4 bytes)
                if (box.PayloadLength >= 12)
                {
                    var handlerHeader = new byte[12];
                    if (!await reader.ReadExactAsync(box.PayloadOffset, handlerHeader, ct))
                    {
                        throw new DomainException("Truncated MP4 handler box.");
                    }

                    var handlerType = handlerHeader.AsSpan(8, 4);
                    if (handlerType.SequenceEqual("vide"u8) ||
                        handlerType.SequenceEqual("pict"u8) ||
                        handlerType.SequenceEqual("auxv"u8))
                    {
                        throw new DomainException("Video tracks are not permitted in voice recordings.");
                    }

                    if (inMdia && handlerType.SequenceEqual("soun"u8))
                    {
                        trackState.HasAudioHandler = true;
                    }
                }
            }
            else if (box.BoxType is "stsz" or "stz2")
            {
                // FullBox (4) + sample_size/reserved (4) + sample_count (4)
                if (inMdia && box.PayloadLength >= 12)
                {
                    var stszHeader = new byte[12];
                    if (!await reader.ReadExactAsync(box.PayloadOffset, stszHeader, ct))
                    {
                        throw new DomainException("Truncated MP4 sample size box.");
                    }

                    var sampleCount = BinaryPrimitives.ReadUInt32BigEndian(stszHeader.AsSpan(8, 4));
                    if (sampleCount > 0)
                    {
                        trackState.HasRegularSamples = true;
                    }
                }
            }
            else if (box.BoxType is "trak" or "mdia" or "minf" or "stbl")
            {
                if (box.EndOffset > box.PayloadOffset)
                {
                    await ScanMp4TrackTreeAsync(
                        reader,
                        box.PayloadOffset,
                        box.EndOffset,
                        depth + 1,
                        inMdia || string.Equals(box.BoxType, "mdia", StringComparison.Ordinal),
                        counter,
                        trackState,
                        ct);
                }
            }

            if (box.EndOffset <= offset)
            {
                throw new DomainException("Invalid MP4 audio container box progression.");
            }

            offset = box.EndOffset;
        }
    }

    private static async Task ScanMp4MoofForSamplesAsync(
        BoundedStorageReader reader,
        long startOffset,
        long endOffset,
        int depth,
        BoxCounter counter,
        HashSet<uint> fragmentSampleTrackIds,
        CancellationToken ct)
    {
        if (depth > MaxContainerDepth)
        {
            throw new DomainException("MP4 fragment container nesting exceeds maximum allowed depth.");
        }

        var offset = startOffset;
        while (offset < endOffset)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("MP4 container exceeds maximum inspectable box count.");
            }

            var box = await TryReadIsoBmffBoxHeaderAsync(
                reader,
                offset,
                endOffset,
                allowZeroSizeToEnd: false,
                ct) ?? throw new DomainException("Invalid MP4 fragment container box structure.");

            if (string.Equals(box.BoxType, "traf", StringComparison.Ordinal) && box.EndOffset > box.PayloadOffset)
            {
                await ScanMp4TrafAsync(
                    reader,
                    box.PayloadOffset,
                    box.EndOffset,
                    depth + 1,
                    counter,
                    fragmentSampleTrackIds,
                    ct);
            }

            if (box.EndOffset <= offset)
            {
                throw new DomainException("Invalid MP4 fragment container box progression.");
            }

            offset = box.EndOffset;
        }
    }

    private static async Task ScanMp4TrafAsync(
        BoundedStorageReader reader,
        long startOffset,
        long endOffset,
        int depth,
        BoxCounter counter,
        HashSet<uint> fragmentSampleTrackIds,
        CancellationToken ct)
    {
        if (depth > MaxContainerDepth)
        {
            throw new DomainException("MP4 fragment container nesting exceeds maximum allowed depth.");
        }

        uint trafTrackId = 0;
        var hasSamples = false;
        var offset = startOffset;

        while (offset < endOffset)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("MP4 container exceeds maximum inspectable box count.");
            }

            var box = await TryReadIsoBmffBoxHeaderAsync(
                reader,
                offset,
                endOffset,
                allowZeroSizeToEnd: false,
                ct) ?? throw new DomainException("Invalid MP4 fragment container box structure.");

            if (string.Equals(box.BoxType, "tfhd", StringComparison.Ordinal) && box.PayloadLength >= 8)
            {
                var buf = new byte[8];
                if (!await reader.ReadExactAsync(box.PayloadOffset, buf, ct))
                {
                    throw new DomainException("Truncated MP4 track fragment header box.");
                }

                trafTrackId = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4));
            }
            else if (string.Equals(box.BoxType, "trun", StringComparison.Ordinal) && box.PayloadLength >= 8)
            {
                var buf = new byte[8];
                if (!await reader.ReadExactAsync(box.PayloadOffset, buf, ct))
                {
                    throw new DomainException("Truncated MP4 track fragment run box.");
                }

                if (BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4)) > 0)
                {
                    hasSamples = true;
                }
            }

            if (box.EndOffset <= offset)
            {
                throw new DomainException("Invalid MP4 fragment container box progression.");
            }

            offset = box.EndOffset;
        }

        if (trafTrackId > 0 && hasSamples)
        {
            fragmentSampleTrackIds.Add(trafTrackId);
        }
    }

    private static async Task<IsoBmffBoxHeader?> TryReadIsoBmffBoxHeaderAsync(
        BoundedStorageReader reader,
        long offset,
        long limitEnd,
        bool allowZeroSizeToEnd,
        CancellationToken ct)
    {
        if (offset < 0 || limitEnd - offset < 8)
        {
            return null;
        }

        var head8 = new byte[8];
        if (!await reader.ReadExactAsync(offset, head8, ct))
        {
            return null;
        }

        var rawSize = BinaryPrimitives.ReadUInt32BigEndian(head8.AsSpan(0, 4));
        var typeSpan = head8.AsSpan(4, 4);
        if (!IsPrintableAscii(typeSpan))
        {
            return null;
        }

        var boxType = Encoding.ASCII.GetString(typeSpan);
        if (rawSize == 0)
        {
            if (!allowZeroSizeToEnd)
            {
                return null;
            }

            return new IsoBmffBoxHeader(boxType, offset + 8, limitEnd);
        }

        long payloadOffset;
        long boxSize;
        if (rawSize == 1)
        {
            if (limitEnd - offset < 16)
            {
                return null;
            }

            var ext8 = new byte[8];
            if (!await reader.ReadExactAsync(offset + 8, ext8, ct))
            {
                return null;
            }

            var extendedSize = BinaryPrimitives.ReadUInt64BigEndian(ext8);
            if (extendedSize < 16 || extendedSize > (ulong)(limitEnd - offset))
            {
                return null;
            }

            payloadOffset = offset + 16;
            boxSize = (long)extendedSize;
        }
        else
        {
            if (rawSize < 8 || (long)rawSize > limitEnd - offset)
            {
                return null;
            }

            payloadOffset = offset + 8;
            boxSize = rawSize;
        }

        return new IsoBmffBoxHeader(boxType, payloadOffset, offset + boxSize);
    }

    private static async Task ValidateWebmAudioTracksAsync(
        BoundedStorageReader reader,
        long segmentSearchStartOffset,
        CancellationToken ct)
    {
        var counter = new BoxCounter(0);
        var pos = segmentSearchStartOffset;
        var foundSegment = false;
        var foundTracks = false;
        var audioTrackNumbers = new HashSet<ulong>();
        var hasAudioBlock = false;

        while (pos < reader.TotalLength)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("WebM container exceeds maximum inspectable element count.");
            }

            var element = await TryReadEbmlElementHeaderAsync(reader, pos, reader.TotalLength, ct)
                ?? throw new DomainException("Invalid WebM EBML container structure.");

            if (element.ElementId == WebmSegmentElementIdValue)
            {
                foundSegment = true;
                var segmentEnd = element.IsUnknownSize
                    ? reader.TotalLength
                    : element.EndOffset;
                (foundTracks, hasAudioBlock) = await ScanWebmSegmentForVoiceAsync(
                    reader,
                    element.PayloadOffset,
                    segmentEnd,
                    counter,
                    audioTrackNumbers,
                    ct);
                break;
            }

            if (element.IsUnknownSize || element.EndOffset <= pos)
            {
                throw new DomainException("WebM container has invalid top-level element sizing.");
            }

            pos = element.EndOffset;
        }

        if (!foundSegment || !foundTracks || audioTrackNumbers.Count == 0)
        {
            throw new DomainException("WebM voice recording must contain an audio track.");
        }

        if (!hasAudioBlock)
        {
            throw new DomainException("WebM voice recording must contain audio data blocks.");
        }
    }

    private static async Task<(bool FoundTracks, bool HasAudioBlock)> ScanWebmSegmentForVoiceAsync(
        BoundedStorageReader reader,
        long startOffset,
        long endOffset,
        BoxCounter counter,
        HashSet<ulong> audioTrackNumbers,
        CancellationToken ct)
    {
        var pos = startOffset;
        var foundTracks = false;
        long? indexedTracksOffset = null;
        EbmlElementHeader? firstCluster = null;

        while (pos < endOffset)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("WebM segment exceeds maximum inspectable element count.");
            }

            var element = await TryReadEbmlElementHeaderAsync(reader, pos, endOffset, ct)
                ?? throw new DomainException("Invalid WebM segment element framing.");

            if (element.ElementId == WebmClusterElementIdValue)
            {
                firstCluster = element;
                break;
            }

            if (element.ElementId == WebmSeekHeadElementIdValue &&
                !element.IsUnknownSize &&
                element.PayloadLength > 0 &&
                !indexedTracksOffset.HasValue)
            {
                indexedTracksOffset = await TryResolveTracksOffsetFromSeekHeadAsync(
                    reader,
                    element.PayloadOffset,
                    element.EndOffset,
                    startOffset,
                    endOffset,
                    counter,
                    ct);
            }
            else if (element.ElementId == WebmTracksElementIdValue)
            {
                if (element.IsUnknownSize || element.PayloadLength == 0)
                {
                    throw new DomainException("WebM voice recording has invalid Tracks section.");
                }

                foundTracks = true;
                await ScanWebmTracksElementAsync(
                    reader,
                    element.PayloadOffset,
                    element.EndOffset,
                    depth: 2,
                    counter,
                    audioTrackNumbers,
                    ct);
            }

            if (element.IsUnknownSize || element.EndOffset <= pos)
            {
                throw new DomainException("WebM segment contains an invalid unknown-size metadata element.");
            }

            pos = element.EndOffset;
        }

        if (!foundTracks && indexedTracksOffset.HasValue)
        {
            var tracksEl = await TryReadEbmlElementHeaderAsync(reader, indexedTracksOffset.Value, endOffset, ct);
            if (tracksEl is { ElementId: WebmTracksElementIdValue, IsUnknownSize: false, PayloadLength: > 0 })
            {
                foundTracks = true;
                await ScanWebmTracksElementAsync(
                    reader,
                    tracksEl.Value.PayloadOffset,
                    tracksEl.Value.EndOffset,
                    depth: 2,
                    counter,
                    audioTrackNumbers,
                    ct);
            }
        }

        if (!foundTracks || audioTrackNumbers.Count == 0 || firstCluster is null)
        {
            return (foundTracks, false);
        }

        var hasAudioBlock = await ScanWebmClusterForAudioBlockAsync(
            reader,
            firstCluster.Value,
            audioTrackNumbers,
            counter,
            ct);

        return (foundTracks, hasAudioBlock);
    }

    private static async Task<long?> TryResolveTracksOffsetFromSeekHeadAsync(
        BoundedStorageReader reader,
        long startOffset,
        long endOffset,
        long segmentPayloadStart,
        long segmentEnd,
        BoxCounter counter,
        CancellationToken ct)
    {
        var pos = startOffset;
        while (pos < endOffset)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("WebM SeekHead exceeds maximum inspectable element count.");
            }

            var element = await TryReadEbmlElementHeaderAsync(reader, pos, endOffset, ct)
                ?? throw new DomainException("Invalid WebM SeekHead element framing.");

            if (element.IsUnknownSize || element.EndOffset <= pos)
            {
                throw new DomainException("Invalid WebM SeekHead element sizing.");
            }

            if (element.ElementId == WebmSeekElementIdValue && element.PayloadLength > 0)
            {
                var seekPos = element.PayloadOffset;
                uint? seekId = null;
                ulong? seekPosition = null;

                while (seekPos < element.EndOffset)
                {
                    counter.Count++;
                    if (counter.Count > MaxContainerBoxesVisited)
                    {
                        throw new DomainException("WebM Seek entry exceeds maximum inspectable element count.");
                    }

                    var child = await TryReadEbmlElementHeaderAsync(reader, seekPos, element.EndOffset, ct)
                        ?? throw new DomainException("Invalid WebM Seek entry framing.");

                    if (child.IsUnknownSize || child.EndOffset <= seekPos)
                    {
                        throw new DomainException("Invalid WebM Seek entry sizing.");
                    }

                    if (child.ElementId == WebmSeekIdElementIdValue && child.PayloadLength is >= 1 and <= 4)
                    {
                        seekId = (uint)await ReadEbmlUnsignedIntAsync(reader, child.PayloadOffset, (int)child.PayloadLength, ct);
                    }
                    else if (child.ElementId == WebmSeekPositionElementIdValue && child.PayloadLength is >= 1 and <= 8)
                    {
                        seekPosition = await ReadEbmlUnsignedIntAsync(reader, child.PayloadOffset, (int)child.PayloadLength, ct);
                    }

                    seekPos = child.EndOffset;
                }

                if (seekId == WebmTracksElementIdValue &&
                    seekPosition.HasValue &&
                    seekPosition.Value <= (ulong)(segmentEnd - segmentPayloadStart))
                {
                    var absOffset = checked(segmentPayloadStart + (long)seekPosition.Value);
                    if (absOffset >= segmentPayloadStart && absOffset < segmentEnd)
                    {
                        return absOffset;
                    }
                }
            }

            pos = element.EndOffset;
        }

        return null;
    }

    private static async Task ScanWebmTracksElementAsync(
        BoundedStorageReader reader,
        long startOffset,
        long endOffset,
        int depth,
        BoxCounter counter,
        HashSet<ulong> audioTrackNumbers,
        CancellationToken ct)
    {
        if (depth > MaxContainerDepth)
        {
            throw new DomainException("WebM Tracks nesting exceeds maximum allowed depth.");
        }

        var pos = startOffset;

        while (pos < endOffset)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("WebM Tracks exceeds maximum inspectable element count.");
            }

            var element = await TryReadEbmlElementHeaderAsync(reader, pos, endOffset, ct)
                ?? throw new DomainException("Invalid WebM Tracks framing.");

            if (element.IsUnknownSize || element.EndOffset <= pos)
            {
                throw new DomainException("Invalid WebM TrackEntry sizing.");
            }

            if (element.ElementId == WebmTrackEntryElementIdValue)
            {
                await ScanWebmTrackEntryAsync(
                    reader,
                    element.PayloadOffset,
                    element.EndOffset,
                    depth + 1,
                    counter,
                    audioTrackNumbers,
                    ct);
            }

            pos = element.EndOffset;
        }
    }

    private static async Task ScanWebmTrackEntryAsync(
        BoundedStorageReader reader,
        long startOffset,
        long endOffset,
        int depth,
        BoxCounter counter,
        HashSet<ulong> audioTrackNumbers,
        CancellationToken ct)
    {
        if (depth > MaxContainerDepth)
        {
            throw new DomainException("WebM TrackEntry nesting exceeds maximum allowed depth.");
        }

        ulong trackNumber = 0;
        ulong trackType = 0;
        var pos = startOffset;

        while (pos < endOffset)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("WebM TrackEntry exceeds maximum inspectable element count.");
            }

            var element = await TryReadEbmlElementHeaderAsync(reader, pos, endOffset, ct)
                ?? throw new DomainException("Invalid WebM TrackEntry framing.");

            if (element.IsUnknownSize || element.EndOffset <= pos)
            {
                throw new DomainException("Invalid WebM TrackEntry element sizing.");
            }

            if (element.ElementId == WebmTrackNumberElementIdValue && element.PayloadLength is >= 1 and <= 8)
            {
                trackNumber = await ReadEbmlUnsignedIntAsync(reader, element.PayloadOffset, (int)element.PayloadLength, ct);
            }
            else if (element.ElementId == WebmTrackTypeElementIdValue && element.PayloadLength is >= 1 and <= 8)
            {
                trackType = await ReadEbmlUnsignedIntAsync(reader, element.PayloadOffset, (int)element.PayloadLength, ct);
                if (trackType == 1)
                {
                    throw new DomainException("Video tracks are not permitted in WebM voice recordings.");
                }
            }

            pos = element.EndOffset;
        }

        if (trackType == 2 && trackNumber > 0)
        {
            audioTrackNumbers.Add(trackNumber);
        }
    }

    private static async Task<bool> ScanWebmClusterForAudioBlockAsync(
        BoundedStorageReader reader,
        EbmlElementHeader cluster,
        HashSet<ulong> audioTrackNumbers,
        BoxCounter counter,
        CancellationToken ct)
    {
        var pos = cluster.PayloadOffset;
        var clusterEnd = cluster.EndOffset;

        while (pos < clusterEnd)
        {
            counter.Count++;
            if (counter.Count > MaxContainerBoxesVisited)
            {
                throw new DomainException("WebM Cluster exceeds maximum inspectable element count.");
            }

            var element = await TryReadEbmlElementHeaderAsync(reader, pos, clusterEnd, ct);
            if (element is null)
            {
                if (cluster.IsUnknownSize)
                {
                    break;
                }

                throw new DomainException("Invalid WebM Cluster element framing.");
            }

            var el = element.Value;
            if (cluster.IsUnknownSize && IsWebmSegmentLevelElementId(el.ElementId))
            {
                break;
            }

            if (el.IsUnknownSize || el.EndOffset <= pos)
            {
                break;
            }

            if (el.ElementId == WebmSimpleBlockElementIdValue)
            {
                if (await BlockTargetsAudioTrackAsync(reader, el, audioTrackNumbers, ct))
                {
                    return true;
                }
            }
            else if (el.ElementId == WebmBlockGroupElementIdValue)
            {
                var groupPos = el.PayloadOffset;
                while (groupPos < el.EndOffset)
                {
                    counter.Count++;
                    if (counter.Count > MaxContainerBoxesVisited)
                    {
                        throw new DomainException("WebM BlockGroup exceeds maximum inspectable element count.");
                    }

                    var child = await TryReadEbmlElementHeaderAsync(reader, groupPos, el.EndOffset, ct)
                        ?? throw new DomainException("Invalid WebM BlockGroup framing.");

                    if (child.IsUnknownSize || child.EndOffset <= groupPos)
                    {
                        throw new DomainException("Invalid WebM BlockGroup element sizing.");
                    }

                    if (child.ElementId == WebmBlockElementIdValue &&
                        await BlockTargetsAudioTrackAsync(reader, child, audioTrackNumbers, ct))
                    {
                        return true;
                    }

                    groupPos = child.EndOffset;
                }
            }

            pos = el.EndOffset;
        }

        return false;
    }

    private static async Task<bool> BlockTargetsAudioTrackAsync(
        BoundedStorageReader reader,
        EbmlElementHeader blockElement,
        HashSet<ulong> audioTrackNumbers,
        CancellationToken ct)
    {
        if (blockElement.PayloadLength < 5)
        {
            return false;
        }

        var probeLen = (int)Math.Min(12, blockElement.PayloadLength);
        var probe = new byte[probeLen];
        if (!await reader.ReadExactAsync(blockElement.PayloadOffset, probe, ct))
        {
            return false;
        }

        var pos = 0;
        if (!TryReadEbmlVintSize(probe, ref pos, out var trackNumber))
        {
            return false;
        }

        // Block payload must contain track VINT + 2-byte relative timecode + 1-byte flags + at least 1 audio byte.
        return blockElement.PayloadLength > pos + 3 && audioTrackNumbers.Contains(trackNumber);
    }

    private static async Task<ulong> ReadEbmlUnsignedIntAsync(
        BoundedStorageReader reader,
        long offset,
        int length,
        CancellationToken ct)
    {
        var bytes = new byte[length];
        if (!await reader.ReadExactAsync(offset, bytes, ct))
        {
            throw new DomainException("Truncated WebM EBML integer element.");
        }

        ulong value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static bool IsWebmSegmentLevelElementId(uint elementId) =>
        elementId is WebmClusterElementIdValue
            or WebmTracksElementIdValue
            or WebmSeekHeadElementIdValue
            or WebmInfoElementIdValue
            or WebmCuesElementIdValue
            or WebmTagsElementIdValue;

    private readonly record struct EbmlElementHeader(
        uint ElementId,
        long PayloadOffset,
        long EndOffset,
        bool IsUnknownSize)
    {
        public long PayloadLength => EndOffset - PayloadOffset;
    }

    private static async Task<EbmlElementHeader?> TryReadEbmlElementHeaderAsync(
        BoundedStorageReader reader,
        long offset,
        long limitEnd,
        CancellationToken ct)
    {
        if (offset < 0 || offset >= limitEnd)
        {
            return null;
        }

        var probeLen = (int)Math.Min(12, limitEnd - offset);
        var probe = new byte[probeLen];
        var read = await reader.ReadAsync(offset, probe, ct);
        if (read < 2)
        {
            return null;
        }

        var span = probe.AsSpan(0, read);
        var pos = 0;
        if (!TryReadEbmlElementId(span, ref pos, out var idStart, out var idLen) ||
            !TryReadEbmlVintSizeOrUnknown(span, ref pos, out var elementSize, out var isUnknownSize))
        {
            return null;
        }

        uint elementId = 0;
        for (var i = 0; i < idLen; i++)
        {
            elementId = (elementId << 8) | span[idStart + i];
        }

        var payloadOffset = offset + pos;
        if (isUnknownSize)
        {
            return new EbmlElementHeader(elementId, payloadOffset, limitEnd, IsUnknownSize: true);
        }

        if (elementSize > (ulong)(limitEnd - payloadOffset))
        {
            return null;
        }

        return new EbmlElementHeader(elementId, payloadOffset, payloadOffset + (long)elementSize, IsUnknownSize: false);
    }

    private sealed class BoundedStorageReader
    {
        private readonly IMediaStorage _storage;
        private readonly string _objectKey;
        private readonly string? _ifMatchETag;
        private readonly Dictionary<long, byte[]> _pages = [];
        private int _rangeReads;
        private int _totalBytesRead;

        public long TotalLength { get; }

        public BoundedStorageReader(
            IMediaStorage storage,
            string objectKey,
            long totalLength,
            string? ifMatchETag,
            byte[] initialZeroOffsetPage)
        {
            _storage = storage;
            _objectKey = objectKey;
            TotalLength = totalLength;
            _ifMatchETag = ifMatchETag;
            _pages[0] = initialZeroOffsetPage;
            _rangeReads = 1;
            _totalBytesRead = initialZeroOffsetPage.Length;
        }

        public async Task<bool> ReadExactAsync(long offset, Memory<byte> destination, CancellationToken ct) =>
            await ReadAsync(offset, destination, ct) == destination.Length;

        public async Task<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct)
        {
            if (offset < 0 || offset >= TotalLength || destination.IsEmpty)
            {
                return 0;
            }

            var want = (int)Math.Min(destination.Length, TotalLength - offset);
            var copied = 0;

            while (copied < want)
            {
                var currentOffset = offset + copied;
                var pageStart = (currentOffset / HeaderInspectBytes) * HeaderInspectBytes;

                if (!_pages.TryGetValue(pageStart, out var page))
                {
                    if (_rangeReads >= MaxContainerRangeReads)
                    {
                        throw new DomainException("Media container metadata exceeds maximum inspectable read budget.");
                    }

                    var pageLen = (int)Math.Min(HeaderInspectBytes, TotalLength - pageStart);
                    if (_totalBytesRead + pageLen > MaxContainerMetadataBytes)
                    {
                        throw new DomainException("Media container metadata exceeds maximum inspectable byte budget.");
                    }

                    page = await _storage.ReadByteRangeAsync(_objectKey, pageStart, pageLen, _ifMatchETag, ct);
                    _rangeReads++;
                    _totalBytesRead += page.Length;

                    if (page.Length == 0)
                    {
                        throw new DomainException("Media container metadata read encountered unexpected end of stream.");
                    }

                    _pages[pageStart] = page;
                }

                var inPageOffset = (int)(currentOffset - pageStart);
                if (inPageOffset >= page.Length)
                {
                    break;
                }

                var take = Math.Min(want - copied, page.Length - inPageOffset);
                page.AsSpan(inPageOffset, take).CopyTo(destination.Span.Slice(copied, take));
                copied += take;
            }

            return copied;
        }
    }

    private static bool TryReadEbmlVintSizeOrUnknown(
        ReadOnlySpan<byte> buffer,
        ref int pos,
        out ulong value,
        out bool isUnknownSize)
    {
        isUnknownSize = false;
        if (pos >= buffer.Length)
        {
            value = 0;
            return false;
        }

        var startPos = pos;
        if (!TryReadEbmlVintSize(buffer, ref pos, out value))
        {
            return false;
        }

        var width = pos - startPos;
        var unknownSentinel = width < 8 ? ((1UL << (width * 7)) - 1UL) : 0x00FF_FFFF_FFFF_FFFFUL;
        if (value == unknownSentinel)
        {
            isUnknownSize = true;
        }

        return true;
    }

    private static void ValidateOggAudioHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < 29 || !header.StartsWith(OggSignature))
        {
            throw new DomainException("Invalid Ogg container signature.");
        }

        // Ogg stream_structure_version must be 0x00 and first page must set BOS flag (0x02)
        if (header[4] != 0x00 || (header[5] & 0x02) == 0)
        {
            throw new DomainException("Ogg voice recording must start with a valid Beginning-of-Stream page.");
        }

        var pageSegments = header[26];
        var headerTableEnd = 27 + pageSegments;
        if (pageSegments < 1 || headerTableEnd > header.Length)
        {
            throw new DomainException("Invalid Ogg page segment table.");
        }

        var firstPacketSize = 0;
        var foundTerminator = false;
        for (var i = 0; i < pageSegments; i++)
        {
            var segLen = header[27 + i];
            firstPacketSize += segLen;
            if (segLen < 255)
            {
                foundTerminator = true;
                break;
            }
        }

        if (!foundTerminator || firstPacketSize < 19 || headerTableEnd + firstPacketSize > header.Length)
        {
            throw new DomainException("Ogg voice recording is missing a complete identification header packet.");
        }

        var packet = header.Slice(headerTableEnd, firstPacketSize);
        if (packet.StartsWith("OpusHead"u8))
        {
            // RFC 7845 §5.1: OpusHead >= 19 bytes; version major nibble == 0; channels >= 1
            var version = packet[8];
            var channels = packet[9];
            var mappingFamily = packet[18];
            if ((version & 0xF0) != 0 || channels < 1)
            {
                throw new DomainException("Invalid Ogg Opus identification header.");
            }

            if (mappingFamily != 0)
            {
                if (packet.Length < 21 + channels)
                {
                    throw new DomainException("Incomplete Ogg Opus channel mapping header.");
                }

                var streamCount = packet[19];
                var coupledCount = packet[20];
                if (streamCount < 1 || coupledCount > streamCount)
                {
                    throw new DomainException("Invalid Ogg Opus channel mapping configuration.");
                }
            }

            return;
        }

        if (packet.StartsWith("\x01vorbis"u8))
        {
            // Vorbis I §4.2.2: identification header is exactly 30 bytes
            if (packet.Length != 30)
            {
                throw new DomainException("Invalid Ogg Vorbis identification header length.");
            }

            var vorbisVersion = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(7, 4));
            var channels = packet[11];
            var sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(12, 4));
            var blocksize0 = packet[28] & 0x0F;
            var blocksize1 = (packet[28] >> 4) & 0x0F;
            var framingFlag = packet[29] & 0x01;
            if (vorbisVersion != 0 ||
                channels < 1 ||
                sampleRate == 0 ||
                blocksize0 is < 6 or > 13 ||
                blocksize1 is < 6 or > 13 ||
                blocksize0 > blocksize1 ||
                framingFlag != 1)
            {
                throw new DomainException("Invalid Ogg Vorbis identification header.");
            }

            return;
        }

        throw new DomainException("Ogg voice recording must contain an Opus or Vorbis audio stream.");
    }

    private static void ValidateMovHeader(ReadOnlySpan<byte> header, long totalContentLength)
    {
        var offset = 0;
        for (var boxIndex = 0; boxIndex < 8; boxIndex++)
        {
            if (offset >= header.Length)
            {
                break;
            }

            var remainingHeader = header[offset..];
            var remainingTotal = totalContentLength - offset;
            if (!TryValidateIsoBmffBox(remainingHeader, remainingTotal, out var boxType, out var payloadOffset, out var boxSize) ||
                !AllowedMovRootBoxes.Contains(boxType))
            {
                throw new DomainException("Invalid QuickTime MOV container signature.");
            }

            if (string.Equals(boxType, "ftyp", StringComparison.Ordinal))
            {
                if (boxSize == 0 || boxSize < payloadOffset + 4 || remainingHeader.Length < payloadOffset + 4 ||
                    !IsPrintableAscii(remainingHeader.Slice(payloadOffset, 4)))
                {
                    throw new DomainException("Invalid MOV 'ftyp' major brand.");
                }

                var majorBrand = Encoding.ASCII.GetString(remainingHeader.Slice(payloadOffset, 4));
                if (RejectedMovFtypBrands.Contains(majorBrand))
                {
                    throw new DomainException($"Media container brand '{majorBrand}' is not permitted as MOV.");
                }

                return;
            }

            if (boxType is "moov" or "mdat")
            {
                return;
            }

            // Padding/preview boxes (free/skip/wide/pnot) cannot use rawSize == 0 and must yield to a real movie box within the 4 KB header.
            if (boxSize <= 0 || boxSize > remainingHeader.Length)
            {
                throw new DomainException("Invalid QuickTime MOV container signature.");
            }

            offset += (int)boxSize;
        }

        throw new DomainException("Invalid QuickTime MOV container signature.");
    }

    private static bool TryValidateIsoBmffBox(
        ReadOnlySpan<byte> header,
        long totalContentLength,
        out string boxType,
        out int payloadOffset,
        out long boxSize)
    {
        boxType = string.Empty;
        payloadOffset = 8;
        boxSize = 0;

        if (header.Length < 8 || totalContentLength < 8)
        {
            return false;
        }

        var rawSize = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
        var typeSpan = header.Slice(4, 4);
        if (!IsPrintableAscii(typeSpan))
        {
            return false;
        }

        boxType = Encoding.ASCII.GetString(typeSpan);

        if (rawSize == 0)
        {
            payloadOffset = 8;
            boxSize = 0;
            return true;
        }

        if (rawSize == 1)
        {
            if (header.Length < 16 || totalContentLength < 16)
            {
                return false;
            }

            var extendedSize = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(8, 8));
            if (extendedSize < 16 || extendedSize > (ulong)totalContentLength)
            {
                return false;
            }

            payloadOffset = 16;
            boxSize = (long)extendedSize;
            return true;
        }

        if (rawSize < 8 || (long)rawSize > totalContentLength)
        {
            return false;
        }

        payloadOffset = 8;
        boxSize = rawSize;
        return true;
    }

    private static long ValidateWebmHeader(ReadOnlySpan<byte> header, long? totalContentLength = null)
    {
        if (!header.StartsWith(WebmSignature))
        {
            throw new DomainException("Invalid WebM EBML header signature.");
        }

        var pos = 4;
        if (!TryReadEbmlVintSize(header, ref pos, out var ebmlHeaderSize) ||
            (totalContentLength.HasValue && ebmlHeaderSize > (ulong)Math.Max(0, totalContentLength.Value - pos)))
        {
            throw new DomainException("Corrupt WebM EBML header size.");
        }

        var headerEndOffset = checked(pos + (long)ebmlHeaderSize);
        var boundedHeaderSize = (int)Math.Min(ebmlHeaderSize, (ulong)(header.Length - pos));
        var ebmlEnd = Math.Min(header.Length, pos + boundedHeaderSize);
        while (pos < ebmlEnd)
        {
            if (!TryReadEbmlElementId(header, ref pos, out var idStart, out var idLen) ||
                !TryReadEbmlVintSize(header, ref pos, out var elementSize))
            {
                break;
            }

            var idSpan = header.Slice(idStart, idLen);
            if (elementSize > (ulong)(ebmlEnd - pos))
            {
                break;
            }

            var payloadLen = (int)elementSize;
            if (idSpan.SequenceEqual(EbmlDocTypeElementId))
            {
                var docType = Encoding.ASCII.GetString(header.Slice(pos, payloadLen)).TrimEnd('\0');
                if (string.Equals(docType, "webm", StringComparison.Ordinal))
                {
                    return headerEndOffset;
                }

                throw new DomainException($"Unsupported EBML DocType '{docType}' for WebM video.");
            }

            pos += payloadLen;
        }

        throw new DomainException("WebM video header is missing required 'webm' DocType.");
    }

    private static bool TryReadEbmlElementId(ReadOnlySpan<byte> buffer, ref int pos, out int start, out int length)
    {
        start = pos;
        length = 0;
        if (pos >= buffer.Length)
        {
            return false;
        }

        var first = buffer[pos];
        if (first == 0)
        {
            return false;
        }

        var width = 1;
        byte mask = 0x80;
        while (width <= 4 && (first & mask) == 0)
        {
            mask >>= 1;
            width++;
        }

        if (width > 4 || pos + width > buffer.Length)
        {
            return false;
        }

        length = width;
        pos += width;
        return true;
    }

    private static bool TryReadEbmlVintSize(ReadOnlySpan<byte> buffer, ref int pos, out ulong value)
    {
        value = 0;
        if (pos >= buffer.Length)
        {
            return false;
        }

        var first = buffer[pos];
        if (first == 0)
        {
            return false;
        }

        var width = 1;
        byte mask = 0x80;
        while (width <= 8 && (first & mask) == 0)
        {
            mask >>= 1;
            width++;
        }

        if (width > 8 || pos + width > buffer.Length)
        {
            return false;
        }

        value = (ulong)(first & ~mask);
        for (var i = 1; i < width; i++)
        {
            value = (value << 8) | buffer[pos + i];
        }

        pos += width;
        return true;
    }

    private static void ValidatePlainTextHeader(ReadOnlySpan<byte> header, long totalContentLength)
    {
        if (header.IndexOf((byte)0x00) >= 0)
        {
            throw new DomainException("Text file cannot contain binary NUL bytes.");
        }

        if (header.StartsWith("MZ"u8) ||
            header.StartsWith(ElfSignature) ||
            header.StartsWith("%PDF-"u8) ||
            header.StartsWith(PkLocalHeader))
        {
            throw new DomainException("Binary file signature is not permitted in text documents.");
        }

        // Strip UTF-8 BOM (EF BB BF) if present
        if (header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
        {
            header = header[3..];
        }

        // When reading a partial 4 KB prefix of a larger file, trim an incomplete trailing UTF-8 sequence
        if (totalContentLength > HeaderInspectBytes && header.Length > 0)
        {
            header = TrimTrailingIncompleteUtf8Sequence(header);
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(header);
        }
        catch (DecoderFallbackException ex)
        {
            throw new DomainException($"Text file is not valid UTF-8 encoding: {ex.Message}");
        }

        var trimmedStart = decoded.TrimStart();
        foreach (var forbiddenPrefix in ActiveHtmlXmlPrefixes)
        {
            if (trimmedStart.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException("Active markup or script content is not permitted in text documents.");
            }
        }
    }

    private static ReadOnlySpan<byte> TrimTrailingIncompleteUtf8Sequence(ReadOnlySpan<byte> span)
    {
        var len = span.Length;
        if (len == 0 || span[len - 1] < 0x80)
        {
            return span;
        }

        // Walk back over at most 3 trailing continuation bytes (10xxxxxx) to inspect the lead byte
        var contCount = 0;
        var idx = len - 1;
        while (idx >= 0 && contCount < 3 && (span[idx] & 0xC0) == 0x80)
        {
            contCount++;
            idx--;
        }

        if (idx < 0)
        {
            return span;
        }

        var leadByte = span[idx];
        int expectedSeqLen;
        if ((leadByte & 0xE0) == 0xC0)
        {
            expectedSeqLen = 2;
        }
        else if ((leadByte & 0xF0) == 0xE0)
        {
            expectedSeqLen = 3;
        }
        else if ((leadByte & 0xF8) == 0xF0)
        {
            expectedSeqLen = 4;
        }
        else
        {
            return span;
        }

        var actualAvailable = len - idx;
        return actualAvailable < expectedSeqLen ? span[..idx] : span;
    }

    private static bool IsPrintableAscii(ReadOnlySpan<byte> span)
    {
        foreach (var b in span)
        {
            if (b < 0x20 || b > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static FileStream CreateDeleteOnCloseTempStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aos-media-verify-{Guid.NewGuid():N}.tmp");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }
}
