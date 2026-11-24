using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Storage;
using ImageMagick;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase9MediaVerifierTests
{
    private static Media CreatePendingMedia(MediaKind kind, string fileName, string declaredMime, long sizeBytes) =>
        Media.CreatePending(
            UserId.New(),
            ConversationId.New(),
            kind,
            fileName,
            declaredMime,
            Math.Max(1, sizeBytes),
            DateTimeOffset.UtcNow);

    private static MediaVerifier CreateVerifier(
        IMediaStorage storage,
        MediaOptions? options = null,
        long? maxEncodedBytesOverride = null)
    {
        var opts = Options.Create(options ?? new MediaOptions());
        return maxEncodedBytesOverride.HasValue
            ? new MediaVerifier(storage, opts, NullLogger<MediaVerifier>.Instance, maxEncodedBytesOverride.Value)
            : new MediaVerifier(storage, opts, NullLogger<MediaVerifier>.Instance);
    }

    // =========================================================================
    // 1. Image Verification, Orientation Normalization & Metadata Stripping
    // =========================================================================

    [Fact]
    public async Task VerifyImage_RotatedJpegWithExifGpsXmpIptc_NormalizesOrientationAndStripsAllMetadata()
    {
        using var src = new MagickImage(new MagickColor(120, 60, 200), 40, 20);
        src.Orientation = Orientation.RightTop; // EXIF Orientation 6 (90 deg CW -> upright should become 20x40)
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.Make, "SecretCamera");
        exif.SetValue(ExifTag.GPSLatitude, [new ImageMagick.Rational(37, 1), new ImageMagick.Rational(46, 1), new ImageMagick.Rational(30, 1)]);
        src.SetProfile(exif);
        src.SetProfile(new XmpProfile("<x:xmpmeta>secret</x:xmpmeta>"u8.ToArray()));
        var iptc = new IptcProfile();
        iptc.SetValue(IptcTag.Caption, "Secret street address");
        src.SetProfile(iptc);

        using var ms = new MemoryStream();
        src.Quality = 90;
        src.Write(ms, MagickFormat.Jpeg);
        var rawBytes = ms.ToArray();

        var media = CreatePendingMedia(MediaKind.Image, "phone.jpg", "image/jpeg", rawBytes.Length);
        var storage = new StubMediaStorage(media.ObjectKey, rawBytes, "\"etag-jpg\"");
        using var verifier = CreateVerifier(storage);

        await using var result = await verifier.VerifyAndSanitizeAsync(
            media,
            new MediaObjectMetadata(rawBytes.Length, "\"etag-jpg\"", "image/jpeg"));

        Assert.Equal("image/jpeg", result.VerifiedContentType);
        Assert.Equal(20, result.Width);  // Swapped AFTER AutoOrient()
        Assert.Equal(40, result.Height);
        Assert.NotNull(result.ProcessedStream);
        Assert.Equal(0, result.ProcessedStream.Position);
        Assert.Equal(result.ProcessedStream.Length, result.VerifiedSizeBytes);

        // Verify zero sensitive metadata remains in re-encoded bytes
        using var reloaded = new MagickImage(result.ProcessedStream);
        Assert.Equal((uint)20, reloaded.Width);
        Assert.Equal((uint)40, reloaded.Height);
        Assert.True(reloaded.Orientation is Orientation.TopLeft or Orientation.Undefined);
        Assert.Null(reloaded.GetExifProfile());
        Assert.Null(reloaded.GetXmpProfile());
        Assert.Null(reloaded.GetIptcProfile());
        Assert.Null(reloaded.Comment);
    }

    [Fact]
    public async Task VerifyImage_PngTextDataAndWebpMetadata_AreStrippedCompletely()
    {
        // PNG with custom tEXt attribute + EXIF
        using var pngImg = new MagickImage(new MagickColor(10, 200, 100), 24, 16);
        pngImg.SetAttribute("Comment", "leak-author");
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "LeakMake");
        pngImg.SetProfile(exif);

        using var pngMs = new MemoryStream();
        pngImg.Write(pngMs, MagickFormat.Png);
        var pngBytes = pngMs.ToArray();

        var pngMedia = CreatePendingMedia(MediaKind.Image, "diagram.png", "image/png", pngBytes.Length);
        using var pngVerifier = CreateVerifier(new StubMediaStorage(pngMedia.ObjectKey, pngBytes, "\"e-png\""));

        await using var pngRes = await pngVerifier.VerifyAndSanitizeAsync(
            pngMedia,
            new MediaObjectMetadata(pngBytes.Length, "\"e-png\"", "image/png"));

        var outPngBytes = ReadAllBytes(pngRes.ProcessedStream!);
        Assert.DoesNotContain("leak-author", Encoding.ASCII.GetString(outPngBytes), StringComparison.Ordinal);

        using (var reloadedPng = new MagickImage(outPngBytes, MagickFormat.Png))
        {
            Assert.Null(reloadedPng.GetExifProfile());
            Assert.Null(reloadedPng.GetXmpProfile());
            Assert.Null(reloadedPng.GetIptcProfile());
            Assert.Null(reloadedPng.GetAttribute("Comment"));
            Assert.Null(reloadedPng.Comment);
        }

        // WebP with XMP + EXIF
        using var webpImg = new MagickImage(new MagickColor(250, 120, 10), 30, 30);
        webpImg.SetProfile(new XmpProfile("<x:xmpmeta>webp-leak</x:xmpmeta>"u8.ToArray()));
        webpImg.SetProfile(exif);

        using var webpMs = new MemoryStream();
        webpImg.Write(webpMs, MagickFormat.WebP);
        var webpBytes = webpMs.ToArray();

        var webpMedia = CreatePendingMedia(MediaKind.Image, "sticker.webp", "image/webp", webpBytes.Length);
        using var webpVerifier = CreateVerifier(new StubMediaStorage(webpMedia.ObjectKey, webpBytes, "\"e-webp\""));

        await using var webpRes = await webpVerifier.VerifyAndSanitizeAsync(
            webpMedia,
            new MediaObjectMetadata(webpBytes.Length, "\"e-webp\"", "image/webp"));

        Assert.Equal("image/webp", webpRes.VerifiedContentType);
        using (var reloadedWebp = new MagickImage(webpRes.ProcessedStream!))
        {
            Assert.Null(reloadedWebp.GetExifProfile());
            Assert.Null(reloadedWebp.GetXmpProfile());
        }
    }

    [Fact]
    public async Task VerifyImage_AnimatedGif_StripsCommentsAndPreservesFrames()
    {
        using var gif = new MagickImageCollection();
        var frame1 = new MagickImage(new MagickColor(255, 0, 0), 16, 16) { AnimationDelay = 10, Comment = "secret-gif-comment" };
        var frame2 = new MagickImage(new MagickColor(0, 255, 0), 16, 16) { AnimationDelay = 10 };
        var frame3 = new MagickImage(new MagickColor(0, 0, 255), 16, 16) { AnimationDelay = 10 };
        gif.Add(frame1);
        gif.Add(frame2);
        gif.Add(frame3);

        using var ms = new MemoryStream();
        gif.Write(ms, MagickFormat.Gif);
        var bytes = ms.ToArray();

        var media = CreatePendingMedia(MediaKind.Image, "anim.gif", "image/gif", bytes.Length);
        using var verifier = CreateVerifier(new StubMediaStorage(media.ObjectKey, bytes, "\"e-gif\""));

        await using var res = await verifier.VerifyAndSanitizeAsync(
            media,
            new MediaObjectMetadata(bytes.Length, "\"e-gif\"", "image/gif"));

        Assert.Equal("image/gif", res.VerifiedContentType);
        Assert.Equal(16, res.Width);
        Assert.Equal(16, res.Height);

        var outBytes = ReadAllBytes(res.ProcessedStream!);
        Assert.DoesNotContain("secret-gif-comment", Encoding.ASCII.GetString(outBytes), StringComparison.Ordinal);

        using var reloaded = new MagickImageCollection(outBytes, MagickFormat.Gif);
        Assert.Equal(3, reloaded.Count);
        Assert.All(reloaded, f => Assert.True(string.IsNullOrEmpty(f.Comment)));
    }

    [Theory]
    [InlineData("image/jpeg", "reencoded.jpg")]
    [InlineData("image/png", "reencoded.png")]
    [InlineData("image/webp", "reencoded.webp")]
    [InlineData("image/gif", "reencoded.gif")]
    public async Task VerifyImage_EnforcesMaxEncodedSizeAcrossAllFourFormats(string mime, string fileName)
    {
        // Build noisy RGBA pixel buffer so re-encoded output cannot shrink below 100 bytes
        const int size = 64;
        var pixels = new byte[size * size * 4];
        new Random(42).NextBytes(pixels);
        using var img = new MagickImage();
        img.ReadPixels(pixels, new PixelReadSettings(size, size, StorageType.Char, PixelMapping.RGBA));

        var format = mime switch
        {
            "image/jpeg" => MagickFormat.Jpeg,
            "image/png" => MagickFormat.Png,
            "image/webp" => MagickFormat.WebP,
            _ => MagickFormat.Gif
        };

        using var ms = new MemoryStream();
        img.Write(ms, format);
        var bytes = ms.ToArray();

        var media = CreatePendingMedia(MediaKind.Image, fileName, mime, bytes.Length);
        using var verifier = CreateVerifier(
            new StubMediaStorage(media.ObjectKey, bytes, "\"e-cap\""),
            maxEncodedBytesOverride: 100);

        await Assert.ThrowsAsync<DomainException>(() => verifier.VerifyAndSanitizeAsync(
            media,
            new MediaObjectMetadata(bytes.Length, "\"e-cap\"", mime)));
    }

    [Fact]
    public async Task VerifyImage_RejectsFormatMismatch_UnsupportedBmp_SvgInjection_DimensionBombs_AndReleasesSemaphoreOnErrors()
    {
        // 1. PNG bytes named .jpg -> DomainException
        using var pngImg = new MagickImage(new MagickColor(0, 0, 255), 10, 10);
        using var pngMs = new MemoryStream();
        pngImg.Write(pngMs, MagickFormat.Png);
        var pngBytes = pngMs.ToArray();

        var disguisedMedia = CreatePendingMedia(MediaKind.Image, "photo.jpg", "image/jpeg", pngBytes.Length);
        using var v1 = CreateVerifier(new StubMediaStorage(disguisedMedia.ObjectKey, pngBytes, "\"e1\""));
        await Assert.ThrowsAsync<DomainException>(() => v1.VerifyAndSanitizeAsync(
            disguisedMedia,
            new MediaObjectMetadata(pngBytes.Length, "\"e1\"", "image/jpeg")));

        // 2. BMP bytes named .png -> DomainException (rejected before Magick)
        using var bmpMs = new MemoryStream();
        pngImg.Write(bmpMs, MagickFormat.Bmp);
        var bmpBytes = bmpMs.ToArray();

        var bmpMedia = CreatePendingMedia(MediaKind.Image, "pic.png", "image/png", bmpBytes.Length);
        using var v2 = CreateVerifier(new StubMediaStorage(bmpMedia.ObjectKey, bmpBytes, "\"e2\""));
        await Assert.ThrowsAsync<DomainException>(() => v2.VerifyAndSanitizeAsync(
            bmpMedia,
            new MediaObjectMetadata(bmpBytes.Length, "\"e2\"", "image/png")));

        // 3. SVG payload disguised as .png -> DomainException (rejected before Magick)
        var svgBytes = "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray();
        var svgMedia = CreatePendingMedia(MediaKind.Image, "evil.png", "image/png", svgBytes.Length);
        using var vSvg = CreateVerifier(new StubMediaStorage(svgMedia.ObjectKey, svgBytes, "\"e-svg\""));
        await Assert.ThrowsAsync<DomainException>(() => vSvg.VerifyAndSanitizeAsync(
            svgMedia,
            new MediaObjectMetadata(svgBytes.Length, "\"e-svg\"", "image/png")));

        // 4. Minimal PNG header claiming 9000x9000 dimensions -> DomainException
        var bombPngHeader = BuildMinimalPngWithDimensions(9000, 9000);
        var bombMedia = CreatePendingMedia(MediaKind.Image, "bomb.png", "image/png", bombPngHeader.Length);
        using var v3 = CreateVerifier(new StubMediaStorage(bombMedia.ObjectKey, bombPngHeader, "\"e3\""));
        await Assert.ThrowsAsync<DomainException>(() => v3.VerifyAndSanitizeAsync(
            bombMedia,
            new MediaObjectMetadata(bombPngHeader.Length, "\"e3\"", "image/png")));

        // 5. 6000x6000 = 36 MP (> 33 MP limit) within <= 8192 width/height -> DomainException
        var mpPngHeader = BuildMinimalPngWithDimensions(6000, 6000);
        var mpMedia = CreatePendingMedia(MediaKind.Image, "36mp.png", "image/png", mpPngHeader.Length);
        using var v4 = CreateVerifier(new StubMediaStorage(mpMedia.ObjectKey, mpPngHeader, "\"e4\""));
        await Assert.ThrowsAsync<DomainException>(() => v4.VerifyAndSanitizeAsync(
            mpMedia,
            new MediaObjectMetadata(mpPngHeader.Length, "\"e4\"", "image/png")));

        // 6. Semaphore with MaxImageProcessingConcurrency = 1 must never leak across failures + concurrent valid work
        var storage = new StubMediaStorage();
        storage.Add(bmpMedia.ObjectKey, bmpBytes, "\"e-bmp\"");
        storage.AddValidPng();
        using var singleSlotVerifier = CreateVerifier(storage, new MediaOptions { MaxImageProcessingConcurrency = 1 });

        var good1 = CreatePendingMedia(MediaKind.Image, "good1.png", "image/png", storage.ValidPngBytes.Length);
        var good2 = CreatePendingMedia(MediaKind.Image, "good2.png", "image/png", storage.ValidPngBytes.Length);
        storage.Add(good1.ObjectKey, storage.ValidPngBytes, "\"g1\"");
        storage.Add(good2.ObjectKey, storage.ValidPngBytes, "\"g2\"");

        var failTask = Assert.ThrowsAsync<DomainException>(() => singleSlotVerifier.VerifyAndSanitizeAsync(
            bmpMedia, new MediaObjectMetadata(bmpBytes.Length, "\"e-bmp\"", "image/png")));
        var t1 = singleSlotVerifier.VerifyAndSanitizeAsync(good1, new MediaObjectMetadata(storage.ValidPngBytes.Length, "\"g1\"", "image/png"));
        var t2 = singleSlotVerifier.VerifyAndSanitizeAsync(good2, new MediaObjectMetadata(storage.ValidPngBytes.Length, "\"g2\"", "image/png"));

        await Task.WhenAll(failTask, t1, t2);
        await using var r1 = await t1;
        await using var r2 = await t2;
        Assert.Equal(8, r1.Width);
        Assert.Equal(8, r2.Width);

        // 7. Process-wide Magick.NET ResourceLimits sized safely + transient vs invalid classification
        Assert.True(ResourceLimits.Memory >= 512UL * 1024 * 1024);
        Assert.True(ResourceLimits.Disk >= 1024UL * 1024 * 1024);
        Assert.Equal(1UL, ResourceLimits.Thread);

        Assert.False(MediaVerifier.IsTransientResourceExhaustion(
            new MagickResourceLimitErrorException("memory allocation failed"), headersValidated: false));
        Assert.False(MediaVerifier.IsTransientResourceExhaustion(
            new MagickResourceLimitErrorException("width or height exceeds limit"), headersValidated: true));
        Assert.True(MediaVerifier.IsTransientResourceExhaustion(
            new MagickCacheErrorException("cache resources exhausted"), headersValidated: true));
        Assert.True(MediaVerifier.IsTransientResourceExhaustion(
            new MagickResourceLimitErrorException("memory allocation failed"), headersValidated: true));
    }

    // =========================================================================
    // 2. ImageFrameCounter Binary DoS & Frame Bomb Tests
    // =========================================================================

    [Fact]
    public void ImageFrameCounter_DetectsGifAndWebpFrameBombs_AndHandlesUint32MaxWithoutWraparound()
    {
        using var gif3 = BuildSyntheticGifStream(frameCount: 3);
        Assert.Equal(3, ImageFrameCounter.CountGifFrames(gif3, maxFrames: 300));

        using var gif301 = BuildSyntheticGifStream(frameCount: 301);
        Assert.Throws<DomainException>(() => ImageFrameCounter.CountGifFrames(gif301, maxFrames: 300));

        using var gifBlockFlood = new MemoryStream();
        gifBlockFlood.Write("GIF89a\x01\x00\x01\x00\x00\x00\x00"u8);
        for (var i = 0; i < 4200; i++)
        {
            gifBlockFlood.WriteByte(0x00);
        }

        Assert.Throws<DomainException>(() => ImageFrameCounter.CountGifFrames(gifBlockFlood, maxFrames: 300));

        // [B3] Valid > 1.5 MB GIF (~6,500 data sub-blocks of 255 bytes each) MUST pass
        using var largeValidGif = BuildLargeValidGifStream(subBlockCount: 6500);
        var largeScan = ImageFrameCounter.ScanGif(largeValidGif, maxFrames: 300, MediaConstraints.MaxTotalDecodedImagePixels);
        Assert.Equal(1, largeScan.FrameCount);

        // [B4] Pre-decode heterogeneous GIF bomb: 1x1 logical screen & Frame 0, then 5 frames of 4000x4000 (= 80M px > 40M budget)
        using var heterogenousGifBomb = BuildHeterogeneousGifStream(
            logicalW: 1,
            logicalH: 1,
            frameDimensions: [(1, 1), (4000, 4000), (4000, 4000), (4000, 4000), (4000, 4000), (4000, 4000)]);
        Assert.Throws<DomainException>(() =>
            ImageFrameCounter.ScanGif(heterogenousGifBomb, maxFrames: 300, MediaConstraints.MaxTotalDecodedImagePixels));

        // [B4] Pre-decode single-frame 33 MP cap on Frame 1 (1x1 Frame 0 + 6000x6000 = 36 MP Frame 1)
        using var oversizedFrame1Gif = BuildHeterogeneousGifStream(
            logicalW: 1,
            logicalH: 1,
            frameDimensions: [(1, 1), (6000, 6000)]);
        Assert.Throws<DomainException>(() =>
            ImageFrameCounter.ScanGif(oversizedFrame1Gif, maxFrames: 300, MediaConstraints.MaxTotalDecodedImagePixels));

        using var webp301 = BuildSyntheticWebpStream(anmfCount: 301);
        Assert.Throws<DomainException>(() => ImageFrameCounter.CountWebpFrames(webp301, maxFrames: 300));

        // [B4] WebP ANMF per-frame > 33 MP and > 40M cumulative pixel bomb rejected pre-decode
        using var webpFrameBomb = BuildHeterogeneousWebpStream(canvasW: 1, canvasH: 1, frameDimensions: [(1, 1), (6000, 6000)]);
        Assert.Throws<DomainException>(() =>
            ImageFrameCounter.ScanWebp(webpFrameBomb, maxFrames: 300, MediaConstraints.MaxTotalDecodedImagePixels));

        using var maliciousWebp = new MemoryStream();
        maliciousWebp.Write("RIFF"u8);
        maliciousWebp.Write(BitConverter.GetBytes(100u));
        maliciousWebp.Write("WEBP"u8);
        maliciousWebp.Write("ANMF"u8);
        maliciousWebp.Write(BitConverter.GetBytes(0xFFFF_FFFFu));
        Assert.Throws<DomainException>(() => ImageFrameCounter.CountWebpFrames(maliciousWebp, maxFrames: 300));
    }

    // =========================================================================
    // 3. Video 4 KB Magic-Byte Inspection Tests
    // =========================================================================

    [Theory]
    [InlineData("clip.mp4", "video/mp4", "isom", true)]
    [InlineData("clip.mp4", "video/mp4", "mp42", true)]
    [InlineData("audio-disguised.mp4", "video/mp4", "M4A ", false)]
    [InlineData("heic-disguised.mp4", "video/mp4", "heic", false)]
    [InlineData("mif1-disguised.mp4", "video/mp4", "mif1", false)]
    [InlineData("avif-disguised.mp4", "video/mp4", "avif", false)]
    [InlineData("mov-disguised.mp4", "video/mp4", "qt  ", false)]
    [InlineData("clip.mov", "video/quicktime", "qt  ", true)]
    [InlineData("heic.mov", "video/quicktime", "heic", false)]
    public async Task VerifyVideoHeader_EnforcesMp4AndMovFtypBrands(
        string fileName,
        string mime,
        string majorBrand,
        bool shouldPass)
    {
        var bytes = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(0, 4), 24);
        Encoding.ASCII.GetBytes("ftyp").CopyTo(bytes, 4);
        Encoding.ASCII.GetBytes(majorBrand).CopyTo(bytes, 8);

        var media = CreatePendingMedia(MediaKind.Video, fileName, mime, bytes.Length);
        using var verifier = CreateVerifier(new StubMediaStorage(media.ObjectKey, bytes, "\"vid\""));

        if (shouldPass)
        {
            await using var res = await verifier.VerifyAndSanitizeAsync(media, new MediaObjectMetadata(bytes.Length, "\"vid\"", mime));
            Assert.Equal(mime, res.VerifiedContentType);
            Assert.Null(res.ProcessedStream);
        }
        else
        {
            await Assert.ThrowsAsync<DomainException>(() =>
                verifier.VerifyAndSanitizeAsync(media, new MediaObjectMetadata(bytes.Length, "\"vid\"", mime)));
        }
    }

    [Fact]
    public async Task VerifyVideoHeader_ValidatesWebmDocTypeAndMovMoovBoxes()
    {
        var validWebm = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x89, 0x42, 0x82, 0x84, (byte)'w', (byte)'e', (byte)'b', (byte)'m', 0x00 };
        var webmMedia = CreatePendingMedia(MediaKind.Video, "ok.webm", "video/webm", validWebm.Length);
        using var vWebm = CreateVerifier(new StubMediaStorage(webmMedia.ObjectKey, validWebm, "\"w1\""));
        await using var wRes = await vWebm.VerifyAndSanitizeAsync(webmMedia, new MediaObjectMetadata(validWebm.Length, "\"w1\"", "video/webm"));
        Assert.Equal("video/webm", wRes.VerifiedContentType);

        var matroska = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x8C, 0x42, 0x82, 0x88, (byte)'m', (byte)'a', (byte)'t', (byte)'r', (byte)'o', (byte)'s', (byte)'k', (byte)'a' };
        var mkvMedia = CreatePendingMedia(MediaKind.Video, "bad.webm", "video/webm", matroska.Length);
        using var vMkv = CreateVerifier(new StubMediaStorage(mkvMedia.ObjectKey, matroska, "\"w2\""));
        await Assert.ThrowsAsync<DomainException>(() =>
            vMkv.VerifyAndSanitizeAsync(mkvMedia, new MediaObjectMetadata(matroska.Length, "\"w2\"", "video/webm")));

        var moovBytes = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(moovBytes.AsSpan(0, 4), 32);
        Encoding.ASCII.GetBytes("moov").CopyTo(moovBytes, 4);
        var movMedia = CreatePendingMedia(MediaKind.Video, "ok.mov", "video/quicktime", moovBytes.Length);
        using var vMov = CreateVerifier(new StubMediaStorage(movMedia.ObjectKey, moovBytes, "\"m1\""));
        await using var mRes = await vMov.VerifyAndSanitizeAsync(movMedia, new MediaObjectMetadata(moovBytes.Length, "\"m1\"", "video/quicktime"));
        Assert.Equal("video/quicktime", mRes.VerifiedContentType);

        // [W5] 64-bit extended-size (rawSize == 1) MP4 ftyp reads major brand from byte 16
        var ext64ValidMp4 = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(ext64ValidMp4.AsSpan(0, 4), 1);
        Encoding.ASCII.GetBytes("ftyp").CopyTo(ext64ValidMp4, 4);
        BinaryPrimitives.WriteUInt64BigEndian(ext64ValidMp4.AsSpan(8, 8), 32);
        Encoding.ASCII.GetBytes("isom").CopyTo(ext64ValidMp4, 16);
        var ext64Media = CreatePendingMedia(MediaKind.Video, "ext64.mp4", "video/mp4", ext64ValidMp4.Length);
        using var vExt64 = CreateVerifier(new StubMediaStorage(ext64Media.ObjectKey, ext64ValidMp4, "\"e64\""));
        await using var ext64Res = await vExt64.VerifyAndSanitizeAsync(ext64Media, new MediaObjectMetadata(ext64ValidMp4.Length, "\"e64\"", "video/mp4"));
        Assert.Equal("video/mp4", ext64Res.VerifiedContentType);

        var ext64BannedMp4 = (byte[])ext64ValidMp4.Clone();
        Encoding.ASCII.GetBytes("M4A ").CopyTo(ext64BannedMp4, 16);
        var ext64BannedMedia = CreatePendingMedia(MediaKind.Video, "ext64bad.mp4", "video/mp4", ext64BannedMp4.Length);
        using var vExt64Bad = CreateVerifier(new StubMediaStorage(ext64BannedMedia.ObjectKey, ext64BannedMp4, "\"e64b\""));
        await Assert.ThrowsAsync<DomainException>(() =>
            vExt64Bad.VerifyAndSanitizeAsync(ext64BannedMedia, new MediaObjectMetadata(ext64BannedMp4.Length, "\"e64b\"", "video/mp4")));

        // [W5] Loose MOV free/skip/wide boxes: rawSize == 0 rejected; wide + moov accepted; free + HTML rejected
        var bogusMov = new byte[64];
        BinaryPrimitives.WriteUInt32BigEndian(bogusMov.AsSpan(0, 4), 0);
        Encoding.ASCII.GetBytes("free").CopyTo(bogusMov, 4);
        Encoding.ASCII.GetBytes("<script>alert(1)</script>").CopyTo(bogusMov, 8);
        var bogusMovMedia = CreatePendingMedia(MediaKind.Video, "bogus.mov", "video/quicktime", bogusMov.Length);
        using var vBogusMov = CreateVerifier(new StubMediaStorage(bogusMovMedia.ObjectKey, bogusMov, "\"bm\""));
        await Assert.ThrowsAsync<DomainException>(() =>
            vBogusMov.VerifyAndSanitizeAsync(bogusMovMedia, new MediaObjectMetadata(bogusMov.Length, "\"bm\"", "video/quicktime")));

        var widePlusMoov = new byte[40];
        BinaryPrimitives.WriteUInt32BigEndian(widePlusMoov.AsSpan(0, 4), 8);
        Encoding.ASCII.GetBytes("wide").CopyTo(widePlusMoov, 4);
        BinaryPrimitives.WriteUInt32BigEndian(widePlusMoov.AsSpan(8, 4), 32);
        Encoding.ASCII.GetBytes("moov").CopyTo(widePlusMoov, 12);
        var wideMovMedia = CreatePendingMedia(MediaKind.Video, "wide.mov", "video/quicktime", widePlusMoov.Length);
        using var vWideMov = CreateVerifier(new StubMediaStorage(wideMovMedia.ObjectKey, widePlusMoov, "\"wm\""));
        await using var wMovRes = await vWideMov.VerifyAndSanitizeAsync(wideMovMedia, new MediaObjectMetadata(widePlusMoov.Length, "\"wm\"", "video/quicktime"));
        Assert.Equal("video/quicktime", wMovRes.VerifiedContentType);
    }

    // =========================================================================
    // 4. Document 4 KB Magic-Byte Inspection & UTF-8 Text Sanitization Tests
    // =========================================================================

    [Fact]
    public async Task VerifyDocumentHeader_ValidatesBinaryDocumentSignatures()
    {
        var pdfBytes = "%PDF-1.7\n..."u8.ToArray();
        var pdfMedia = CreatePendingMedia(MediaKind.Document, "doc.pdf", "application/pdf", pdfBytes.Length);
        using var vPdf = CreateVerifier(new StubMediaStorage(pdfMedia.ObjectKey, pdfBytes, "\"p\""));
        await using var rPdf = await vPdf.VerifyAndSanitizeAsync(pdfMedia, new MediaObjectMetadata(pdfBytes.Length, "\"p\"", "application/pdf"));
        Assert.Equal("application/pdf", rPdf.VerifiedContentType);

        var fakePdf = "<html><script>alert(1)</script></html>"u8.ToArray();
        var badPdf = CreatePendingMedia(MediaKind.Document, "evil.pdf", "application/pdf", fakePdf.Length);
        using var vBadPdf = CreateVerifier(new StubMediaStorage(badPdf.ObjectKey, fakePdf, "\"bp\""));
        await Assert.ThrowsAsync<DomainException>(() =>
            vBadPdf.VerifyAndSanitizeAsync(badPdf, new MediaObjectMetadata(fakePdf.Length, "\"bp\"", "application/pdf")));

        var oleBytes = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00 };
        var docMedia = CreatePendingMedia(MediaKind.Document, "legacy.doc", "application/msword", oleBytes.Length);
        using var vDoc = CreateVerifier(new StubMediaStorage(docMedia.ObjectKey, oleBytes, "\"d\""));
        await using var rDoc = await vDoc.VerifyAndSanitizeAsync(docMedia, new MediaObjectMetadata(oleBytes.Length, "\"d\"", "application/msword"));
        Assert.Equal("application/msword", rDoc.VerifiedContentType);

        var emptyZipBytes = new byte[] { 0x50, 0x4B, 0x05, 0x06, 0x00, 0x00 };
        var zipMedia = CreatePendingMedia(MediaKind.Document, "empty.zip", "application/zip", emptyZipBytes.Length);
        using var vZip = CreateVerifier(new StubMediaStorage(zipMedia.ObjectKey, emptyZipBytes, "\"z\""));
        await using var rZip = await vZip.VerifyAndSanitizeAsync(zipMedia, new MediaObjectMetadata(emptyZipBytes.Length, "\"z\"", "application/zip"));
        Assert.Equal("application/zip", rZip.VerifiedContentType);

        var fakeDocx = CreatePendingMedia(
            MediaKind.Document,
            "fake.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            emptyZipBytes.Length);
        using var vFakeDocx = CreateVerifier(new StubMediaStorage(fakeDocx.ObjectKey, emptyZipBytes, "\"fd\""));
        await Assert.ThrowsAsync<DomainException>(() => vFakeDocx.VerifyAndSanitizeAsync(
            fakeDocx,
            new MediaObjectMetadata(emptyZipBytes.Length, "\"fd\"", "application/vnd.openxmlformats-officedocument.wordprocessingml.document")));

        using var zipMs = new MemoryStream();
        using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("word/document.xml");
            await using var ew = entry.Open();
            await ew.WriteAsync("<w:document/>"u8.ToArray());
        }

        var realDocxBytes = zipMs.ToArray();
        var realDocx = CreatePendingMedia(
            MediaKind.Document,
            "real.docx",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            realDocxBytes.Length);
        using var vRealDocx = CreateVerifier(new StubMediaStorage(realDocx.ObjectKey, realDocxBytes, "\"rd\""));
        await using var rDocx = await vRealDocx.VerifyAndSanitizeAsync(
            realDocx,
            new MediaObjectMetadata(realDocxBytes.Length, "\"rd\"", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"));
        Assert.Equal("application/vnd.openxmlformats-officedocument.wordprocessingml.document", rDocx.VerifiedContentType);
    }

    [Fact]
    public async Task VerifyDocumentHeader_PlainTextHandlesUtf8BomBoundaryAndRejectsDisguisedContent()
    {
        var fullTextFile = new byte[5000];
        fullTextFile[0] = 0xEF;
        fullTextFile[1] = 0xBB;
        fullTextFile[2] = 0xBF;
        Array.Fill(fullTextFile, (byte)'a', 3, 5000 - 3);
        var emoji = Encoding.UTF8.GetBytes("😀");
        Array.Copy(emoji, 0, fullTextFile, 4094, 4);

        var splitMedia = CreatePendingMedia(MediaKind.Document, "notes.txt", "text/plain", fullTextFile.Length);
        using var vSplit = CreateVerifier(new StubMediaStorage(splitMedia.ObjectKey, fullTextFile, "\"t1\""));
        await using var rSplit = await vSplit.VerifyAndSanitizeAsync(
            splitMedia,
            new MediaObjectMetadata(fullTextFile.Length, "\"t1\"", "text/plain"));
        Assert.Equal("text/plain", rSplit.VerifiedContentType);

        var exactTruncated = fullTextFile[..4096];
        var badTruncMedia = CreatePendingMedia(MediaKind.Document, "trunc.txt", "text/plain", exactTruncated.Length);
        using var vTrunc = CreateVerifier(new StubMediaStorage(badTruncMedia.ObjectKey, exactTruncated, "\"t2\""));
        await Assert.ThrowsAsync<DomainException>(() => vTrunc.VerifyAndSanitizeAsync(
            badTruncMedia,
            new MediaObjectMetadata(exactTruncated.Length, "\"t2\"", "text/plain")));

        foreach (var badPayload in new[]
        {
            [0xEF, 0xBB, 0xBF, (byte)' ', (byte)'\n', .. "<!DOCTYPE html>"u8.ToArray()],
            [0xEF, 0xBB, 0xBF, (byte)'\t', .. "<svg onload=alert(1)>"u8.ToArray()],
            "   <script>evil()</script>"u8.ToArray(),
            "<?xml version=\"1.0\"?>"u8.ToArray(),
            "MZ\x90\x00"u8.ToArray(),
            [0x7F, (byte)'E', (byte)'L', (byte)'F', 0x02],
            [(byte)'o', (byte)'k', 0x00, (byte)'n', (byte)'u', (byte)'l'],
            [0xFF, 0xFE, 0x41]
        })
        {
            var badMedia = CreatePendingMedia(MediaKind.Document, "bad.csv", "text/csv", badPayload.Length);
            using var vBad = CreateVerifier(new StubMediaStorage(badMedia.ObjectKey, badPayload, "\"bad\""));
            await Assert.ThrowsAsync<DomainException>(() => vBad.VerifyAndSanitizeAsync(
                badMedia,
                new MediaObjectMetadata(badPayload.Length, "\"bad\"", "text/csv")));
        }
    }

    // =========================================================================
    // Test Helpers
    // =========================================================================

    private static byte[] ReadAllBytes(Stream stream)
    {
        stream.Seek(0, SeekOrigin.Begin);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] BuildMinimalPngWithDimensions(uint width, uint height)
    {
        var data = new byte[8 + 4 + 4 + 13 + 4];
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        sig.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(data, 12);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20, 4), height);
        data[24] = 8;
        data[25] = 6;
        return data;
    }

    private static MemoryStream BuildSyntheticGifStream(int frameCount) =>
        BuildHeterogeneousGifStream(1, 1, Enumerable.Repeat(((ushort)1, (ushort)1), frameCount));

    private static MemoryStream BuildHeterogeneousGifStream(
        ushort logicalW,
        ushort logicalH,
        IEnumerable<(ushort Width, ushort Height)> frameDimensions)
    {
        var ms = new MemoryStream();
        ms.Write("GIF89a"u8);
        Span<byte> lsd = stackalloc byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(lsd[..2], logicalW);
        BinaryPrimitives.WriteUInt16LittleEndian(lsd.Slice(2, 2), logicalH);
        ms.Write(lsd);

        Span<byte> desc = stackalloc byte[9];
        foreach (var (width, height) in frameDimensions)
        {
            ms.WriteByte(0x2C);
            desc.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(desc.Slice(4, 2), width);
            BinaryPrimitives.WriteUInt16LittleEndian(desc.Slice(6, 2), height);
            ms.Write(desc);
            ms.WriteByte(0x02);
            ms.WriteByte(0x01);
            ms.WriteByte(0xAA);
            ms.WriteByte(0x00);
        }

        ms.WriteByte(0x3B);
        ms.Seek(0, SeekOrigin.Begin);
        return ms;
    }

    private static MemoryStream BuildLargeValidGifStream(int subBlockCount)
    {
        var ms = new MemoryStream(subBlockCount * 256 + 64);
        ms.Write("GIF89a\x02\x00\x02\x00\x00\x00\x00"u8);
        ms.WriteByte(0x2C);
        Span<byte> desc = stackalloc byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(desc.Slice(4, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(desc.Slice(6, 2), 2);
        ms.Write(desc);
        ms.WriteByte(0x02);
        var subBlock = new byte[255];
        for (var i = 0; i < subBlockCount; i++)
        {
            ms.WriteByte(255);
            ms.Write(subBlock);
        }

        ms.WriteByte(0x00);
        ms.WriteByte(0x3B);
        ms.Seek(0, SeekOrigin.Begin);
        return ms;
    }

    private static MemoryStream BuildSyntheticWebpStream(int anmfCount) =>
        BuildHeterogeneousWebpStream(1, 1, Enumerable.Repeat((1, 1), anmfCount));

    private static MemoryStream BuildHeterogeneousWebpStream(
        int canvasW,
        int canvasH,
        IEnumerable<(int Width, int Height)> frameDimensions)
    {
        var body = new MemoryStream();
        body.Write("WEBP"u8);
        body.Write("VP8X"u8);
        body.Write(BitConverter.GetBytes(10u));
        Span<byte> vp8x = stackalloc byte[10];
        vp8x[0] = 0x02; // animation flag
        WriteUInt24LE(vp8x.Slice(4, 3), canvasW - 1);
        WriteUInt24LE(vp8x.Slice(7, 3), canvasH - 1);
        body.Write(vp8x);

        Span<byte> anmf = stackalloc byte[16];
        foreach (var (width, height) in frameDimensions)
        {
            body.Write("ANMF"u8);
            body.Write(BitConverter.GetBytes(16u));
            anmf.Clear();
            WriteUInt24LE(anmf.Slice(6, 3), width - 1);
            WriteUInt24LE(anmf.Slice(9, 3), height - 1);
            body.Write(anmf);
        }

        var full = new MemoryStream();
        full.Write("RIFF"u8);
        full.Write(BitConverter.GetBytes((uint)body.Length));
        body.Seek(0, SeekOrigin.Begin);
        body.CopyTo(full);
        full.Seek(0, SeekOrigin.Begin);
        return full;
    }

    private static void WriteUInt24LE(Span<byte> dest, int value)
    {
        dest[0] = (byte)(value & 0xFF);
        dest[1] = (byte)((value >> 8) & 0xFF);
        dest[2] = (byte)((value >> 16) & 0xFF);
    }

    private sealed class StubMediaStorage : IMediaStorage
    {
        private readonly Dictionary<string, (byte[] Bytes, string ETag)> _objects = new(StringComparer.Ordinal);
        public byte[] ValidPngBytes { get; private set; } = [];

        public StubMediaStorage()
        {
        }

        public StubMediaStorage(string key, byte[] bytes, string etag)
        {
            _objects[key] = (bytes, etag);
        }

        public void Add(string key, byte[] bytes, string etag) => _objects[key] = (bytes, etag);

        public void AddValidPng()
        {
            using var img = new MagickImage(new MagickColor(1, 2, 3), 8, 8);
            using var ms = new MemoryStream();
            img.Write(ms, MagickFormat.Png);
            ValidPngBytes = ms.ToArray();
        }

        public string GeneratePresignedPutUrl(string objectKey, string contentType, long contentLength, TimeSpan expiresIn, DateTimeOffset nowUtc) =>
            throw new NotSupportedException();

        public string GeneratePresignedGetUrl(string objectKey, string responseContentType, string responseContentDisposition, TimeSpan expiresIn, DateTimeOffset nowUtc) =>
            throw new NotSupportedException();

        public Task<MediaObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct = default)
        {
            return Task.FromResult(_objects.TryGetValue(objectKey, out var entry)
                ? new MediaObjectMetadata(entry.Bytes.Length, entry.ETag, null)
                : null);
        }

        public Task<byte[]> ReadByteRangeAsync(string objectKey, long offset, int length, string? ifMatchETag = null, CancellationToken ct = default)
        {
            if (!_objects.TryGetValue(objectKey, out var entry))
            {
                throw new NotFoundException("Media object", objectKey);
            }

            if (ifMatchETag is not null && !string.Equals(ifMatchETag, entry.ETag, StringComparison.Ordinal))
            {
                throw new ConflictException("ETag mismatch");
            }

            if (offset >= entry.Bytes.Length || length <= 0)
            {
                return Task.FromResult(Array.Empty<byte>());
            }

            var toRead = (int)Math.Min(length, entry.Bytes.Length - offset);
            var slice = new byte[toRead];
            Array.Copy(entry.Bytes, offset, slice, 0, toRead);
            return Task.FromResult(slice);
        }

        public async Task DownloadToStreamAsync(string objectKey, Stream destination, long maxBytes, string? ifMatchETag = null, CancellationToken ct = default)
        {
            if (!_objects.TryGetValue(objectKey, out var entry))
            {
                throw new NotFoundException("Media object", objectKey);
            }

            if (ifMatchETag is not null && !string.Equals(ifMatchETag, entry.ETag, StringComparison.Ordinal))
            {
                throw new ConflictException("ETag mismatch");
            }

            if (entry.Bytes.Length > maxBytes)
            {
                throw new DomainException("Exceeds maxBytes");
            }

            await destination.WriteAsync(entry.Bytes, ct);
        }

        public Task PromoteByCopyAsync(string stagingKey, string permanentKey, string verifiedContentType, string? contentDisposition, string? ifMatchETag = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task PromoteFromStreamAsync(string stagingKey, string permanentKey, Stream content, long contentLength, string verifiedContentType, string? contentDisposition, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<StoredObjectInfo> ListObjectsAsync(string prefix, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
