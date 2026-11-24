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
/// and normalizing orientation via <c>Magick.NET</c>) plus bounded 4 KB magic-byte inspection for videos and documents.
/// </summary>
public sealed class MediaVerifier : IMediaVerifier, IDisposable
{
    private const int HeaderInspectBytes = 4096;

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

    private static readonly Lock ResourceLimitsLock = new();

    private readonly IMediaStorage _storage;
    private readonly MediaOptions _options;
    private readonly ILogger<MediaVerifier> _logger;
    private readonly SemaphoreSlim _imageSemaphore;
    private readonly long _maxEncodedImageBytes;

    static MediaVerifier()
    {
        lock (ResourceLimitsLock)
        {
            ResourceLimits.Width = (ulong)MediaConstraints.MaxImageWidth;
            ResourceLimits.Height = (ulong)MediaConstraints.MaxImageHeight;
            ResourceLimits.Area = 256UL * 1024 * 1024;
            ResourceLimits.Memory = 512UL * 1024 * 1024;
            ResourceLimits.Disk = 1024UL * 1024 * 1024;
            ResourceLimits.MaxMemoryRequest = 256UL * 1024 * 1024;
            ResourceLimits.Thread = 1UL;
            ResourceLimits.Time = 15UL;
        }
    }

    [ActivatorUtilitiesConstructor]
    public MediaVerifier(
        IMediaStorage storage,
        IOptions<MediaOptions> options,
        ILogger<MediaVerifier>? logger = null)
        : this(storage, options, logger ?? NullLogger<MediaVerifier>.Instance, MediaConstraints.MaxImageBytes)
    {
    }

    internal MediaVerifier(
        IMediaStorage storage,
        IOptions<MediaOptions> options,
        ILogger<MediaVerifier> logger,
        long maxEncodedImageBytes)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEncodedImageBytes);

        _storage = storage;
        _options = options.Value;
        _logger = logger;
        _maxEncodedImageBytes = maxEncodedImageBytes;

        var concurrency = Math.Max(1, _options.MaxImageProcessingConcurrency);
        _imageSemaphore = new SemaphoreSlim(concurrency, concurrency);
        if (_options.ImageProcessingTimeoutSeconds is >= 1 and <= 60)
        {
            lock (ResourceLimitsLock)
            {
                ResourceLimits.Time = (ulong)_options.ImageProcessingTimeoutSeconds;
            }
        }
    }

    public async Task<VerifiedMediaResult> VerifyAndSanitizeAsync(
        Media media,
        MediaObjectMetadata stagingMetadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(stagingMetadata);

        var (expectedKind, canonicalMime, maxSizeBytes) =
            MediaConstraints.ResolveAllowedMedia(media.SanitizedFileName, media.DeclaredContentType);

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
            _ => throw new DomainException($"Unsupported media kind '{media.Kind}'.")
        };
    }

    public void Dispose()
    {
        _imageSemaphore.Dispose();
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

        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.ImageProcessingTimeoutSeconds));
        var acquired = await _imageSemaphore.WaitAsync(timeout, ct);
        if (!acquired)
        {
            throw new ConflictException("Image processing capacity is currently busy. Please try again.");
        }

        FileStream? outputStream = null;
        try
        {
            outputStream = CreateDeleteOnCloseTempStream();
            var (finalWidth, finalHeight) = await Task.Run(
                () => ProcessImageLocked(
                    inputStream,
                    outputStream,
                    expectedFormat,
                    animationScan,
                    maxFrames,
                    ct),
                ct);

            await outputStream.FlushAsync(ct);
            outputStream.Seek(0, SeekOrigin.Begin);

            var ownershipStream = outputStream;
            outputStream = null;

            return new VerifiedMediaResult(
                sniffedMime,
                ownershipStream.Length,
                finalWidth,
                finalHeight,
                durationMs: null,
                processedStream: ownershipStream);
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

            // Release semaphore only after all native CPU work inside Task.Run has completed.
            _imageSemaphore.Release();
        }
    }

    private (int Width, int Height) ProcessImageLocked(
        FileStream inputStream,
        FileStream outputStream,
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
        ValidateImageDimensions(checked((int)info.Width), checked((int)info.Height));

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
                decodeSettings.FrameCount = (uint)maxFrames;
                return ProcessAnimationCollectionLocked(inputStream, outputStream, pinnedFormat, decodeSettings, maxFrames, ct);
            }

            using var image = new MagickImage(inputStream, decodeSettings);
            ct.ThrowIfCancellationRequested();
            image.AutoOrient();

            // Read upright dimensions strictly AFTER AutoOrient()
            var finalWidth = checked((int)image.Width);
            var finalHeight = checked((int)image.Height);
            ValidateImageDimensions(finalWidth, finalHeight);

            StripSingleImageMetadata(image);
            EncodeSingleImageWithinLimit(image, pinnedFormat, outputStream, ct);
            return (finalWidth, finalHeight);
        }
        catch (MagickException ex) when (IsTransientResourceExhaustion(ex, headersValidated))
        {
            _logger.LogWarning(
                "Transient ImageMagick resource limit hit while processing valid-header image: {ErrorType}",
                ex.GetType().Name);
            throw new ConflictException("Image processing capacity is currently busy. Please try again.");
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

            var rawWidth = checked((int)frame.Width);
            var rawHeight = checked((int)frame.Height);
            ValidateImageDimensions(rawWidth, rawHeight);

            actualTotalPixels += (long)rawWidth * rawHeight;
            if (actualTotalPixels > _options.MaxTotalDecodedImagePixels)
            {
                throw new DomainException("Total decoded animation pixel count exceeds the maximum allowed budget.");
            }

            frame.AutoOrient();

            var uprightWidth = checked((int)frame.Width);
            var uprightHeight = checked((int)frame.Height);
            ValidateImageDimensions(uprightWidth, uprightHeight);
            maxWidth = Math.Max(maxWidth, uprightWidth);
            maxHeight = Math.Max(maxHeight, uprightHeight);

            StripSingleImageMetadata(frame);
        }

        ValidateImageDimensions(maxWidth, maxHeight);

        EncodeCollectionWithinLimit(collection, pinnedFormat, outputStream, ct);
        return (maxWidth, maxHeight);
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

    private static void ValidateImageDimensions(int width, int height)
    {
        if (width <= 0 || width > MediaConstraints.MaxImageWidth ||
            height <= 0 || height > MediaConstraints.MaxImageHeight)
        {
            throw new DomainException(
                $"Image dimensions must not exceed {MediaConstraints.MaxImageWidth}x{MediaConstraints.MaxImageHeight} pixels.");
        }

        if ((long)width * height > MediaConstraints.MaxImagePixels)
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

    private static void ValidateWebmHeader(ReadOnlySpan<byte> header)
    {
        if (!header.StartsWith(WebmSignature))
        {
            throw new DomainException("Invalid WebM EBML header signature.");
        }

        var pos = 4;
        if (!TryReadEbmlVintSize(header, ref pos, out var ebmlHeaderSize))
        {
            throw new DomainException("Corrupt WebM EBML header size.");
        }

        var boundedHeaderSize = (int)Math.Min(ebmlHeaderSize, (ulong)header.Length);
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
                    return;
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
