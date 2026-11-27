using System.Buffers.Binary;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Infrastructure.Storage;

internal readonly record struct AnimationScanResult(
    int FrameCount,
    int MaxWidth,
    int MaxHeight,
    long TotalPixels);

/// <summary>
/// Zero-allocation binary container scanners for pre-decode GIF and WebP animation frame counting
/// and per-frame dimension/pixel-budget enforcement before native image decoding.
/// </summary>
internal static class ImageFrameCounter
{
    public const int DefaultMaxChunksScanned = 4096;
    public const int DefaultMaxGifSubBlocks = 100_000;

    public static int CountGifFrames(Stream stream, int maxFrames, int maxChunks = DefaultMaxChunksScanned) =>
        ScanGif(stream, maxFrames, MediaConstraints.MaxTotalDecodedImagePixels, maxChunks).FrameCount;

    public static AnimationScanResult ScanGif(
        Stream stream,
        int maxFrames,
        long maxTotalPixels,
        int maxBlocks = DefaultMaxChunksScanned,
        int maxSubBlocks = DefaultMaxGifSubBlocks)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBlocks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSubBlocks);

        if (stream.Length > MediaConstraints.MaxImageBytes)
        {
            throw new DomainException($"GIF file size exceeds maximum allowed {MediaConstraints.MaxImageBytes} bytes.");
        }

        stream.Seek(0, SeekOrigin.Begin);

        Span<byte> header = stackalloc byte[13];
        if (!TryReadExact(stream, header))
        {
            throw new DomainException("Invalid or truncated GIF header.");
        }

        if (!header[..6].SequenceEqual("GIF87a"u8) && !header[..6].SequenceEqual("GIF89a"u8))
        {
            throw new DomainException("Invalid GIF file signature.");
        }

        var logicalWidth = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6, 2));
        var logicalHeight = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8, 2));
        ValidateFrameDimensions(logicalWidth, logicalHeight);

        var packed = header[10];
        if ((packed & 0x80) != 0)
        {
            var gctSize = 3L * (1L << ((packed & 0x07) + 1));
            SkipBytes(stream, gctSize, "Truncated GIF Global Color Table.");
        }

        var frames = 0;
        var blocksScanned = 0;
        var subBlocksScanned = 0;
        var maxWidth = (int)logicalWidth;
        var maxHeight = (int)logicalHeight;
        long totalPixels = 0;
        Span<byte> descriptor = stackalloc byte[9];

        while (true)
        {
            if (stream.Position >= stream.Length)
            {
                break;
            }

            blocksScanned++;
            if (blocksScanned > maxBlocks)
            {
                throw new DomainException("GIF block count exceeds maximum allowed structure limit.");
            }

            var iterStart = stream.Position;
            var introducer = stream.ReadByte();
            if (introducer < 0 || introducer == 0x3B)
            {
                break;
            }

            if (introducer == 0x00)
            {
                continue;
            }

            if (introducer == 0x2C)
            {
                frames++;
                if (frames > maxFrames)
                {
                    throw new DomainException($"GIF exceeds the maximum allowed {maxFrames} frames.");
                }

                if (!TryReadExact(stream, descriptor))
                {
                    throw new DomainException("Truncated GIF image descriptor.");
                }

                var frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(4, 2));
                var frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(descriptor.Slice(6, 2));
                ValidateFrameDimensions(frameWidth, frameHeight);

                totalPixels += (long)frameWidth * frameHeight;
                if (totalPixels > maxTotalPixels)
                {
                    throw new DomainException("Total decoded animation pixel count exceeds the maximum allowed budget.");
                }

                maxWidth = Math.Max(maxWidth, frameWidth);
                maxHeight = Math.Max(maxHeight, frameHeight);

                var imgPacked = descriptor[8];
                if ((imgPacked & 0x80) != 0)
                {
                    var lctSize = 3L * (1L << ((imgPacked & 0x07) + 1));
                    SkipBytes(stream, lctSize, "Truncated GIF Local Color Table.");
                }

                if (stream.ReadByte() < 0)
                {
                    throw new DomainException("Truncated GIF image data.");
                }

                SkipGifSubBlocks(stream, ref subBlocksScanned, maxSubBlocks);
            }
            else if (introducer == 0x21)
            {
                if (stream.ReadByte() < 0)
                {
                    throw new DomainException("Truncated GIF extension block.");
                }

                SkipGifSubBlocks(stream, ref subBlocksScanned, maxSubBlocks);
            }
            else
            {
                throw new DomainException($"Invalid GIF block introducer 0x{introducer:X2}.");
            }

            if (stream.Position <= iterStart)
            {
                throw new DomainException("Corrupt GIF block offset.");
            }
        }

        if (frames < 1)
        {
            throw new DomainException("GIF image contains no frames.");
        }

        return new AnimationScanResult(frames, maxWidth, maxHeight, totalPixels);
    }

    public static int CountWebpFrames(Stream stream, int maxFrames, int maxChunks = DefaultMaxChunksScanned) =>
        ScanWebp(stream, maxFrames, MediaConstraints.MaxTotalDecodedImagePixels, maxChunks).FrameCount;

    public static AnimationScanResult ScanWebp(
        Stream stream,
        int maxFrames,
        long maxTotalPixels,
        int maxChunks = DefaultMaxChunksScanned)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTotalPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChunks);

        if (stream.Length > MediaConstraints.MaxImageBytes)
        {
            throw new DomainException($"WebP file size exceeds maximum allowed {MediaConstraints.MaxImageBytes} bytes.");
        }

        stream.Seek(0, SeekOrigin.Begin);

        Span<byte> riffHeader = stackalloc byte[12];
        if (!TryReadExact(stream, riffHeader))
        {
            throw new DomainException("Invalid or truncated WebP header.");
        }

        if (!riffHeader[..4].SequenceEqual("RIFF"u8) || !riffHeader[8..12].SequenceEqual("WEBP"u8))
        {
            throw new DomainException("Invalid WebP RIFF signature.");
        }

        long riffPayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(riffHeader[4..8]);
        if (riffPayloadSize < 4)
        {
            throw new DomainException("Invalid WebP RIFF container size.");
        }

        long riffEnd = Math.Min(stream.Length, 8L + riffPayloadSize);

        var anmfFrames = 0;
        var chunksScanned = 0;
        var maxWidth = 0;
        var maxHeight = 0;
        long totalPixels = 0;
        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> vp8xHeader = stackalloc byte[10];
        Span<byte> anmfHeader = stackalloc byte[16];

        while (stream.Position < riffEnd)
        {
            chunksScanned++;
            if (chunksScanned > maxChunks)
            {
                throw new DomainException("WebP chunk count exceeds maximum allowed structure limit.");
            }

            var iterStart = stream.Position;
            if (riffEnd - iterStart < 8 || !TryReadExact(stream, chunkHeader))
            {
                throw new DomainException("Truncated WebP chunk header.");
            }

            var payloadStart = stream.Position;
            long payloadSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            long paddedSize = payloadSize + (payloadSize & 1L);

            if (paddedSize > riffEnd - payloadStart)
            {
                throw new DomainException("Truncated or oversized WebP chunk payload.");
            }

            if (chunkHeader[..4].SequenceEqual("VP8X"u8))
            {
                if (payloadSize < 10 || !TryReadExact(stream, vp8xHeader))
                {
                    throw new DomainException("Truncated WebP VP8X header.");
                }

                var canvasWidth = 1 + ReadUInt24LittleEndian(vp8xHeader.Slice(4, 3));
                var canvasHeight = 1 + ReadUInt24LittleEndian(vp8xHeader.Slice(7, 3));
                ValidateFrameDimensions(canvasWidth, canvasHeight);
                maxWidth = Math.Max(maxWidth, canvasWidth);
                maxHeight = Math.Max(maxHeight, canvasHeight);
            }
            else if (chunkHeader[..4].SequenceEqual("ANMF"u8))
            {
                anmfFrames++;
                if (anmfFrames > maxFrames)
                {
                    throw new DomainException($"Animated WebP exceeds the maximum allowed {maxFrames} frames.");
                }

                if (payloadSize < 16 || !TryReadExact(stream, anmfHeader))
                {
                    throw new DomainException("Invalid or truncated WebP ANMF frame header.");
                }

                var frameWidth = 1 + ReadUInt24LittleEndian(anmfHeader.Slice(6, 3));
                var frameHeight = 1 + ReadUInt24LittleEndian(anmfHeader.Slice(9, 3));
                ValidateFrameDimensions(frameWidth, frameHeight);

                totalPixels += (long)frameWidth * frameHeight;
                if (totalPixels > maxTotalPixels)
                {
                    throw new DomainException("Total decoded animation pixel count exceeds the maximum allowed budget.");
                }

                maxWidth = Math.Max(maxWidth, frameWidth);
                maxHeight = Math.Max(maxHeight, frameHeight);
            }

            var nextPos = Math.Min(stream.Length, payloadStart + paddedSize);
            stream.Position = nextPos;

            if (stream.Position <= iterStart)
            {
                throw new DomainException("Corrupt WebP chunk offset.");
            }
        }

        if (anmfFrames == 0)
        {
            totalPixels = maxWidth > 0 && maxHeight > 0 ? (long)maxWidth * maxHeight : 0L;
            return new AnimationScanResult(1, maxWidth, maxHeight, totalPixels);
        }

        return new AnimationScanResult(anmfFrames, maxWidth, maxHeight, totalPixels);
    }

    private static void SkipGifSubBlocks(Stream stream, ref int subBlocksScanned, int maxSubBlocks)
    {
        while (true)
        {
            subBlocksScanned++;
            if (subBlocksScanned > maxSubBlocks)
            {
                throw new DomainException("GIF sub-block count exceeds maximum allowed structure limit.");
            }

            var blockSize = stream.ReadByte();
            if (blockSize < 0)
            {
                throw new DomainException("Truncated GIF sub-block.");
            }

            if (blockSize == 0)
            {
                return;
            }

            SkipBytes(stream, blockSize, "Truncated GIF sub-block payload.");
        }
    }

    private static void ValidateFrameDimensions(int width, int height)
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

    private static int ReadUInt24LittleEndian(ReadOnlySpan<byte> bytes) =>
        bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);

    private static void SkipBytes(Stream stream, long byteCount, string errorMessage)
    {
        if (byteCount < 0 || byteCount > stream.Length - stream.Position)
        {
            throw new DomainException(errorMessage);
        }

        stream.Seek(byteCount, SeekOrigin.Current);
    }

    private static bool TryReadExact(Stream stream, Span<byte> destination)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                return false;
            }

            total += read;
        }

        return true;
    }
}
