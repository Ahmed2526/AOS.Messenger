using System.Text.RegularExpressions;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Domain.Constants;

/// <summary>
/// Domain bounds, validation allowlists, and canonical formatting rules for system and custom R2 stickers.
/// </summary>
public static partial class StickerConstraints
{
    public const long MaxRawStickerUploadBytes = 5L * 1024 * 1024;
    public const long MaxStaticWebpOutputBytes = 500L * 1024;
    public const long MaxAnimatedWebpOutputBytes = 1024L * 1024;
    public const int CanonicalStickerSize = 512;

    public const int MaxAnimationFrames = 120;
    public const int MaxAnimationFrameDimension = 2048;
    public const long MaxAnimationTotalPixels = 32L * 1024 * 1024;
    public const int MinFrameDelayMs = 40;
    public const int MinRawDelayThresholdMs = 20;
    public const int MaxAnimationDurationMs = 10_000;

    public const int MaxCustomPacksPerUser = 10;
    public const int MaxStickersPerCustomPack = 30;
    public const int MaxTotalCustomStickersPerUser = 150;
    public const int MaxFavoriteStickersPerUser = 200;
    public const int MaxRecentStickersPerUser = 36;

    public const int MaxPackNameLength = 64;
    public const int MaxAuthorNameLength = 64;
    public const int MaxEmojiTagsLength = 128;
    public const int MaxSlugLength = 64;

    public const int StickerPresignedGetTtlMinutes = 10;
    public const string CanonicalStickerContentType = "image/webp";
    public const string StickerPreviewText = "🏷️ Sticker";
    public const string DefaultCustomPackName = "My Stickers";
    public const string NeutralCommunityAuthor = "Community Sticker";

    [GeneratedRegex("^/stickers/packs/[a-z0-9-]+/[a-z0-9-]+\\.webp$", RegexOptions.CultureInvariant)]
    private static partial Regex StaticAssetPathRegex();

    [GeneratedRegex("^[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    private static readonly IReadOnlyDictionary<string, (string CanonicalMime, IReadOnlySet<string> AllowedDeclaredMimes)> AllowedFormats =
        new Dictionary<string, (string CanonicalMime, IReadOnlySet<string> AllowedDeclaredMimes)>(StringComparer.OrdinalIgnoreCase)
        {
            [".webp"] = ("image/webp", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/webp" }),
            [".png"] = ("image/png", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png" }),
            [".jpg"] = ("image/jpeg", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg" }),
            [".jpeg"] = ("image/jpeg", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg" }),
            [".gif"] = ("image/gif", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/gif" })
        };

    public static bool IsValidStaticAssetPath(string? path) =>
        !string.IsNullOrWhiteSpace(path) && StaticAssetPathRegex().IsMatch(path);

    public static bool IsValidSlug(string? slug) =>
        !string.IsNullOrWhiteSpace(slug) && slug.Length <= MaxSlugLength && SlugRegex().IsMatch(slug);

    public static string NormalizeEmojiTags(string? emojiTags)
    {
        if (string.IsNullOrWhiteSpace(emojiTags))
        {
            return string.Empty;
        }

        var trimmed = emojiTags.Trim();
        if (trimmed.Length > MaxEmojiTagsLength)
        {
            throw new DomainException($"Sticker tags cannot exceed {MaxEmojiTagsLength} characters.");
        }

        return trimmed;
    }

    public static string ResolveAllowedStickerUpload(string? rawFileName, string? declaredContentType)
    {
        if (string.IsNullOrWhiteSpace(rawFileName))
        {
            throw new DomainException("File name cannot be empty.");
        }

        if (rawFileName.Length > MediaConstraints.MaxRawFileNameLength)
        {
            throw new DomainException($"File name cannot exceed {MediaConstraints.MaxRawFileNameLength} characters.");
        }

        var lastSep = rawFileName.LastIndexOfAny(['/', '\\']);
        var segment = lastSep >= 0 ? rawFileName[(lastSep + 1)..] : rawFileName;
        var ext = Path.GetExtension(segment.Trim().TrimEnd('.')).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || !AllowedFormats.TryGetValue(ext, out var spec))
        {
            throw new DomainException("Stickers must be WebP, PNG, JPEG, or GIF images.");
        }

        if (!string.IsNullOrWhiteSpace(declaredContentType))
        {
            if (declaredContentType.Length > MediaConstraints.MaxDeclaredContentTypeLength)
            {
                throw new DomainException($"Content type cannot exceed {MediaConstraints.MaxDeclaredContentTypeLength} characters.");
            }

            var semiIdx = declaredContentType.IndexOf(';');
            var normalizedMime = (semiIdx >= 0 ? declaredContentType[..semiIdx] : declaredContentType)
                .Trim()
                .ToLowerInvariant();

            if (!string.IsNullOrEmpty(normalizedMime)
                && !string.Equals(normalizedMime, "application/octet-stream", StringComparison.Ordinal)
                && !spec.AllowedDeclaredMimes.Contains(normalizedMime))
            {
                throw new DomainException($"Declared content type is not valid for '{ext}' sticker images.");
            }
        }

        return spec.CanonicalMime;
    }
}
