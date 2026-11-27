using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Infrastructure.Storage;

/// <summary>
/// Cloudflare R2 implementation of <see cref="IMediaStorage"/> using the official AWS S3 SDK over R2's S3 API endpoint.
/// Never deletes staging objects inside promotion methods; callers delete staging strictly after SQL commit.
/// </summary>
public sealed class CloudflareR2MediaStorage : IMediaStorage, IDisposable
{
    private const string StrictCacheControl = "private, no-store, no-cache";

    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly ILogger<CloudflareR2MediaStorage> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly bool _ownsClient;

    public CloudflareR2MediaStorage(
        IOptions<R2StorageOptions> options,
        ILogger<CloudflareR2MediaStorage> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        var r2 = options.Value;
        if (!r2.IsConfigured)
        {
            throw new InvalidOperationException("R2 storage options must be fully configured to initialize CloudflareR2MediaStorage.");
        }

        _bucketName = r2.BucketName!.Trim();
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsClient = true;

        var credentials = new BasicAWSCredentials(r2.AccessKeyId!.Trim(), r2.SecretAccessKey!.Trim());
        var config = new AmazonS3Config
        {
            ServiceURL = r2.ServiceUrl!.Trim().TrimEnd('/'),
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromSeconds(90)
        };

        _s3Client = new AmazonS3Client(credentials, config);
    }

    internal CloudflareR2MediaStorage(
        IAmazonS3 s3Client,
        R2StorageOptions options,
        ILogger<CloudflareR2MediaStorage> logger,
        bool ownsClient = false,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(s3Client);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(options.BucketName))
        {
            throw new ArgumentException("BucketName is required.", nameof(options));
        }

        _s3Client = s3Client;
        _bucketName = options.BucketName.Trim();
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _ownsClient = ownsClient;
    }

    public string GeneratePresignedPutUrl(
        string objectKey,
        string contentType,
        long contentLength,
        TimeSpan expiresIn,
        DateTimeOffset nowUtc)
    {
        MediaStorageKeyValidator.ValidateObjectKey(objectKey, MediaConstraints.PendingKeyPrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentLength);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresIn, TimeSpan.Zero);

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = objectKey,
            Verb = HttpVerb.PUT,
            Protocol = Protocol.HTTPS,
            Expires = nowUtc.Add(expiresIn).UtcDateTime,
            ContentType = contentType.Trim()
        };
        request.Headers["Content-Length"] = contentLength.ToString(CultureInfo.InvariantCulture);

        try
        {
            return _s3Client.GetPreSignedURL(request);
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            _logger.LogError(ex, "Failed to generate R2 presigned PUT URL for {ObjectKey}", objectKey);
            throw new MediaStorageException("Unable to generate media upload URL.", ex);
        }
    }

    public string GeneratePresignedGetUrl(
        string objectKey,
        string responseContentType,
        string responseContentDisposition,
        TimeSpan expiresIn,
        DateTimeOffset nowUtc)
    {
        MediaStorageKeyValidator.ValidatePermanentObjectKey(objectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseContentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(responseContentDisposition);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresIn, TimeSpan.Zero);

        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = objectKey,
            Verb = HttpVerb.GET,
            Protocol = Protocol.HTTPS,
            Expires = nowUtc.Add(expiresIn).UtcDateTime,
            ResponseHeaderOverrides = new ResponseHeaderOverrides
            {
                ContentType = responseContentType.Trim(),
                CacheControl = StrictCacheControl,
                ContentDisposition = responseContentDisposition
            }
        };

        try
        {
            return _s3Client.GetPreSignedURL(request);
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException)
        {
            _logger.LogError(ex, "Failed to generate R2 presigned GET URL for {ObjectKey}", objectKey);
            throw new MediaStorageException("Unable to generate media download URL.", ex);
        }
    }

    public async Task<MediaObjectMetadata?> GetObjectMetadataAsync(
        string objectKey,
        CancellationToken ct = default)
    {
        MediaStorageKeyValidator.ValidateObjectKey(objectKey);

        try
        {
            var response = await _s3Client.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucketName,
                Key = objectKey
            }, ct);

            return new MediaObjectMetadata(
                response.ContentLength,
                response.ETag,
                response.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            return null;
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Failed to read R2 object metadata for {ObjectKey}", objectKey);
            throw new MediaStorageException("Unable to inspect uploaded media metadata.", ex);
        }
    }

    public async Task<byte[]> ReadByteRangeAsync(
        string objectKey,
        long offset,
        int length,
        string? ifMatchETag = null,
        CancellationToken ct = default)
    {
        MediaStorageKeyValidator.ValidateObjectKey(objectKey);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);

        if (length == 0)
        {
            return [];
        }

        var request = new GetObjectRequest
        {
            BucketName = _bucketName,
            Key = objectKey,
            ByteRange = new ByteRange(offset, offset + length - 1)
        };

        if (!string.IsNullOrWhiteSpace(ifMatchETag))
        {
            request.EtagToMatch = ifMatchETag.Trim();
        }

        try
        {
            using var response = await _s3Client.GetObjectAsync(request, ct);
            var buffer = new byte[length];
            var totalRead = 0;

            while (totalRead < length)
            {
                var read = await response.ResponseStream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return totalRead == length ? buffer : buffer[..totalRead];
        }
        catch (AmazonS3Exception ex) when (IsRangeNotSatisfiable(ex))
        {
            return [];
        }
        catch (AmazonS3Exception ex) when (IsPreconditionFailed(ex))
        {
            throw new ConflictException("The staged media object was modified before verification completed.", ex);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            throw new NotFoundException("Media object", "staged");
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Failed to read R2 byte range for {ObjectKey}", objectKey);
            throw new MediaStorageException("Unable to read media object header bytes.", ex);
        }
    }

    public async Task DownloadToStreamAsync(
        string objectKey,
        Stream destination,
        long maxBytes,
        string? ifMatchETag = null,
        CancellationToken ct = default)
    {
        MediaStorageKeyValidator.ValidateObjectKey(objectKey);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);

        var request = new GetObjectRequest
        {
            BucketName = _bucketName,
            Key = objectKey
        };

        if (!string.IsNullOrWhiteSpace(ifMatchETag))
        {
            request.EtagToMatch = ifMatchETag.Trim();
        }

        try
        {
            using var response = await _s3Client.GetObjectAsync(request, ct);
            if (response.ContentLength > maxBytes)
            {
                throw new DomainException($"Media object size exceeds maximum allowed ceiling of {maxBytes} bytes.");
            }

            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await response.ResponseStream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                totalRead += read;
                if (totalRead > maxBytes)
                {
                    throw new DomainException($"Media object size exceeds maximum allowed ceiling of {maxBytes} bytes.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch (AmazonS3Exception ex) when (IsPreconditionFailed(ex))
        {
            throw new ConflictException("The staged media object was modified before download completed.", ex);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            throw new NotFoundException("Media object", "staged");
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Failed to download R2 object {ObjectKey}", objectKey);
            throw new MediaStorageException("Unable to download staged media object.", ex);
        }
    }

    public async Task PromoteByCopyAsync(
        string stagingKey,
        string permanentKey,
        string verifiedContentType,
        string? contentDisposition,
        string? ifMatchETag = null,
        CancellationToken ct = default)
    {
        MediaStorageKeyValidator.ValidateObjectKey(stagingKey, MediaConstraints.PendingKeyPrefix);
        MediaStorageKeyValidator.ValidatePermanentObjectKey(permanentKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedContentType);

        var request = new CopyObjectRequest
        {
            SourceBucket = _bucketName,
            SourceKey = stagingKey,
            DestinationBucket = _bucketName,
            DestinationKey = permanentKey,
            MetadataDirective = S3MetadataDirective.REPLACE,
            ContentType = verifiedContentType.Trim()
        };
        request.Headers.CacheControl = StrictCacheControl;

        if (!string.IsNullOrWhiteSpace(contentDisposition))
        {
            request.Headers.ContentDisposition = contentDisposition;
        }

        if (!string.IsNullOrWhiteSpace(ifMatchETag))
        {
            request.ETagToMatch = ifMatchETag.Trim();
        }

        try
        {
            await _s3Client.CopyObjectAsync(request, ct);
        }
        catch (AmazonS3Exception ex) when (IsPreconditionFailed(ex))
        {
            throw new ConflictException("The staged media object changed concurrently during promotion.", ex);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            throw new NotFoundException("Media object", "staged");
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Failed to promote R2 object {StagingKey} to {PermanentKey}", stagingKey, permanentKey);
            throw new MediaStorageException("Unable to promote verified media object.", ex);
        }
    }

    public async Task PromoteFromStreamAsync(
        string stagingKey,
        string permanentKey,
        Stream content,
        long contentLength,
        string verifiedContentType,
        string? contentDisposition,
        CancellationToken ct = default)
    {
        MediaStorageKeyValidator.ValidateObjectKey(stagingKey, MediaConstraints.PendingKeyPrefix);
        MediaStorageKeyValidator.ValidatePermanentObjectKey(permanentKey);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contentLength);
        ArgumentException.ThrowIfNullOrWhiteSpace(verifiedContentType);

        var request = new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = permanentKey,
            InputStream = content,
            AutoCloseStream = false,
            ContentType = verifiedContentType.Trim(),
            DisablePayloadSigning = true,
            IfNoneMatch = "*"
        };
        request.Headers.ContentLength = contentLength;
        request.Headers.CacheControl = StrictCacheControl;

        if (!string.IsNullOrWhiteSpace(contentDisposition))
        {
            request.Headers.ContentDisposition = contentDisposition;
        }

        try
        {
            await _s3Client.PutObjectAsync(request, ct);
        }
        catch (AmazonS3Exception ex) when (IsPreconditionFailed(ex))
        {
            throw new ConflictException("The permanent media object already exists.", ex);
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Failed to upload processed media stream to {PermanentKey}", permanentKey);
            throw new MediaStorageException("Unable to store processed media object.", ex);
        }
    }

    public async Task DeleteObjectAsync(
        string objectKey,
        CancellationToken ct = default)
    {
        MediaStorageKeyValidator.ValidateObjectKey(objectKey);

        try
        {
            await _s3Client.DeleteObjectAsync(new DeleteObjectRequest
            {
                BucketName = _bucketName,
                Key = objectKey
            }, ct);
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            // Idempotent delete
        }
        catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException
                                   || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _logger.LogWarning(ex, "Failed to delete R2 object {ObjectKey}", objectKey);
            throw new MediaStorageException("Unable to delete media object.", ex);
        }
    }

    public async IAsyncEnumerable<StoredObjectInfo> ListObjectsAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        MediaStorageKeyValidator.ValidatePrefix(prefix);

        string? continuationToken = null;
        do
        {
            ListObjectsV2Response response;
            try
            {
                response = await _s3Client.ListObjectsV2Async(new ListObjectsV2Request
                {
                    BucketName = _bucketName,
                    Prefix = prefix,
                    MaxKeys = 500,
                    ContinuationToken = continuationToken
                }, ct);
            }
            catch (Exception ex) when (ex is AmazonServiceException or AmazonClientException or HttpRequestException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _logger.LogWarning(ex, "Failed to list R2 objects for prefix {Prefix}", prefix);
                throw new MediaStorageException("Unable to enumerate stored media objects.", ex);
            }

            if (response.S3Objects is not null)
            {
                foreach (var obj in response.S3Objects)
                {
                    if (string.IsNullOrWhiteSpace(obj.Key))
                    {
                        continue;
                    }

                    DateTimeOffset lastModifiedUtc;
                    if (obj.LastModified.HasValue)
                    {
                        var dt = obj.LastModified.Value;
                        var utcDt = dt.Kind == DateTimeKind.Local
                            ? dt.ToUniversalTime()
                            : DateTime.SpecifyKind(dt, DateTimeKind.Utc);
                        lastModifiedUtc = new DateTimeOffset(utcDt);
                    }
                    else
                    {
                        lastModifiedUtc = _timeProvider.GetUtcNow();
                    }

                    yield return new StoredObjectInfo(
                        obj.Key,
                        lastModifiedUtc,
                        obj.Size ?? 0L);
                }
            }

            continuationToken = response.IsTruncated == true
                ? response.NextContinuationToken
                : null;
        }
        while (!string.IsNullOrEmpty(continuationToken));
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _s3Client.Dispose();
        }
    }

    private static bool IsNotFound(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.NotFound ||
        string.Equals(ex.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(ex.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase);

    private static bool IsPreconditionFailed(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.PreconditionFailed ||
        string.Equals(ex.ErrorCode, "PreconditionFailed", StringComparison.OrdinalIgnoreCase);

    private static bool IsRangeNotSatisfiable(AmazonS3Exception ex) =>
        ex.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable ||
        string.Equals(ex.ErrorCode, "InvalidRange", StringComparison.OrdinalIgnoreCase);
}
