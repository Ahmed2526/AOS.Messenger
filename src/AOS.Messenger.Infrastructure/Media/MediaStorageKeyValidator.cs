using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Infrastructure.Storage;

internal static class MediaStorageKeyValidator
{
    public static void ValidateObjectKey(string? objectKey, string? requiredPrefix = null)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new DomainException("Storage object key cannot be empty.");
        }

        string matchedPrefix;
        if (objectKey.StartsWith(MediaConstraints.PendingKeyPrefix, StringComparison.Ordinal))
        {
            matchedPrefix = MediaConstraints.PendingKeyPrefix;
        }
        else if (objectKey.StartsWith(MediaConstraints.MediaKeyPrefix, StringComparison.Ordinal))
        {
            matchedPrefix = MediaConstraints.MediaKeyPrefix;
        }
        else
        {
            throw new DomainException("Storage object key has an invalid prefix.");
        }

        if (requiredPrefix is not null && !string.Equals(matchedPrefix, requiredPrefix, StringComparison.Ordinal))
        {
            throw new DomainException($"Storage object key must start with '{requiredPrefix}'.");
        }

        var suffix = objectKey[matchedPrefix.Length..];
        if (!MediaConstraints.IsValidStorageObjectId(suffix))
        {
            throw new DomainException("Storage object key suffix must be a 32-character lowercase hexadecimal identifier.");
        }
    }

    public static void ValidatePrefix(string? prefix)
    {
        if (!string.Equals(prefix, MediaConstraints.PendingKeyPrefix, StringComparison.Ordinal) &&
            !string.Equals(prefix, MediaConstraints.MediaKeyPrefix, StringComparison.Ordinal))
        {
            throw new DomainException("Storage listing prefix must be 'pending/' or 'media/'.");
        }
    }
}
