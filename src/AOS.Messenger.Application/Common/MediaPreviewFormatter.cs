using System.Globalization;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;

namespace AOS.Messenger.Application.Common;

/// <summary>
/// Builds human-friendly, surrogate-safe preview text for text, sticker, media, and captioned media messages
/// across conversation lists, reply banners, live edits, and delete fallbacks.
/// </summary>
public static class MediaPreviewFormatter
{
    public static string FormatStickerPreview() => StickerConstraints.StickerPreviewText;
    public static string FormatPreview(
        string? content,
        IReadOnlyList<MediaAttachmentDto>? attachments,
        int maxLength = 100)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        var normalizedCaption = Message.HasVisibleContent(content) ? content!.Trim() : null;
        if (attachments is null || attachments.Count == 0)
        {
            return normalizedCaption is null
                ? string.Empty
                : TruncateSafe(normalizedCaption, maxLength);
        }

        var kinds = new MediaKind[attachments.Count];
        for (var i = 0; i < attachments.Count; i++)
        {
            kinds[i] = ParseKind(attachments[i].Kind);
        }

        return FormatWithKinds(normalizedCaption, kinds, attachments[0].FileName, maxLength);
    }

    public static string FormatPreview(
        string? content,
        IReadOnlyList<Media>? attachments,
        int maxLength = 100)
    {
        if (maxLength <= 0)
        {
            return string.Empty;
        }

        var normalizedCaption = Message.HasVisibleContent(content) ? content!.Trim() : null;
        if (attachments is null || attachments.Count == 0)
        {
            return normalizedCaption is null
                ? string.Empty
                : TruncateSafe(normalizedCaption, maxLength);
        }

        var kinds = new MediaKind[attachments.Count];
        for (var i = 0; i < attachments.Count; i++)
        {
            kinds[i] = attachments[i].Kind;
        }

        return FormatWithKinds(normalizedCaption, kinds, attachments[0].SanitizedFileName, maxLength);
    }

    private static string FormatWithKinds(
        string? normalizedCaption,
        MediaKind[] kinds,
        string? firstFileName,
        int maxLength)
    {
        var count = kinds.Length;
        var allSame = true;
        var firstKind = kinds[0];
        for (var i = 1; i < count; i++)
        {
            if (kinds[i] != firstKind)
            {
                allSame = false;
                break;
            }
        }

        var icon = allSame ? GetIcon(firstKind) : "📎";

        if (normalizedCaption is not null)
        {
            return TruncateSafe($"{icon} {normalizedCaption}", maxLength);
        }

        if (count == 1)
        {
            var singleText = firstKind switch
            {
                MediaKind.Image => "📷 Photo",
                MediaKind.Video => "🎬 Video",
                MediaKind.Voice => "🎤 Voice message",
                _ => $"📎 {(string.IsNullOrWhiteSpace(firstFileName) ? "Attachment" : firstFileName.Trim())}"
            };

            return TruncateSafe(singleText, maxLength);
        }

        if (allSame)
        {
            var batchText = firstKind switch
            {
                MediaKind.Image => $"📷 {count} photos",
                MediaKind.Video => $"🎬 {count} videos",
                MediaKind.Voice => $"🎤 {count} voice messages",
                _ => $"📎 {count} files"
            };

            return TruncateSafe(batchText, maxLength);
        }

        return TruncateSafe($"📎 {count} attachments", maxLength);
    }

    private static MediaKind ParseKind(string? kind)
    {
        if (string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Image;
        }

        if (string.Equals(kind, "video", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Video;
        }

        if (string.Equals(kind, "voice", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Voice;
        }

        return MediaKind.Document;
    }

    private static string GetIcon(MediaKind kind) => kind switch
    {
        MediaKind.Image => "📷",
        MediaKind.Video => "🎬",
        MediaKind.Voice => "🎤",
        _ => "📎"
    };

    private static string TruncateSafe(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        const string ellipsis = "...";
        var isTinyBudget = maxLength <= ellipsis.Length;
        var budget = isTinyBudget ? maxLength : maxLength - ellipsis.Length;
        var endIndex = 0;

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            var element = (string)enumerator.Current;
            var candidateEnd = enumerator.ElementIndex + element.Length;
            if (candidateEnd > budget)
            {
                break;
            }

            endIndex = candidateEnd;
        }

        if (isTinyBudget)
        {
            return endIndex > 0 ? value[..endIndex] : string.Empty;
        }

        if (endIndex <= 0)
        {
            return ellipsis;
        }

        return string.Concat(value.AsSpan(0, endIndex), ellipsis);
    }
}
