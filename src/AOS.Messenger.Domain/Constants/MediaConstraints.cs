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

    public const int MaxImageWidth = 8192;
    public const int MaxImageHeight = 8192;
    public const long MaxImagePixels = 33_000_000L;
    public const int MaxAnimationFrames = 300;
    public const long MaxTotalDecodedImagePixels = 40_000_000L;

    public const int MaxSanitizedFileNameLength = 150;
    public const int MaxRawFileNameLength = 1024;
    public const int MaxDeclaredContentTypeLength = 256;
    public const int MaxFailureReasonLength = 256;

    public const string PendingKeyPrefix = "pending/";
    public const string MediaKeyPrefix = "media/";

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

    public static string SanitizeFileName(string? rawFileName, string? fallbackContentType = null)
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
            if (!TryGetFallbackExtension(fallbackContentType, out var fallbackExt))
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
            stem = "attachment";
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
        string? declaredContentType)
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
        if (string.IsNullOrEmpty(ext) || !Phase9Allowlist.TryGetValue(ext, out var spec))
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

    private static bool TryGetFallbackExtension(string? contentType, out string extension)
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

        return FallbackExtensionByMime.TryGetValue(normalizedMime, out extension!);
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
