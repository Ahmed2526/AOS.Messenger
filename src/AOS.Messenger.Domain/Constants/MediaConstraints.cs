using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Domain.Constants;

/// <summary>
/// Authoritative domain constraints, format allowlists, filename sanitization, and disposition helpers for media.
/// </summary>
public static class MediaConstraints
{
    public const int MaxAttachmentsPerMessage = 5;

    public const long MaxImageBytes = 15L * 1024 * 1024;
    public const long MaxVideoBytes = 70L * 1024 * 1024;
    public const long MaxDocumentBytes = 50L * 1024 * 1024;
    public const long MaxVoiceBytes = 25L * 1024 * 1024;

    public const int MinVoiceDurationMs = 500;
    public const int MaxVoiceDurationMs = 20 * 60 * 1000;
    public const int MinVoiceWaveformPeaks = 1;
    public const int MaxVoiceWaveformPeaks = 64;
    public const int MaxWaveformPeakValue = 100;
    public const int MaxWaveformPeaksSerializedLength = 256;

    public const int MaxImageWidth = 8192;
    public const int MaxImageHeight = 8192;
    public const long MaxImagePixels = 33_000_000L;
    public const int MaxAnimationFrames = 300;
    public const long MaxTotalDecodedImagePixels = 40_000_000L;
    public const int MaxChatImageThumbnailEdge = 480;
    public const long MaxChatImageThumbnailBytes = 150L * 1024;
    public const long ChatImageThumbnailThresholdBytes = 100L * 1024;

    public const int MaxSanitizedFileNameLength = 150;
    public const int MaxRawFileNameLength = 1024;
    public const int MaxDeclaredContentTypeLength = 128;
    public const int MaxFailureReasonLength = 256;

    public const string PendingKeyPrefix = "pending/";
    public const string MediaKeyPrefix = "media/";
    public const string ThumbnailKeyPrefix = "thumbs/";
    public const string AvatarKeyPrefix = "avatars/";
    public const string StickerKeyPrefix = "stickers/";

    private sealed record AllowedFormatSpec(
        MediaKind Kind,
        string CanonicalContentType,
        IReadOnlySet<string> AllowedDeclaredMimes);

    private static readonly IReadOnlyDictionary<string, AllowedFormatSpec> Phase9Allowlist =
        new Dictionary<string, AllowedFormatSpec>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = new(MediaKind.Image, "image/jpeg", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg" }),
            [".jpeg"] = new(MediaKind.Image, "image/jpeg", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg" }),
            [".png"] = new(MediaKind.Image, "image/png", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png" }),
            [".webp"] = new(MediaKind.Image, "image/webp", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/webp" }),
            [".gif"] = new(MediaKind.Image, "image/gif", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/gif" }),

            [".mp4"] = new(MediaKind.Video, "video/mp4", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/mp4" }),
            [".webm"] = new(MediaKind.Video, "video/webm", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/webm" }),
            [".mov"] = new(MediaKind.Video, "video/quicktime", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "video/quicktime" }),

            [".pdf"] = new(MediaKind.Document, "application/pdf", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/pdf" }),
            [".doc"] = new(MediaKind.Document, "application/msword", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/msword" }),
            [".docx"] = new(MediaKind.Document, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/vnd.openxmlformats-officedocument.wordprocessingml.document" }),
            [".xls"] = new(MediaKind.Document, "application/vnd.ms-excel", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/vnd.ms-excel" }),
            [".xlsx"] = new(MediaKind.Document, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" }),
            [".ppt"] = new(MediaKind.Document, "application/vnd.ms-powerpoint", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/vnd.ms-powerpoint" }),
            [".pptx"] = new(MediaKind.Document, "application/vnd.openxmlformats-officedocument.presentationml.presentation", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/vnd.openxmlformats-officedocument.presentationml.presentation" }),
            [".txt"] = new(MediaKind.Document, "text/plain", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text/plain" }),
            [".csv"] = new(MediaKind.Document, "text/csv", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "text/csv", "application/vnd.ms-excel", "text/x-csv", "application/csv" }),
            [".zip"] = new(MediaKind.Document, "application/zip", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "application/zip", "application/x-zip-compressed", "multipart/x-zip" })
        };

    private static readonly IReadOnlyDictionary<string, string> FallbackExtensionByMime =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = ".jpg",
            ["image/jpg"] = ".jpg",
            ["image/png"] = ".png",
            ["image/webp"] = ".webp",
            ["image/gif"] = ".gif",

            ["video/mp4"] = ".mp4",
            ["video/webm"] = ".webm",
            ["video/quicktime"] = ".mov",

            ["application/pdf"] = ".pdf",
            ["application/msword"] = ".doc",
            ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
            ["application/vnd.ms-excel"] = ".xls",
            ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
            ["application/vnd.ms-powerpoint"] = ".ppt",
            ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",
            ["text/plain"] = ".txt",
            ["text/csv"] = ".csv",
            ["application/zip"] = ".zip"
        };

    // Kept strictly separate from Phase9Allowlist and FallbackExtensionByMime so
    // audio containers (.webm/.mp4/.ogg) can only enter through explicit voice initiation.
    private static readonly IReadOnlyDictionary<string, AllowedFormatSpec> VoiceAllowlist =
        new Dictionary<string, AllowedFormatSpec>(StringComparer.OrdinalIgnoreCase)
        {
            [".m4a"] = new(MediaKind.Voice, "audio/mp4", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "audio/mp4", "audio/x-m4a", "audio/aac" }),
            [".webm"] = new(MediaKind.Voice, "audio/webm", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "audio/webm" }),
            [".weba"] = new(MediaKind.Voice, "audio/webm", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "audio/webm" }),
            [".ogg"] = new(MediaKind.Voice, "audio/ogg", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "audio/ogg" }),
            [".oga"] = new(MediaKind.Voice, "audio/ogg", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "audio/ogg" })
        };

    private static readonly IReadOnlyDictionary<string, string> VoiceFallbackExtensionByMime =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["audio/mp4"] = ".m4a",
            ["audio/x-m4a"] = ".m4a",
            ["audio/aac"] = ".m4a",
            ["audio/webm"] = ".webm",
            ["audio/ogg"] = ".ogg"
        };

    public static long GetMaxSizeBytes(MediaKind kind) => kind switch
    {
        MediaKind.Image => MaxImageBytes,
        MediaKind.Video => MaxVideoBytes,
        MediaKind.Document => MaxDocumentBytes,
        MediaKind.Voice => MaxVoiceBytes,
        _ => throw new DomainException($"Unsupported media kind '{kind}'.")
    };

    public static string GenerateStorageObjectId() =>
        RandomNumberGenerator.GetHexString(32, lowercase: true);

    public static bool IsValidStorageObjectId(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 32)
        {
            return false;
        }

        foreach (var ch in value)
        {
            if (ch is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    public static string SanitizeFileName(string? rawFileName, string? fallbackContentType = null) =>
        SanitizeFileNameCore(rawFileName, fallbackContentType, "attachment", FallbackExtensionByMime);

    public static string SanitizeVoiceFileName(string? rawFileName, string? fallbackContentType = null) =>
        SanitizeFileNameCore(rawFileName, fallbackContentType, "voice-note", VoiceFallbackExtensionByMime);

    private static string SanitizeFileNameCore(
        string? rawFileName,
        string? fallbackContentType,
        string defaultStem,
        IReadOnlyDictionary<string, string> fallbackExtensions)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            throw new DomainException("File name cannot be empty.");
        }

        if (rawFileName.Length > MaxRawFileNameLength)
        {
            throw new DomainException($"File name cannot exceed {MaxRawFileNameLength} characters.");
        }

        if (fallbackContentType is not null && fallbackContentType.Length > MaxDeclaredContentTypeLength)
        {
            throw new DomainException($"Content type cannot exceed {MaxDeclaredContentTypeLength} characters.");
        }

        var lastSep = rawFileName.LastIndexOfAny(['/', '\\']);
        var segment = lastSep >= 0 ? rawFileName[(lastSep + 1)..] : rawFileName;

        var sb = new StringBuilder(segment.Length);
        foreach (var rune in segment.EnumerateRunes())
        {
            if (IsForbiddenFileNameCodePoint(rune.Value))
            {
                continue;
            }

            sb.Append(rune.ToString());
        }

        var cleaned = sb.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            throw new DomainException("File name cannot be empty.");
        }

        var ext = Path.GetExtension(cleaned).ToLowerInvariant();
        string rawStem;
        if (string.IsNullOrEmpty(ext) || ext == ".")
        {
            if (!TryGetFallbackExtension(fallbackContentType, fallbackExtensions, out var fallbackExt))
            {
                throw new DomainException("File must have a valid extension.");
            }

            ext = fallbackExt;
            rawStem = cleaned;
        }
        else
        {
            rawStem = cleaned[..^ext.Length];
        }

        var stem = rawStem.Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = defaultStem;
        }

        var maxStemLen = MaxSanitizedFileNameLength - ext.Length;
        if (maxStemLen <= 0)
        {
            throw new DomainException("File extension exceeds maximum allowed filename length.");
        }

        if (stem.Length > maxStemLen)
        {
            var cutLen = maxStemLen;
            while (cutLen > 0 && char.IsHighSurrogate(stem[cutLen - 1]))
            {
                cutLen--;
            }

            stem = cutLen > 0 ? stem[..cutLen].TrimEnd() : "a";
            if (string.IsNullOrWhiteSpace(stem))
            {
                stem = "a";
            }
        }

        return stem + ext;
    }

    public static (MediaKind Kind, string CanonicalContentType, long MaxSizeBytes) ResolveAllowedMedia(
        string sanitizedFileName,
        string? declaredContentType) =>
        ResolveAllowedMediaCore(sanitizedFileName, declaredContentType, Phase9Allowlist);

    public static (MediaKind Kind, string CanonicalContentType, long MaxSizeBytes) ResolveAllowedVoiceMedia(
        string sanitizedFileName,
        string? declaredContentType) =>
        ResolveAllowedMediaCore(sanitizedFileName, declaredContentType, VoiceAllowlist);

    public static string ValidateAndSerializeWaveformPeaks(IReadOnlyList<int>? peaks)
    {
        if (peaks is null || peaks.Count < MinVoiceWaveformPeaks || peaks.Count > MaxVoiceWaveformPeaks)
        {
            throw new DomainException(
                $"Voice waveform must contain between {MinVoiceWaveformPeaks} and {MaxVoiceWaveformPeaks} peaks.");
        }

        var sb = new StringBuilder(peaks.Count * 4);
        for (var i = 0; i < peaks.Count; i++)
        {
            var peak = peaks[i];
            if (peak < 0 || peak > MaxWaveformPeakValue)
            {
                throw new DomainException($"Voice waveform peaks must be between 0 and {MaxWaveformPeakValue}.");
            }

            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append(peak.ToString(CultureInfo.InvariantCulture));
        }

        var serialized = sb.ToString();
        if (serialized.Length > MaxWaveformPeaksSerializedLength)
        {
            throw new DomainException($"Voice waveform exceeds {MaxWaveformPeaksSerializedLength} characters.");
        }

        return serialized;
    }

    public static string ValidateSerializedWaveformPeaks(string? serialized)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            throw new DomainException("Voice waveform peaks are required.");
        }

        if (serialized.Length > MaxWaveformPeaksSerializedLength)
        {
            throw new DomainException($"Voice waveform exceeds {MaxWaveformPeaksSerializedLength} characters.");
        }

        var parsed = ParseWaveformPeaksCore(serialized, throwOnInvalid: true)!;
        return ValidateAndSerializeWaveformPeaks(parsed);
    }

    public static IReadOnlyList<int>? ParseWaveformPeaks(string? serialized) =>
        ParseWaveformPeaksCore(serialized, throwOnInvalid: false);

    private static IReadOnlyList<int>? ParseWaveformPeaksCore(string? serialized, bool throwOnInvalid)
    {
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return null;
        }

        if (serialized.Length > MaxWaveformPeaksSerializedLength)
        {
            if (throwOnInvalid)
            {
                throw new DomainException($"Voice waveform exceeds {MaxWaveformPeaksSerializedLength} characters.");
            }

            return null;
        }

        var parts = serialized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < MinVoiceWaveformPeaks || parts.Length > MaxVoiceWaveformPeaks)
        {
            if (throwOnInvalid)
            {
                throw new DomainException(
                    $"Voice waveform must contain between {MinVoiceWaveformPeaks} and {MaxVoiceWaveformPeaks} peaks.");
            }

            return null;
        }

        var result = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var peak)
                || peak < 0
                || peak > MaxWaveformPeakValue)
            {
                if (throwOnInvalid)
                {
                    throw new DomainException($"Voice waveform peaks must be between 0 and {MaxWaveformPeakValue}.");
                }

                return null;
            }

            result[i] = peak;
        }

        return result;
    }

    private static (MediaKind Kind, string CanonicalContentType, long MaxSizeBytes) ResolveAllowedMediaCore(
        string sanitizedFileName,
        string? declaredContentType,
        IReadOnlyDictionary<string, AllowedFormatSpec> allowlist)
    {
        if (string.IsNullOrWhiteSpace(sanitizedFileName))
        {
            throw new DomainException("File name cannot be empty.");
        }

        if (sanitizedFileName.Length > MaxRawFileNameLength)
        {
            throw new DomainException($"File name cannot exceed {MaxRawFileNameLength} characters.");
        }

        if (declaredContentType is not null && declaredContentType.Length > MaxDeclaredContentTypeLength)
        {
            throw new DomainException($"Content type cannot exceed {MaxDeclaredContentTypeLength} characters.");
        }

        var ext = Path.GetExtension(sanitizedFileName);
        if (string.IsNullOrEmpty(ext) || !allowlist.TryGetValue(ext, out var spec))
        {
            throw new DomainException($"Unsupported file type '{ext}'.");
        }

        if (!string.IsNullOrWhiteSpace(declaredContentType))
        {
            var semiIdx = declaredContentType.IndexOf(';');
            var normalizedMime = (semiIdx >= 0 ? declaredContentType[..semiIdx] : declaredContentType)
                .Trim()
                .ToLowerInvariant();

            if (!string.IsNullOrEmpty(normalizedMime) &&
                !string.Equals(normalizedMime, "application/octet-stream", StringComparison.Ordinal))
            {
                if (!spec.AllowedDeclaredMimes.Contains(normalizedMime))
                {
                    throw new DomainException(
                        $"Declared content type '{normalizedMime}' does not match file extension '{ext.ToLowerInvariant()}'.");
                }
            }
        }

        return (spec.Kind, spec.CanonicalContentType, GetMaxSizeBytes(spec.Kind));
    }

    public static string BuildContentDisposition(string sanitizedFileName, bool isAttachment)
    {
        var disposition = isAttachment ? "attachment" : "inline";
        var ext = Path.GetExtension(sanitizedFileName);
        var stem = ext.Length > 0 && sanitizedFileName.Length >= ext.Length
            ? sanitizedFileName[..^ext.Length]
            : sanitizedFileName;

        var asciiStemBuilder = new StringBuilder(stem.Length);
        foreach (var ch in stem)
        {
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-' or ' ')
            {
                asciiStemBuilder.Append(ch);
            }
        }

        var asciiStem = asciiStemBuilder.ToString().Trim().Trim('.');
        if (string.IsNullOrWhiteSpace(asciiStem))
        {
            asciiStem = "attachment";
        }

        var asciiExtBuilder = new StringBuilder(ext.Length);
        foreach (var ch in ext)
        {
            if (ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.')
            {
                asciiExtBuilder.Append(ch);
            }
        }

        var asciiFallback = asciiStem + asciiExtBuilder.ToString();
        var encoded = Uri.EscapeDataString(sanitizedFileName);
        return $"{disposition}; filename=\"{asciiFallback}\"; filename*=UTF-8''{encoded}";
    }

    private static bool TryGetFallbackExtension(
        string? contentType,
        IReadOnlyDictionary<string, string> fallbackMap,
        out string extension)
    {
        extension = string.Empty;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var semiIdx = contentType.IndexOf(';');
        var normalizedMime = (semiIdx >= 0 ? contentType[..semiIdx] : contentType)
            .Trim()
            .ToLowerInvariant();

        if (string.IsNullOrEmpty(normalizedMime))
        {
            return false;
        }

        return fallbackMap.TryGetValue(normalizedMime, out extension!);
    }

    private static bool IsForbiddenFileNameCodePoint(int cp) => cp is
        <= 0x1F or
        0x7F or
        (>= 0x80 and <= 0x9F) or
        '"' or
        '\'' or
        ';' or
        '/' or
        '\\' or
        0x061C or
        0x200B or
        0x200C or
        0x200D or
        0x200E or
        0x200F or
        (>= 0x202A and <= 0x202E) or
        (>= 0x2066 and <= 0x2069) or
        0xFEFF;
}
