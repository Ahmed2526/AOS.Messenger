using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase9MediaDomainTests
{
    [Fact]
    public void MediaId_RejectsEmptyGuid_AndSupportsParseAndEquality()
    {
        Assert.Throws<DomainException>(() => new MediaId(Guid.Empty));
        Assert.Throws<DomainException>(() => MediaId.Parse("not-a-guid"));
        Assert.False(MediaId.TryParse(Guid.Empty.ToString(), out _));
        Assert.False(MediaId.TryParse(null, out _));

        var raw = Guid.NewGuid();
        var id1 = MediaId.From(raw);
        var id2 = MediaId.Parse(raw.ToString());
        Assert.Equal(id1, id2);
        Assert.True(MediaId.TryParse(raw.ToString(), out var parsed));
        Assert.Equal(id1, parsed);
    }

    [Theory]
    [InlineData("../../etc/passwd.pdf", "passwd.pdf")]
    [InlineData("C:\\Users\\Alice\\Docs\\report.docx", "report.docx")]
    [InlineData("evil\u202Egpj.exe.pdf", "evilgpj.exe.pdf")]
    [InlineData("a\u2066b\u2069c\u200Bd\uFEFF.txt", "abcd.txt")]
    [InlineData("quote\"semi;colon\r\nctrl\x07.csv", "quotesemicolonctrl.csv")]
    [InlineData("c1\u0085nel\u009Bcsi.txt", "c1nelcsi.txt")]
    [InlineData("trailing-dots.pdf...", "trailing-dots.pdf")]
    [InlineData("...pdf", "attachment.pdf")]
    [InlineData(".png", "attachment.png")]
    [InlineData("تقرير نهائي.pdf", "تقرير نهائي.pdf")]
    public void SanitizeFileName_StripsDangerousCharactersAndPreservesValidNames(string raw, string expected)
    {
        var sanitized = MediaConstraints.SanitizeFileName(raw);
        Assert.Equal(expected, sanitized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noextension")]
    [InlineData("trailingdot.")]
    [InlineData("///")]
    public void SanitizeFileName_RejectsEmptyOrExtensionlessNames(string raw)
    {
        Assert.Throws<DomainException>(() => MediaConstraints.SanitizeFileName(raw));
    }

    [Theory]
    [InlineData("blob", "image/png", "blob.png")]
    [InlineData("screenshot", "image/jpeg; charset=binary", "screenshot.jpg")]
    [InlineData("image.", "image/webp", "image.webp")]
    [InlineData("photo.png", "image/jpeg", "photo.png")]
    public void SanitizeFileName_UsesAllowlistedContentTypeFallbackForExtensionlessClipboardPastes(
        string raw,
        string? fallbackContentType,
        string expected)
    {
        var sanitized = MediaConstraints.SanitizeFileName(raw, fallbackContentType);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void SanitizeFileName_WithFallbackExtension_TruncatesLongNamesIncludingAppendedExtension()
    {
        var longExtensionless = new string('a', 180);
        var sanitized = MediaConstraints.SanitizeFileName(longExtensionless, "image/png");

        Assert.EndsWith(".png", sanitized, StringComparison.Ordinal);
        Assert.Equal(MediaConstraints.MaxSanitizedFileNameLength, sanitized.Length);
    }

    [Theory]
    [InlineData("blob", "image/svg+xml")]
    [InlineData("blob", "application/octet-stream")]
    [InlineData("blob", "audio/ogg")]
    [InlineData("blob", null)]
    [InlineData("blob", "")]
    public void SanitizeFileName_RejectsExtensionlessNamesWhenFallbackContentTypeIsDisallowedOrMissing(
        string raw,
        string? fallbackContentType)
    {
        Assert.Throws<DomainException>(() => MediaConstraints.SanitizeFileName(raw, fallbackContentType));
    }

    [Fact]
    public void SanitizeFileName_TruncatesLongNamesWithoutSplittingSurrogatePairs()
    {
        // Build a stem of 90 emoji surrogate pairs (180 UTF-16 chars) + ".png" (4 chars)
        var longEmojiStem = string.Concat(Enumerable.Repeat("😀", 90));
        var sanitized = MediaConstraints.SanitizeFileName(longEmojiStem + ".png");

        Assert.EndsWith(".png", sanitized, StringComparison.Ordinal);
        Assert.True(sanitized.Length <= MediaConstraints.MaxSanitizedFileNameLength);
        // Must remain valid UTF-16 with no lone surrogates
        for (var i = 0; i < sanitized.Length; i++)
        {
            if (char.IsHighSurrogate(sanitized[i]))
            {
                Assert.True(i + 1 < sanitized.Length && char.IsLowSurrogate(sanitized[i + 1]));
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(sanitized[i]));
            }
        }
    }

    [Fact]
    public void BuildContentDisposition_EmitsAsciiFallbackAndRfc5987Utf8Filename()
    {
        var unicodeHeader = MediaConstraints.BuildContentDisposition("تقرير 2026.pdf", isAttachment: true);
        Assert.StartsWith("attachment; filename=\"", unicodeHeader, StringComparison.Ordinal);
        Assert.Contains("filename*=UTF-8''", unicodeHeader, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString("تقرير 2026.pdf"), unicodeHeader, StringComparison.Ordinal);

        var inlineHeader = MediaConstraints.BuildContentDisposition("photo.jpg", isAttachment: false);
        Assert.StartsWith("inline; filename=\"photo.jpg\"; filename*=UTF-8''photo.jpg", inlineHeader, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("photo.jpg", "image/jpeg", MediaKind.Image, "image/jpeg")]
    [InlineData("photo.jpeg", "image/jpg", MediaKind.Image, "image/jpeg")]
    [InlineData("pic.png", null, MediaKind.Image, "image/png")]
    [InlineData("anim.webp", "application/octet-stream", MediaKind.Image, "image/webp")]
    [InlineData("anim.gif", "image/gif", MediaKind.Image, "image/gif")]
    [InlineData("clip.mp4", "video/mp4", MediaKind.Video, "video/mp4")]
    [InlineData("clip.webm", "video/webm", MediaKind.Video, "video/webm")]
    [InlineData("clip.mov", "", MediaKind.Video, "video/quicktime")]
    [InlineData("doc.pdf", "application/pdf", MediaKind.Document, "application/pdf")]
    [InlineData("doc.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", MediaKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]
    [InlineData("sheet.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", MediaKind.Document, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    [InlineData("slides.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation", MediaKind.Document, "application/vnd.openxmlformats-officedocument.presentationml.presentation")]
    [InlineData("notes.txt", "text/plain; charset=utf-8", MediaKind.Document, "text/plain")]
    [InlineData("data.csv", "application/vnd.ms-excel", MediaKind.Document, "text/csv")]
    [InlineData("archive.zip", "application/x-zip-compressed", MediaKind.Document, "application/zip")]
    public void ResolveAllowedMedia_AcceptsAllowedTypesAndMimeFallbacks(
        string fileName,
        string? declaredMime,
        MediaKind expectedKind,
        string expectedCanonicalMime)
    {
        var result = MediaConstraints.ResolveAllowedMedia(fileName, declaredMime);
        Assert.Equal(expectedKind, result.Kind);
        Assert.Equal(expectedCanonicalMime, result.CanonicalContentType);
    }

    [Theory]
    [InlineData("vector.svg", "image/svg+xml")]
    [InlineData("page.html", "text/html")]
    [InlineData("script.js", "application/javascript")]
    [InlineData("feed.xml", "application/xml")]
    [InlineData("app.exe", "application/octet-stream")]
    [InlineData("voice.ogg", "audio/ogg")]
    [InlineData("fake.jpg", "application/pdf")]
    [InlineData("fake.pdf", "text/html")]
    public void ResolveAllowedMedia_RejectsActiveContentAudioAndMismatchedMimes(string fileName, string declaredMime)
    {
        Assert.Throws<DomainException>(() => MediaConstraints.ResolveAllowedMedia(fileName, declaredMime));
    }

    [Fact]
    public void MediaEntity_LifecycleTransitions_EnforceInvariants()
    {
        var uploaderId = UserId.New();
        var convId = ConversationId.New();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<DomainException>(() => Media.CreatePending(
            uploaderId, convId, MediaKind.Image, "photo.jpg", "image/jpeg", 0, now));
        Assert.Throws<DomainException>(() => Media.CreatePending(
            uploaderId, convId, MediaKind.Image, "photo.jpg", "image/jpeg", MediaConstraints.MaxImageBytes + 1, now));

        var media = Media.CreatePending(
            uploaderId, convId, MediaKind.Image, "photo.jpg", "image/jpeg", 1024, now);

        Assert.Equal(MediaState.Pending, media.State);
        Assert.StartsWith("pending/", media.ObjectKey, StringComparison.Ordinal);
        Assert.True(MediaConstraints.IsValidStorageObjectId(media.StorageObjectId));
        Assert.Null(media.MessageId);

        // Cannot attach before Uploaded
        Assert.Throws<DomainException>(() => media.MarkAttached(MessageId.From(1), 0, now));

        // Wrong promoted key prefix or invalid identifier rejected
        Assert.Throws<DomainException>(() => media.MarkUploaded(
            $"pending/{media.StorageObjectId}", "image/jpeg", 900, 100, 200, null, now));
        Assert.Throws<DomainException>(() => media.MarkUploaded(
            "media/not-valid-hex", "image/jpeg", 900, 100, 200, null, now));
        Assert.Throws<DomainException>(() => media.MarkUploaded(
            $"media/{media.StorageObjectId.ToUpperInvariant()}", "image/jpeg", 900, 100, 200, null, now));
        Assert.Throws<DomainException>(() => media.MarkUploaded(
            "media/../etc/passwd", "image/jpeg", 900, 100, 200, null, now));

        var promotedKey = $"media/{MediaConstraints.GenerateStorageObjectId()}";
        media.MarkUploaded(promotedKey, "image/jpeg", 900, 100, 200, null, now.AddSeconds(5));
        Assert.Equal(MediaState.Uploaded, media.State);
        Assert.Equal(promotedKey, media.ObjectKey);
        Assert.Equal(100, media.Width);
        Assert.Equal(200, media.Height);

        // Invalid DisplayOrder rejected
        Assert.Throws<DomainException>(() => media.MarkAttached(MessageId.From(1), MediaConstraints.MaxAttachmentsPerMessage, now));

        media.MarkAttached(MessageId.From(42), 1, now.AddSeconds(10));
        Assert.Equal(MediaState.Attached, media.State);
        Assert.Equal(MessageId.From(42), media.MessageId);
        Assert.Equal(1, media.DisplayOrder);

        // Cannot mark failed after Uploaded/Attached
        Assert.Throws<DomainException>(() => media.MarkFailed("bad", now));

        // MarkDeleted + MarkPurged
        media.MarkDeleted(now.AddMinutes(1));
        Assert.Equal(MediaState.Deleted, media.State);
        media.MarkDeleted(now.AddMinutes(2)); // idempotent
        media.MarkPurged(now.AddMinutes(3));
        Assert.Equal(now.AddMinutes(3), media.PurgedAtUtc);
    }

    [Fact]
    public void Message_WithAttachments_SupportsOptionalInvisibleNormalizedCaptionsAndEditClearing()
    {
        var convId = ConversationId.New();
        var senderId = UserId.New();
        var now = DateTimeOffset.UtcNow;

        // Text-only still rejects null/invisible
        Assert.Throws<DomainException>(() => Message.Create(convId, senderId, "k1", null, now));
        Assert.Throws<DomainException>(() => Message.Create(convId, senderId, "k2", "\u200B", now));

        // Attachment message allows null and normalizes invisible-only to null
        var msg = Message.Create(convId, senderId, "k3", "\u200B  ", now, hasAttachments: true);
        Assert.True(msg.HasAttachments);
        Assert.Null(msg.Content);

        // Can add caption via Edit
        msg.Edit("Check this photo", now.AddMinutes(1));
        Assert.Equal("Check this photo", msg.Content);
        Assert.Equal(now.AddMinutes(1), msg.EditedAtUtc);

        // Can clear caption back to null on attachment message
        msg.Edit("   ", now.AddMinutes(2));
        Assert.Null(msg.Content);
        Assert.Equal(now.AddMinutes(2), msg.EditedAtUtc);

        // Text-only message cannot be edited to empty
        var textOnly = Message.Create(convId, senderId, "k4", "Hello", now);
        Assert.Throws<DomainException>(() => textOnly.Edit("   ", now.AddMinutes(1)));

        // DeleteForEveryone prevents subsequent Edit
        msg.DeleteForEveryone(senderId, now.AddMinutes(3));
        Assert.True(msg.IsDeletedForEveryone);
        Assert.Null(msg.Content);
        Assert.Throws<DomainException>(() => msg.Edit("After delete", now.AddMinutes(4)));
    }

    [Fact]
    public void SanitizeFileName_And_ResolveAllowedMedia_RejectOversizedInputsBeforeAllocation()
    {
        var oversizedFileName = new string('a', MediaConstraints.MaxRawFileNameLength + 1) + ".png";
        var oversizedMime = "image/" + new string('x', MediaConstraints.MaxDeclaredContentTypeLength);

        Assert.Throws<DomainException>(() => MediaConstraints.SanitizeFileName(oversizedFileName));
        Assert.Throws<DomainException>(() => MediaConstraints.SanitizeFileName("blob", oversizedMime));
        Assert.Throws<DomainException>(() => MediaConstraints.ResolveAllowedMedia(oversizedFileName, "image/png"));
        Assert.Throws<DomainException>(() => MediaConstraints.ResolveAllowedMedia("photo.png", oversizedMime));
    }

    [Fact]
    public void Media_MarkUploaded_RejectsOver33Megapixels()
    {
        var media = Media.CreatePending(
            UserId.New(),
            ConversationId.New(),
            MediaKind.Image,
            "huge.png",
            "image/png",
            2048,
            DateTimeOffset.UtcNow);

        var objId = MediaConstraints.GenerateStorageObjectId();
        Assert.Throws<DomainException>(() =>
            media.MarkUploaded(
                $"media/{objId}",
                "image/png",
                1024,
                7000,
                5000,
                null,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Sticker_MarkReadyAndMarkPurged_EnforceKeyDimensionsOutputBudgetAndStateInvariants()
    {
        var packId = StickerPackId.New();
        var uploaderId = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Assert.Throws<DomainException>(() =>
            Sticker.CreatePendingCustom(packId, uploaderId, new string('x', 129), 1024, "😀", 1, now));
        Assert.Throws<DomainException>(() =>
            Sticker.CreatePendingCustom(packId, uploaderId, "image/png", 1024, "😀", -1, now));

        var pending = Sticker.CreatePendingCustom(packId, uploaderId, "image/png", 1024, "😀", 1, now);
        var permId = MediaConstraints.GenerateStorageObjectId();
        var otherPermId = MediaConstraints.GenerateStorageObjectId();

        // Key mismatch must fail
        Assert.Throws<DomainException>(() =>
            pending.MarkReady(permId, $"{MediaConstraints.StickerKeyPrefix}{otherPermId}", 100, 512, 512, false, now));

        // Dimension bounds
        Assert.Throws<DomainException>(() =>
            pending.MarkReady(permId, $"{MediaConstraints.StickerKeyPrefix}{permId}", 100, 0, 512, false, now));
        Assert.Throws<DomainException>(() =>
            pending.MarkReady(permId, $"{MediaConstraints.StickerKeyPrefix}{permId}", 100, 512, StickerConstraints.CanonicalStickerSize + 1, false, now));

        // Output byte ceiling for static & animated WebP
        Assert.Throws<DomainException>(() =>
            pending.MarkReady(permId, $"{MediaConstraints.StickerKeyPrefix}{permId}", StickerConstraints.MaxStaticWebpOutputBytes + 1, 512, 512, false, now));
        Assert.Throws<DomainException>(() =>
            pending.MarkReady(permId, $"{MediaConstraints.StickerKeyPrefix}{permId}", StickerConstraints.MaxAnimatedWebpOutputBytes + 1, 512, 512, true, now));

        // MarkPurged disallowed from Pending or Ready
        Assert.Throws<DomainException>(() => pending.MarkPurged(now));

        pending.MarkReady(permId, $"{MediaConstraints.StickerKeyPrefix}{permId}", 2048, 512, 512, false, now);
        Assert.Throws<DomainException>(() => pending.MarkPurged(now));

        pending.MarkArchived(now.AddMinutes(1));
        pending.MarkPurged(now.AddMinutes(2));
        Assert.NotNull(pending.PurgedAtUtc);
    }

    [Fact]
    public void AvatarAndStickerConstraints_NormalizeMimeParametersAndOctetStream_WithoutEchoingRawInput()
    {
        Assert.Equal("image/png", AvatarConstraints.ResolveAllowedAvatarUpload("avatar.png", "image/png; charset=binary"));
        Assert.Equal("image/webp", AvatarConstraints.ResolveAllowedAvatarUpload("avatar.webp", "application/octet-stream"));

        Assert.Equal("image/png", StickerConstraints.ResolveAllowedStickerUpload("sticker.png", "image/png; name=\"s.png\""));
        Assert.Equal("image/gif", StickerConstraints.ResolveAllowedStickerUpload("sticker.gif", "application/octet-stream"));

        const string sentinel = "evil-echo-marker/custom";
        var avatarEx = Assert.Throws<DomainException>(() =>
            AvatarConstraints.ResolveAllowedAvatarUpload("avatar.png", sentinel));
        Assert.DoesNotContain(sentinel, avatarEx.Message, StringComparison.OrdinalIgnoreCase);

        var stickerEx = Assert.Throws<DomainException>(() =>
            StickerConstraints.ResolveAllowedStickerUpload("sticker.png", sentinel));
        Assert.DoesNotContain(sentinel, stickerEx.Message, StringComparison.OrdinalIgnoreCase);
    }
}
