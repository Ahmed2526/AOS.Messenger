using System.Security.Cryptography;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Domain.Constants;

/// <summary>
/// Domain constraints and format allowlists for user profile photos stored in Cloudflare R2.
/// </summary>
public static class AvatarConstraints
{
    public const long MaxRawAvatarUploadBytes = 5L * 1024 * 1024;
    public const int MaxFullAvatarDimension = 1024;
    public const long MaxFullAvatarOutputBytes = 1024L * 1024;
    public const int ThumbnailAvatarDimension = 128;
    public const long MaxThumbnailAvatarOutputBytes = 64L * 1024;
    public const int AvatarPresignedGetTtlMinutes = 5;
    public const int MaxAvatarVersionLength = 32;
    public const string CanonicalAvatarContentType = "image/webp";

    private static readonly IReadOnlyDictionary<string, (string CanonicalMime, IReadOnlySet<string> AllowedDeclaredMimes)> AllowedFormats =
        new Dictionary<string, (string CanonicalMime, IReadOnlySet<string> AllowedDeclaredMimes)>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = ("image/jpeg", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg" }),
            [".jpeg"] = ("image/jpeg", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/jpeg", "image/jpg" }),
            [".png"] = ("image/png", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png" }),
            [".webp"] = ("image/webp", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/webp" })
        };

    public static string ResolveAllowedAvatarUpload(string? rawFileName, string? declaredContentType)
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
            throw new DomainException("Profile photos must be JPEG, PNG, or static WebP images.");
        }

        if (!string.IsNullOrWhiteSpace(declaredContentType))
        {
            var trimmedMime = declaredContentType.Trim();
            if (trimmedMime.Length > MediaConstraints.MaxDeclaredContentTypeLength)
            {
                throw new DomainException($"Content type cannot exceed {MediaConstraints.MaxDeclaredContentTypeLength} characters.");
            }

            if (!spec.AllowedDeclaredMimes.Contains(trimmedMime))
            {
                throw new DomainException($"Content type '{trimmedMime}' is not valid for '{ext}' profile photos.");
            }
        }

        return spec.CanonicalMime;
    }

    public static string GenerateAvatarVersion() =>
        RandomNumberGenerator.GetHexString(16, lowercase: true);
}
