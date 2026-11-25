using System.Security.Cryptography;
using System.Text.Json;
using AOS.Messenger.Domain.Constants;
using Microsoft.AspNetCore.DataProtection;

namespace AOS.Messenger.Infrastructure.Storage;

internal sealed record LocalMediaTokenPayload(
    string Action,
    string ObjectKey,
    string ContentType,
    long? ContentLength,
    string? ContentDisposition,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Issues and verifies single-purpose AEAD DataProtection tokens for the Development local-disk media endpoints.
/// </summary>
public sealed class LocalMediaSigningService
{
    public const string PutAction = "put";
    public const string GetAction = "get";
    private const string ProtectorPurpose = "AOS.Messenger.DevMedia.UrlToken.v1";

    private readonly IDataProtector _protector;

    public LocalMediaSigningService(IDataProtectionProvider dataProtectionProvider)
    {
        ArgumentNullException.ThrowIfNull(dataProtectionProvider);
        _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
    }

    public string CreatePutToken(
        string objectKey,
        string contentType,
        long contentLength,
        DateTimeOffset expiresAtUtc)
    {
        MediaStorageKeyValidator.ValidateObjectKey(objectKey, MediaConstraints.PendingKeyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentLength);

        var payload = new LocalMediaTokenPayload(
            PutAction,
            objectKey,
            contentType.Trim(),
            contentLength,
            null,
            expiresAtUtc);

        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    public string CreateGetToken(
        string objectKey,
        string responseContentType,
        string responseContentDisposition,
        DateTimeOffset expiresAtUtc)
    {
        MediaStorageKeyValidator.ValidatePermanentObjectKey(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseContentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseContentDisposition);

        var payload = new LocalMediaTokenPayload(
            GetAction,
            objectKey,
            responseContentType.Trim(),
            null,
            responseContentDisposition,
            expiresAtUtc);

        return _protector.Protect(JsonSerializer.Serialize(payload));
    }

    internal bool TryValidateToken(
        string? token,
        string expectedAction,
        DateTimeOffset nowUtc,
        out LocalMediaTokenPayload? payload)
    {
        payload = null;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        try
        {
            var json = _protector.Unprotect(token);
            var parsed = JsonSerializer.Deserialize<LocalMediaTokenPayload>(json);
            if (parsed is null ||
                !string.Equals(parsed.Action, expectedAction, StringComparison.Ordinal) ||
                parsed.ExpiresAtUtc <= nowUtc ||
                string.IsNullOrWhiteSpace(parsed.ContentType))
            {
                return false;
            }

            if (string.Equals(expectedAction, PutAction, StringComparison.Ordinal))
            {
                if (!parsed.ContentLength.HasValue || parsed.ContentLength.Value <= 0)
                {
                    return false;
                }

                MediaStorageKeyValidator.ValidateObjectKey(parsed.ObjectKey, MediaConstraints.PendingKeyPrefix);
            }
            else if (string.Equals(expectedAction, GetAction, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(parsed.ContentDisposition))
                {
                    return false;
                }

                MediaStorageKeyValidator.ValidatePermanentObjectKey(parsed.ObjectKey);
            }
            else
            {
                return false;
            }

            payload = parsed;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or Domain.Exceptions.DomainException)
        {
            return false;
        }
    }
}
