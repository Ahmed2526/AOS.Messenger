using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Exceptions;

namespace AOS.Messenger.Infrastructure.Storage;

/// <summary>
/// Development-only local-disk implementation of <see cref="IMediaStorage"/> rooted at <c>.dev-media/</c>.
/// Presigned URLs point to <see cref="DevMediaStartupFilter"/> endpoints protected by AEAD DataProtection tokens.
/// </summary>
public sealed class LocalDiskMediaStorage : IMediaStorage
{
    private readonly string _rootPath;
    private readonly string _rootPathWithSeparator;
    private readonly LocalMediaSigningService _signingService;

    public LocalDiskMediaStorage(
        LocalMediaSigningService signingService,
        string? rootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(signingService);
        _signingService = signingService;

        var baseDir = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(Directory.GetCurrentDirectory(), ".dev-media")
            : rootDirectory;

        _rootPath = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _rootPathWithSeparator = _rootPath + Path.DirectorySeparatorChar;
    }

    public string GeneratePresignedPutUrl(
        string objectKey,
        string contentType,
        long contentLength,
        TimeSpan expiresIn,
        DateTimeOffset nowUtc)
    {
        MediaStorageKeyValidator.ValidateObjectKey(objectKey, MediaConstraints.PendingKeyPrefix);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresIn, TimeSpan.Zero);

        var token = _signingService.CreatePutToken(objectKey, contentType, contentLength, nowUtc.Add(expiresIn));
        return $"/dev-media/upload?token={Uri.EscapeDataString(token)}";
    }

    public string GeneratePresignedGetUrl(
        string objectKey,
        string responseContentType,
        string responseContentDisposition,
        TimeSpan expiresIn,
        DateTimeOffset nowUtc)
    {
        MediaStorageKeyValidator.ValidatePermanentObjectKey(objectKey);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresIn, TimeSpan.Zero);

        var token = _signingService.CreateGetToken(
            objectKey,
            responseContentType,
            responseContentDisposition,
            nowUtc.Add(expiresIn));
        return $"/dev-media/download?token={Uri.EscapeDataString(token)}";
    }

    public async Task<MediaObjectMetadata?> GetObjectMetadataAsync(
        string objectKey,
        CancellationToken ct = default)
    {
        MediaStorageKeyValidator.ValidateObjectKey(objectKey);
        var path = ResolveSafePath(objectKey);

        if (!File.Exists(path))
        {
            return null;
        }

        var fileInfo = new FileInfo(path);
        var etag = await ComputeETagAsync(path, ct);
        return new MediaObjectMetadata(fileInfo.Length, etag, null);
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

        var path = ResolveSafePath(objectKey);
        if (!File.Exists(path))
        {
            throw new NotFoundException("Media object", "staged");
        }

        if (!string.IsNullOrWhiteSpace(ifMatchETag))
        {
            var currentETag = await ComputeETagAsync(path, ct);
            if (!string.Equals(currentETag, ifMatchETag.Trim(), StringComparison.Ordinal))
            {
                throw new ConflictException("The staged media object was modified before verification completed.");
            }
        }

        if (length == 0)
        {
            return [];
        }

        await using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        if (offset >= fs.Length)
        {
            return [];
        }

        var toRead = (int)Math.Min(length, fs.Length - offset);
        fs.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[toRead];
        var totalRead = 0;
        while (totalRead < toRead)
        {
            var read = await fs.ReadAsync(buffer.AsMemory(totalRead, toRead - totalRead), ct);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
        }

        return totalRead == toRead ? buffer : buffer[..totalRead];
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

        var path = ResolveSafePath(objectKey);
        if (!File.Exists(path))
        {
            throw new NotFoundException("Media object", "staged");
        }

        if (!string.IsNullOrWhiteSpace(ifMatchETag))
        {
            var currentETag = await ComputeETagAsync(path, ct);
            if (!string.Equals(currentETag, ifMatchETag.Trim(), StringComparison.Ordinal))
            {
                throw new ConflictException("The staged media object was modified before download completed.");
            }
        }

        await using var fs = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        if (fs.Length > maxBytes)
        {
            throw new DomainException($"Media object size exceeds maximum allowed ceiling of {maxBytes} bytes.");
        }

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await fs.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            totalRead += read;
            if (totalRead > maxBytes)
            {
                throw new DomainException($"Media object size exceeds maximum allowed ceiling of {maxBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
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
        _ = contentDisposition;

        var stagingPath = ResolveSafePath(stagingKey);
        if (!File.Exists(stagingPath))
        {
            throw new NotFoundException("Media object", "staged");
        }

        if (!string.IsNullOrWhiteSpace(ifMatchETag))
        {
            var currentETag = await ComputeETagAsync(stagingPath, ct);
            if (!string.Equals(currentETag, ifMatchETag.Trim(), StringComparison.Ordinal))
            {
                throw new ConflictException("The staged media object changed concurrently during promotion.");
            }
        }

        var permanentPath = ResolveSafePath(permanentKey);
        Directory.CreateDirectory(Path.GetDirectoryName(permanentPath)!);

        var tempPath = $"{permanentPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var src = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var dst = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await src.CopyToAsync(dst, ct);
            }

            File.Move(tempPath, permanentPath, overwrite: false);
        }
        catch (IOException ex) when (File.Exists(permanentPath))
        {
            TryDeleteFileQuietly(tempPath);
            throw new ConflictException("The permanent media object already exists.", ex);
        }
        catch
        {
            TryDeleteFileQuietly(tempPath);
            throw;
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
        _ = contentDisposition;

        var permanentPath = ResolveSafePath(permanentKey);
        Directory.CreateDirectory(Path.GetDirectoryName(permanentPath)!);

        var tempPath = $"{permanentPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            long totalCopied = 0;
            var buffer = new byte[81920];

            await using (var dst = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(), ct)) > 0)
                {
                    totalCopied += read;
                    if (totalCopied > contentLength)
                    {
                        throw new MediaStorageException("Processed media stream exceeded declared content length.");
                    }

                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            if (totalCopied != contentLength)
            {
                throw new MediaStorageException("Processed media stream size did not match declared content length.");
            }

            File.Move(tempPath, permanentPath, overwrite: false);
        }
        catch (IOException ex) when (File.Exists(permanentPath))
        {
            TryDeleteFileQuietly(tempPath);
            throw new ConflictException("The permanent media object already exists.", ex);
        }
        catch
        {
            TryDeleteFileQuietly(tempPath);
            throw;
        }
    }

    public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
    {
        MediaStorageKeyValidator.ValidateObjectKey(objectKey);
        ct.ThrowIfCancellationRequested();

        var path = ResolveSafePath(objectKey);
        TryDeleteFileQuietly(path);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StoredObjectInfo> ListObjectsAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        MediaStorageKeyValidator.ValidatePrefix(prefix);
        await Task.Yield();

        var folderName = prefix.TrimEnd('/');
        var dirPath = Path.Combine(_rootPath, folderName);
        if (!Directory.Exists(dirPath))
        {
            yield break;
        }

        foreach (var filePath in Directory.EnumerateFiles(dirPath, "*", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();

            if (filePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = Path.GetFileName(filePath);
            if (!MediaConstraints.IsValidStorageObjectId(fileName))
            {
                continue;
            }

            FileInfo info;
            try
            {
                info = new FileInfo(filePath);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }

            yield return new StoredObjectInfo(
                $"{folderName}/{fileName}",
                new DateTimeOffset(DateTime.SpecifyKind(info.LastWriteTimeUtc, DateTimeKind.Utc)),
                info.Length);
        }
    }

    internal string ResolveSafePath(string objectKey)
    {
        MediaStorageKeyValidator.ValidateObjectKey(objectKey);
        var relative = objectKey.Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.GetFullPath(Path.Combine(_rootPath, relative));

        if (!combined.StartsWith(_rootPathWithSeparator, StringComparison.Ordinal))
        {
            throw new DomainException("Resolved storage path escapes storage root.");
        }

        return combined;
    }

    internal async Task<string> ComputeETagAsync(string fullPath, CancellationToken ct)
    {
        await using var fs = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        var hash = await SHA256.HashDataAsync(fs, ct);
        return $"\"{Convert.ToHexStringLower(hash)}\"";
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
