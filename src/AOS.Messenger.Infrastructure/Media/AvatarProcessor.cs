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
/// Validates staged user avatar images via strict magic-byte sniffing and <c>Magick.NET</c>,
/// performing a single decode to emit EXIF-stripped WebP full (<=1024px) and 128x128 thumbnail streams.
/// Always re-encodes to neutralize trailing polyglot payloads and rejects animated inputs.
/// </summary>
public sealed class AvatarProcessor : IAvatarProcessor, IDisposable
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly IMediaStorage _storage;
    private readonly ImageProcessingGate _gate;
    private readonly bool _ownsGate;
    private readonly ILogger<AvatarProcessor> _logger;

    [ActivatorUtilitiesConstructor]
    public AvatarProcessor(
        IMediaStorage storage,
        ImageProcessingGate gate,
        ILogger<AvatarProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(gate);

        _storage = storage;
        _gate = gate;
        _ownsGate = false;
        _logger = logger ?? NullLogger<AvatarProcessor>.Instance;
    }

    internal AvatarProcessor(
        IMediaStorage storage,
        IOptions<MediaOptions> options,
        ILogger<AvatarProcessor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(options);

        _storage = storage;
        _gate = new ImageProcessingGate(options);
        _ownsGate = true;
        _logger = logger ?? NullLogger<AvatarProcessor>.Instance;
    }

    public async Task<ProcessedAvatarResult> ProcessAvatarAsync(
        PendingAvatarUpload upload,
        MediaObjectMetadata stagingMetadata,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(upload);
        ArgumentNullException.ThrowIfNull(stagingMetadata);

        if (stagingMetadata.ContentLength <= 0 || stagingMetadata.ContentLength > AvatarConstraints.MaxRawAvatarUploadBytes)
        {
            throw new DomainException(
                $"Profile photo size must be between 1 byte and {AvatarConstraints.MaxRawAvatarUploadBytes} bytes.");
        }

        await using var inputStream = CreateDeleteOnCloseTempStream();
        await _storage.DownloadToStreamAsync(
            upload.ObjectKey,
            inputStream,
            AvatarConstraints.MaxRawAvatarUploadBytes,
            stagingMetadata.ETag,
            ct);
        await inputStream.FlushAsync(ct);

        if (inputStream.Length <= 0)
        {
            throw new DomainException("Uploaded profile photo is empty.");
        }

        var (sniffedMime, pinnedFormat) = SniffAllowedStaticImageSignature(inputStream);
        if (!string.Equals(sniffedMime, upload.DeclaredContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException(
                $"Profile photo content '{sniffedMime}' does not match expected type '{upload.DeclaredContentType}'.");
        }

        if (pinnedFormat == MagickFormat.WebP)
        {
            var scan = ImageFrameCounter.ScanWebp(inputStream, maxFrames: 2, maxTotalPixels: MediaConstraints.MaxImagePixels);
            if (scan.FrameCount > 1)
            {
                throw new DomainException("Animated WebP images are not supported for profile photos.");
            }
        }

        var acquired = await _gate.Semaphore.WaitAsync(_gate.Timeout, ct);
        if (!acquired)
        {
            throw new ConflictException("Image processing capacity is currently busy. Please try again.");
        }

        FileStream? fullStream = null;
        FileStream? thumbStream = null;
        try
        {
            fullStream = CreateDeleteOnCloseTempStream();
            thumbStream = CreateDeleteOnCloseTempStream();

            await Task.Run(
                () => ProcessAvatarLocked(inputStream, fullStream, thumbStream, pinnedFormat, ct),
                ct);

            await fullStream.FlushAsync(ct);
            await thumbStream.FlushAsync(ct);
            fullStream.Seek(0, SeekOrigin.Begin);
            thumbStream.Seek(0, SeekOrigin.Begin);

            var ownedFull = fullStream;
            var ownedThumb = thumbStream;
            fullStream = null;
            thumbStream = null;

            return new ProcessedAvatarResult(
                AvatarConstraints.CanonicalAvatarContentType,
                inputStream.Length,
                ownedFull,
                ownedFull.Length,
                ownedThumb,
                ownedThumb.Length);
        }
        catch (MagickException ex)
        {
            _logger.LogWarning("Rejected invalid avatar image for upload {UploadId}: {ErrorType}", upload.Id.Value, ex.GetType().Name);
            throw new DomainException("The uploaded file is not a valid or supported image.");
        }
        finally
        {
            if (fullStream is not null) await fullStream.DisposeAsync();
            if (thumbStream is not null) await thumbStream.DisposeAsync();
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

    private void ProcessAvatarLocked(
        FileStream inputStream,
        FileStream fullStream,
        FileStream thumbStream,
        MagickFormat pinnedFormat,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var pingSettings = new MagickReadSettings { Format = pinnedFormat };
        var headersValidated = false;
        inputStream.Seek(0, SeekOrigin.Begin);
        var info = new MagickImageInfo(inputStream, pingSettings);
        ValidateImageDimensions(checked((int)info.Width), checked((int)info.Height));
        headersValidated = true;

        ct.ThrowIfCancellationRequested();
        inputStream.Seek(0, SeekOrigin.Begin);
        var decodeSettings = new MagickReadSettings { Format = pinnedFormat };

        try
        {
            using var decodedImage = LoadStaticImage(inputStream, pinnedFormat, decodeSettings);
            ct.ThrowIfCancellationRequested();

            decodedImage.AutoOrient();

            var uprightWidth = checked((int)decodedImage.Width);
            var uprightHeight = checked((int)decodedImage.Height);
            ValidateImageDimensions(uprightWidth, uprightHeight);

            if (uprightWidth != uprightHeight)
            {
                var side = Math.Min(uprightWidth, uprightHeight);
                var offsetX = (uprightWidth - side) / 2;
                var offsetY = (uprightHeight - side) / 2;
                decodedImage.Crop(new MagickGeometry(offsetX, offsetY, (uint)side, (uint)side));
                decodedImage.ResetPage();
            }

            StripSingleImageMetadata(decodedImage);

            // Generate 128x128 thumbnail BEFORE resizing full variant so thumbnail keeps full source crispness
            using (var thumbImage = decodedImage.Clone())
            {
                thumbImage.FilterType = FilterType.Lanczos;
                thumbImage.Resize(new MagickGeometry(
                    (uint)AvatarConstraints.ThumbnailAvatarDimension,
                    (uint)AvatarConstraints.ThumbnailAvatarDimension)
                {
                    IgnoreAspectRatio = true
                });

                EncodeWebpWithinLimit(
                    thumbImage,
                    thumbStream,
                    [80u, 70u, 55u],
                    AvatarConstraints.MaxThumbnailAvatarOutputBytes,
                    ct);
            }

            // Generate full variant (<= 1024x1024, never upscaled)
            var currentSide = checked((int)decodedImage.Width);
            if (currentSide > AvatarConstraints.MaxFullAvatarDimension)
            {
                decodedImage.FilterType = FilterType.Lanczos;
                decodedImage.Resize(new MagickGeometry(
                    (uint)AvatarConstraints.MaxFullAvatarDimension,
                    (uint)AvatarConstraints.MaxFullAvatarDimension)
                {
                    IgnoreAspectRatio = true
                });
            }

            EncodeWebpWithinLimit(
                decodedImage,
                fullStream,
                [88u, 78u, 65u],
                AvatarConstraints.MaxFullAvatarOutputBytes,
                ct);
        }
        catch (MagickException ex) when (MediaVerifier.IsTransientResourceExhaustion(ex, headersValidated))
        {
            _logger.LogWarning("Transient ImageMagick resource limit hit while processing avatar: {ErrorType}", ex.GetType().Name);
            throw new ConflictException("Image processing capacity is currently busy. Please try again.");
        }
    }

    private static MagickImage LoadStaticImage(FileStream inputStream, MagickFormat pinnedFormat, MagickReadSettings decodeSettings)
    {
        if (pinnedFormat == MagickFormat.WebP)
        {
            decodeSettings.FrameIndex = 0;
            decodeSettings.FrameCount = 2;
            using var collection = new MagickImageCollection(inputStream, decodeSettings);
            if (collection.Count != 1)
            {
                throw new DomainException("Animated WebP images are not supported for profile photos.");
            }

            return new MagickImage(collection[0]);
        }

        return new MagickImage(inputStream, decodeSettings);
    }

    private static void EncodeWebpWithinLimit(
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

        throw new DomainException($"Processed profile photo exceeds the maximum allowed {maxBytes} bytes.");
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

    private static (string CanonicalMime, MagickFormat Format) SniffAllowedStaticImageSignature(Stream stream)
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

        throw new DomainException("Profile photos must be JPEG, PNG, or static WebP images.");
    }

    private static FileStream CreateDeleteOnCloseTempStream()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aos-avatar-proc-{Guid.NewGuid():N}.tmp");
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
    }
}
