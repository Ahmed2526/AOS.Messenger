namespace AOS.Messenger.Application.Common.Interfaces;

public sealed record MediaObjectMetadata(
    long ContentLength,
    string? ETag,
    string? ContentType);

public sealed record StoredObjectInfo(
    string ObjectKey,
    DateTimeOffset LastModifiedUtc,
    long SizeBytes);

/// <summary>
/// Object storage abstraction implemented by Infrastructure (Cloudflare R2 S3 API in Production/configured Dev,
/// or signed local-disk simulator in unconfigured Development).
/// </summary>
public interface IMediaStorage
{
    string GeneratePresignedPutUrl(
        string objectKey,
        string contentType,
        long contentLength,
        TimeSpan expiresIn,
        DateTimeOffset nowUtc);

    string GeneratePresignedGetUrl(
        string objectKey,
        string responseContentType,
        string responseContentDisposition,
        TimeSpan expiresIn,
        DateTimeOffset nowUtc);

    Task<MediaObjectMetadata?> GetObjectMetadataAsync(
        string objectKey,
        CancellationToken ct = default);

    Task<byte[]> ReadByteRangeAsync(
        string objectKey,
        long offset,
        int length,
        string? ifMatchETag = null,
        CancellationToken ct = default);

    Task DownloadToStreamAsync(
        string objectKey,
        Stream destination,
        long maxBytes,
        string? ifMatchETag = null,
        CancellationToken ct = default);

    /// <summary>
    /// Promotes a verified object from staging to permanent key via server-side copy with strict Cache-Control
    /// (and Content-Disposition when specified). Implementations MUST NOT delete <paramref name="stagingKey"/>;
    /// staging deletion happens strictly after the database commit in the caller.
    /// </summary>
    Task PromoteByCopyAsync(
        string stagingKey,
        string permanentKey,
        string verifiedContentType,
        string? contentDisposition,
        string? ifMatchETag = null,
        CancellationToken ct = default);

    /// <summary>
    /// Writes a re-encoded stream directly to the permanent key with strict Cache-Control
    /// (and Content-Disposition when specified). Implementations MUST NOT delete <paramref name="stagingKey"/>;
    /// staging deletion happens strictly after the database commit in the caller.
    /// </summary>
    Task PromoteFromStreamAsync(
        string stagingKey,
        string permanentKey,
        Stream content,
        long contentLength,
        string verifiedContentType,
        string? contentDisposition,
        CancellationToken ct = default);

    Task DeleteObjectAsync(
        string objectKey,
        CancellationToken ct = default);

    IAsyncEnumerable<StoredObjectInfo> ListObjectsAsync(
        string prefix,
        CancellationToken ct = default);
}
