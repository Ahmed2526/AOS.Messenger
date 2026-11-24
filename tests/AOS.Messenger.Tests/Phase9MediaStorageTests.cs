using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase9MediaStorageTests : IClassFixture<SqlTestDatabaseFixture>, IDisposable
{
    private readonly SqlTestDatabaseFixture _fixture;
    private readonly string _tempRoot;

    public Phase9MediaStorageTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _tempRoot = Path.Combine(Path.GetTempPath(), $"aos_media_storage_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    // =========================================================================
    // 1. R2StorageOptionsValidator Tests
    // =========================================================================

    [Fact]
    public void R2StorageOptionsValidator_EnforcesEnvironmentAndPureS3OriginRules()
    {
        var devEnv = new FakeHostEnvironment(Environments.Development);
        var prodEnv = new FakeHostEnvironment(Environments.Production);

        // Empty config: valid in Development, fails fast in Production
        var empty = new R2StorageOptions();
        Assert.True(new R2StorageOptionsValidator(devEnv).Validate(null, empty).Succeeded);
        Assert.False(new R2StorageOptionsValidator(prodEnv).Validate(null, empty).Succeeded);

        // Partial config fails even in Development
        var partial = new R2StorageOptions { ServiceUrl = "https://abc.r2.cloudflarestorage.com" };
        Assert.False(new R2StorageOptionsValidator(devEnv).Validate(null, partial).Succeeded);

        // Invalid ServiceUrl schemes, wildcards, paths, userinfo, queries
        foreach (var badUrl in new[]
        {
            "http://abc.r2.cloudflarestorage.com",
            "https://user:pass@abc.r2.cloudflarestorage.com",
            "https://*.r2.cloudflarestorage.com",
            "https://abc.r2.cloudflarestorage.com/bucket",
            "https://abc.r2.cloudflarestorage.com?foo=bar",
            "https://abc.r2.cloudflarestorage.com#frag",
            "not-a-uri"
        })
        {
            var badOpts = new R2StorageOptions
            {
                ServiceUrl = badUrl,
                BucketName = "aos-media",
                AccessKeyId = "ak",
                SecretAccessKey = "sk"
            };
            Assert.False(new R2StorageOptionsValidator(prodEnv).Validate(null, badOpts).Succeeded, $"Expected failure for {badUrl}");
        }

        // Valid Production S3 origin succeeds
        var validOpts = new R2StorageOptions
        {
            ServiceUrl = "https://0123456789abcdef.r2.cloudflarestorage.com/",
            BucketName = "aos-media-prod",
            AccessKeyId = "test-access-key",
            SecretAccessKey = "test-secret-key"
        };
        Assert.True(new R2StorageOptionsValidator(prodEnv).Validate(null, validOpts).Succeeded);
    }

    // =========================================================================
    // 2. CloudflareR2MediaStorage Presigning, Operations & Error Translation
    // =========================================================================

    [Fact]
    public void CloudflareR2MediaStorage_GeneratesExpectedSigV4PresignedUrls_WithoutChecksumQueryParams()
    {
        var r2Opts = Options.Create(new R2StorageOptions
        {
            ServiceUrl = "https://0123456789abcdef.r2.cloudflarestorage.com",
            BucketName = "aos-media-prod",
            AccessKeyId = "AKIATESTACCESSKEY123",
            SecretAccessKey = "SecretTestKeyValue0123456789abcdef"
        });

        using var storage = new CloudflareR2MediaStorage(r2Opts, NullLogger<CloudflareR2MediaStorage>.Instance);
        var objId = MediaConstraints.GenerateStorageObjectId();
        var pendingKey = $"pending/{objId}";
        var mediaKey = $"media/{objId}";
        var now = new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);

        var putUrl = storage.GeneratePresignedPutUrl(pendingKey, "image/png", 12345, TimeSpan.FromMinutes(5), now);
        Assert.StartsWith($"https://0123456789abcdef.r2.cloudflarestorage.com/aos-media-prod/{pendingKey}?", putUrl, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Algorithm=AWS4-HMAC-SHA256", putUrl, StringComparison.Ordinal);
        Assert.Contains("%2Fauto%2Fs3%2Faws4_request", putUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("X-Amz-SignedHeaders=", putUrl, StringComparison.Ordinal);
        Assert.Contains("content-length", putUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", putUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-amz-checksum", putUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x-amz-sdk-checksum-algorithm", putUrl, StringComparison.OrdinalIgnoreCase);

        var disp = MediaConstraints.BuildContentDisposition("report 2026.pdf", isAttachment: true);
        var getUrl = storage.GeneratePresignedGetUrl(mediaKey, "application/pdf", disp, TimeSpan.FromMinutes(10), now);
        Assert.StartsWith($"https://0123456789abcdef.r2.cloudflarestorage.com/aos-media-prod/{mediaKey}?", getUrl, StringComparison.Ordinal);
        Assert.Contains("response-content-type=application%2Fpdf", getUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("response-cache-control=private", getUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("response-content-disposition=attachment", getUrl, StringComparison.OrdinalIgnoreCase);

        // Key prefix invariants enforced
        Assert.Throws<DomainException>(() => storage.GeneratePresignedPutUrl(mediaKey, "image/png", 10, TimeSpan.FromMinutes(1), now));
        Assert.Throws<DomainException>(() => storage.GeneratePresignedGetUrl(pendingKey, "image/png", "inline", TimeSpan.FromMinutes(1), now));
        Assert.Throws<DomainException>(() => storage.GeneratePresignedPutUrl("pending/../evil", "image/png", 10, TimeSpan.FromMinutes(1), now));
    }

    [Fact]
    public async Task CloudflareR2MediaStorage_PromoteOperations_SetHeaders_PreserveStream_AndNeverDeleteStaging()
    {
        var listPage = 0;
        var payloadBytes = Encoding.UTF8.GetBytes("hello-world-video-header");

        var (s3Proxy, calls) = RecordingAmazonS3Proxy.Create((methodName, req) =>
        {
            return methodName switch
            {
                nameof(IAmazonS3.CopyObjectAsync) => Task.FromResult(new CopyObjectResponse { HttpStatusCode = HttpStatusCode.OK }),
                nameof(IAmazonS3.PutObjectAsync) => Task.FromResult(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK }),
                nameof(IAmazonS3.GetObjectMetadataAsync) => Task.FromException<GetObjectMetadataResponse>(
                    new AmazonS3Exception("NotFound") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" }),
                nameof(IAmazonS3.DeleteObjectAsync) => Task.FromException<DeleteObjectResponse>(
                    new AmazonS3Exception("NotFound") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" }),
                nameof(IAmazonS3.ListObjectsV2Async) => ++listPage == 1
                    ? Task.FromResult(new ListObjectsV2Response
                    {
                        IsTruncated = true,
                        NextContinuationToken = "page-2",
                        S3Objects = [new S3Object { Key = $"pending/{new string('a', 32)}", Size = 100, LastModified = DateTime.UtcNow }]
                    })
                    : Task.FromResult(new ListObjectsV2Response
                    {
                        IsTruncated = false,
                        S3Objects = null // Verify AWSSDK v4 null S3Objects safety
                    }),
                nameof(IAmazonS3.GetObjectAsync) => ((GetObjectRequest)req!).ByteRange is not null
                    ? Task.FromException<GetObjectResponse>(
                        new AmazonS3Exception("Range invalid") { StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable })
                    : Task.FromResult(new GetObjectResponse
                    {
                        HttpStatusCode = HttpStatusCode.OK,
                        ContentLength = payloadBytes.Length,
                        ResponseStream = new MemoryStream(payloadBytes, writable: false)
                    }),
                _ => throw new NotSupportedException($"Unexpected call {methodName}")
            };
        });

        var options = new R2StorageOptions { BucketName = "aos-media" };
        using var storage = new CloudflareR2MediaStorage(s3Proxy, options, NullLogger<CloudflareR2MediaStorage>.Instance, ownsClient: false);

        var objId = MediaConstraints.GenerateStorageObjectId();
        var pendingKey = $"pending/{objId}";
        var mediaKey = $"media/{objId}";
        var docDisp = MediaConstraints.BuildContentDisposition("spec.pdf", isAttachment: true);

        // PromoteByCopyAsync checks ETagToMatch, CacheControl, ContentDisposition, REPLACE, and zero DeleteObject calls
        await storage.PromoteByCopyAsync(pendingKey, mediaKey, "application/pdf", docDisp, "\"etag-1\"");
        var copyReq = Assert.IsType<CopyObjectRequest>(calls.Single(c => c.Method == nameof(IAmazonS3.CopyObjectAsync)).Request);
        Assert.Equal("\"etag-1\"", copyReq.ETagToMatch);
        Assert.Equal(S3MetadataDirective.REPLACE, copyReq.MetadataDirective);
        Assert.Equal("private, no-store, no-cache", copyReq.Headers.CacheControl);
        Assert.Equal(docDisp, copyReq.Headers.ContentDisposition);
        Assert.DoesNotContain(calls, c => c.Method == nameof(IAmazonS3.DeleteObjectAsync));

        // PromoteFromStreamAsync sets AutoCloseStream = false, DisablePayloadSigning = true, IfNoneMatch = "*", keeps stream open, and zero DeleteObject calls
        await using var uploadStream = new MemoryStream(payloadBytes);
        await storage.PromoteFromStreamAsync(pendingKey, mediaKey, uploadStream, payloadBytes.Length, "image/png", null);
        var putReq = Assert.IsType<PutObjectRequest>(calls.Single(c => c.Method == nameof(IAmazonS3.PutObjectAsync)).Request);
        Assert.False(putReq.AutoCloseStream);
        Assert.True(putReq.DisablePayloadSigning);
        Assert.Equal("*", putReq.IfNoneMatch);
        Assert.Equal("private, no-store, no-cache", putReq.Headers.CacheControl);
        Assert.True(uploadStream.CanRead);
        Assert.DoesNotContain(calls, c => c.Method == nameof(IAmazonS3.DeleteObjectAsync));

        // 404 GetObjectMetadata -> null, 404 DeleteObject -> idempotent no-op, 416 ReadByteRange -> empty
        Assert.Null(await storage.GetObjectMetadataAsync(pendingKey));
        await storage.DeleteObjectAsync(pendingKey);
        Assert.Empty(await storage.ReadByteRangeAsync(pendingKey, 0, 10, "\"etag-1\""));

        // DownloadToStreamAsync enforces hard maxBytes ceiling
        using var smallDest = new MemoryStream();
        await Assert.ThrowsAsync<DomainException>(() =>
            storage.DownloadToStreamAsync(pendingKey, smallDest, maxBytes: 4));

        // Multi-page ListObjectsAsync handles continuation token + null S3Objects page
        var listed = new List<StoredObjectInfo>();
        await foreach (var item in storage.ListObjectsAsync("pending/"))
        {
            listed.Add(item);
        }

        Assert.Single(listed);
    }

    [Fact]
    public async Task CloudflareR2MediaStorage_TranslatesSdkExceptionsToApplicationExceptions()
    {
        var (s3Proxy, _) = RecordingAmazonS3Proxy.Create((methodName, _) => methodName switch
        {
            nameof(IAmazonS3.CopyObjectAsync) => Task.FromException<CopyObjectResponse>(
                new AmazonS3Exception("Precondition") { StatusCode = HttpStatusCode.PreconditionFailed }),
            nameof(IAmazonS3.PutObjectAsync) => Task.FromException<PutObjectResponse>(
                new AmazonS3Exception("Precondition") { StatusCode = HttpStatusCode.PreconditionFailed }),
            nameof(IAmazonS3.GetObjectMetadataAsync) => Task.FromException<GetObjectMetadataResponse>(
                new AmazonS3Exception("Forbidden") { StatusCode = HttpStatusCode.Forbidden }),
            nameof(IAmazonS3.GetObjectAsync) => Task.FromException<GetObjectResponse>(
                new AmazonS3Exception("NotFound") { StatusCode = HttpStatusCode.NotFound, ErrorCode = "NoSuchKey" }),
            _ => throw new NotSupportedException()
        });

        using var storage = new CloudflareR2MediaStorage(
            s3Proxy,
            new R2StorageOptions { BucketName = "aos-media" },
            NullLogger<CloudflareR2MediaStorage>.Instance);

        var objId = MediaConstraints.GenerateStorageObjectId();
        var pendingKey = $"pending/{objId}";
        var mediaKey = $"media/{objId}";

        await Assert.ThrowsAsync<ConflictException>(() =>
            storage.PromoteByCopyAsync(pendingKey, mediaKey, "image/png", null, "\"stale\""));

        await using var putStream = new MemoryStream("abc"u8.ToArray());
        await Assert.ThrowsAsync<ConflictException>(() =>
            storage.PromoteFromStreamAsync(pendingKey, mediaKey, putStream, 3, "image/png", null));

        var notFound = await Assert.ThrowsAsync<NotFoundException>(() =>
            storage.ReadByteRangeAsync(pendingKey, 0, 10));
        Assert.DoesNotContain(objId, notFound.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<MediaStorageException>(() =>
            storage.GetObjectMetadataAsync(pendingKey));
    }

    [Fact]
    public async Task CloudflareR2MediaStorage_ListObjectsAsync_UsesTimeProvider_WhenLastModifiedMissing()
    {
        var fixedNow = new DateTimeOffset(2026, 11, 24, 18, 0, 0, TimeSpan.Zero);
        var clock = new FixedTimeProvider(fixedNow);
        var (s3Proxy, _) = RecordingAmazonS3Proxy.Create((methodName, _) => methodName switch
        {
            nameof(IAmazonS3.ListObjectsV2Async) => Task.FromResult(new ListObjectsV2Response
            {
                IsTruncated = false,
                S3Objects = [new S3Object { Key = $"pending/{new string('b', 32)}", Size = 42, LastModified = null }]
            }),
            _ => throw new NotSupportedException()
        });

        using var storage = new CloudflareR2MediaStorage(
            s3Proxy,
            new R2StorageOptions { BucketName = "aos-media" },
            NullLogger<CloudflareR2MediaStorage>.Instance,
            ownsClient: false,
            timeProvider: clock);

        var items = new List<StoredObjectInfo>();
        await foreach (var item in storage.ListObjectsAsync("pending/"))
        {
            items.Add(item);
        }

        Assert.Single(items);
        Assert.Equal(fixedNow, items[0].LastModifiedUtc);
    }

    [Fact]
    public void MediaOptionsValidator_EnforcesPositiveBoundsAndQuotaRetentionWindow()
    {
        var validator = new MediaOptionsValidator();
        Assert.True(validator.Validate(null, new MediaOptions()).Succeeded);

        Assert.False(validator.Validate(null, new MediaOptions { PurgedMetadataRetentionHours = 23 }).Succeeded);
        Assert.False(validator.Validate(null, new MediaOptions { DailyUploadQuotaBytes = 0 }).Succeeded);
        Assert.False(validator.Validate(null, new MediaOptions { MaxConcurrentPendingPerUser = 0 }).Succeeded);
        Assert.False(validator.Validate(null, new MediaOptions { MaxConcurrentPendingPerUser = 11 }).Succeeded);
        Assert.False(validator.Validate(null, new MediaOptions { PresignedPutTtlMinutes = 0 }).Succeeded);
        Assert.False(validator.Validate(null, new MediaOptions { PresignedPutTtlMinutes = 6 }).Succeeded);
        Assert.False(validator.Validate(null, new MediaOptions { PresignedGetTtlMinutes = 16 }).Succeeded);
        Assert.False(validator.Validate(null, new MediaOptions { MaxImageProcessingConcurrency = 5 }).Succeeded);
        Assert.False(validator.Validate(null, new MediaOptions { MaxTotalDecodedImagePixels = 1000 }).Succeeded);
    }

    // =========================================================================
    // 3. LocalMediaSigningService & LocalDiskMediaStorage Tests
    // =========================================================================

    [Fact]
    public void LocalMediaSigningService_EnforcesAeadIntegrityExpiryAndActionIsolation()
    {
        var dp = DataProtectionProvider.Create(new DirectoryInfo(_tempRoot));
        var signer = new LocalMediaSigningService(dp);

        var objId = MediaConstraints.GenerateStorageObjectId();
        var now = new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);

        var putToken = signer.CreatePutToken($"pending/{objId}", "image/jpeg", 512, now.AddMinutes(5));
        var getToken = signer.CreateGetToken($"media/{objId}", "image/jpeg", "inline; filename=\"a.jpg\"", now.AddMinutes(10));

        Assert.True(signer.TryValidateToken(putToken, LocalMediaSigningService.PutAction, now.AddMinutes(1), out var putPayload));
        Assert.Equal(512, putPayload!.ContentLength);

        // Cross-action replay rejected
        Assert.False(signer.TryValidateToken(putToken, LocalMediaSigningService.GetAction, now.AddMinutes(1), out _));
        Assert.False(signer.TryValidateToken(getToken, LocalMediaSigningService.PutAction, now.AddMinutes(1), out _));

        // Expiry enforced
        Assert.False(signer.TryValidateToken(putToken, LocalMediaSigningService.PutAction, now.AddMinutes(6), out _));

        // Tampering rejected
        var tampered = putToken[..^2] + "XX";
        Assert.False(signer.TryValidateToken(tampered, LocalMediaSigningService.PutAction, now.AddMinutes(1), out _));
    }

    [Fact]
    public async Task LocalDiskMediaStorage_HandlesSmallRangeClamping_IfMatch_NonDeletingPromotion_AndTmpExclusion()
    {
        var dp = DataProtectionProvider.Create(new DirectoryInfo(_tempRoot));
        var signer = new LocalMediaSigningService(dp);
        var storageDir = Path.Combine(_tempRoot, "disk");
        var storage = new LocalDiskMediaStorage(signer, storageDir);

        var objId = MediaConstraints.GenerateStorageObjectId();
        var pendingKey = $"pending/{objId}";
        var mediaKey = $"media/{objId}";

        var stagingFile = Path.Combine(storageDir, "pending", objId);
        Directory.CreateDirectory(Path.GetDirectoryName(stagingFile)!);
        var tinyData = Encoding.UTF8.GetBytes("abc"); // 3 bytes (< 4 KB)
        await File.WriteAllBytesAsync(stagingFile, tinyData);

        // Orphan .tmp file inside pending/ must be ignored by ListObjectsAsync
        await File.WriteAllBytesAsync(stagingFile + ".1234.tmp", tinyData);

        var meta = await storage.GetObjectMetadataAsync(pendingKey);
        Assert.NotNull(meta);
        Assert.Equal(3, meta.ContentLength);
        Assert.NotNull(meta.ETag);

        // ReadByteRangeAsync clamps reads larger than file size cleanly
        var headerBytes = await storage.ReadByteRangeAsync(pendingKey, 0, 4096, meta.ETag);
        Assert.Equal(tinyData, headerBytes);
        Assert.Empty(await storage.ReadByteRangeAsync(pendingKey, 10, 4096));

        // Stale ETag rejected with ConflictException
        await Assert.ThrowsAsync<ConflictException>(() =>
            storage.ReadByteRangeAsync(pendingKey, 0, 10, "\"wrong-etag\""));

        // PromoteFromStreamAsync leaves staging intact and stream open, and enforces write-once immutability
        var reencoded = Encoding.UTF8.GetBytes("reencoded-clean-image");
        await using var stream = new MemoryStream(reencoded);
        await storage.PromoteFromStreamAsync(pendingKey, mediaKey, stream, reencoded.Length, "image/png", null);
        Assert.True(stream.CanRead);
        Assert.True(File.Exists(stagingFile)); // MUST NOT delete pending/ inside Promote

        // Second write to same permanent key is rejected with ConflictException without altering original object
        var overwriteBytes = Encoding.UTF8.GetBytes("tampered-v2");
        await using var overwriteStream = new MemoryStream(overwriteBytes);
        await Assert.ThrowsAsync<ConflictException>(() =>
            storage.PromoteFromStreamAsync(pendingKey, mediaKey, overwriteStream, overwriteBytes.Length, "image/png", null));
        await Assert.ThrowsAsync<ConflictException>(() =>
            storage.PromoteByCopyAsync(pendingKey, mediaKey, "image/png", null, meta.ETag));
        Assert.Equal(reencoded, await File.ReadAllBytesAsync(Path.Combine(storageDir, "media", objId)));

        // ListObjectsAsync excludes .tmp files
        var pendingListed = new List<StoredObjectInfo>();
        await foreach (var entry in storage.ListObjectsAsync("pending/"))
        {
            pendingListed.Add(entry);
        }

        Assert.Single(pendingListed);
        Assert.Equal(pendingKey, pendingListed[0].ObjectKey);

        // Path traversal / invalid key rejected
        await Assert.ThrowsAsync<DomainException>(() => storage.GetObjectMetadataAsync("pending/../escape"));
    }

    // =========================================================================
    // 4. DevMediaStartupFilter End-to-End HTTP Simulator & Prod Fail-Fast Tests
    // =========================================================================

    [Fact]
    public async Task DevMediaStartupFilter_SupportsUploadPromoteDownloadRangeAndHead_WithHardeningHeaders()
    {
        var simRoot = Path.Combine(_tempRoot, "dev-http-sim");
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Development);
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                        ["PublicBaseUrl"] = "https://localhost:7001",
                        ["Media:LocalStoragePath"] = simRoot
                    });
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var storage = factory.Services.GetRequiredService<IMediaStorage>();
        Assert.IsType<LocalDiskMediaStorage>(storage);

        var objId = MediaConstraints.GenerateStorageObjectId();
        var pendingKey = $"pending/{objId}";
        var mediaKey = $"media/{objId}";
        var now = DateTimeOffset.UtcNow;
        var payload = Encoding.UTF8.GetBytes("0123456789ABCDEF");

        var putUrl = storage.GeneratePresignedPutUrl(pendingKey, "application/pdf", payload.Length, TimeSpan.FromMinutes(5), now);

        // Wrong verb rejected (405)
        var wrongVerb = await client.PostAsync(putUrl, new ByteArrayContent(payload));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongVerb.StatusCode);

        // Missing Content-Length rejected (411 Length Required)
        using var noLengthReq = new HttpRequestMessage(HttpMethod.Put, putUrl)
        {
            Content = new NoLengthStreamContent(payload)
        };
        noLengthReq.Headers.TransferEncodingChunked = true;
        noLengthReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var noLengthRes = await client.SendAsync(noLengthReq);
        Assert.Equal(HttpStatusCode.LengthRequired, noLengthRes.StatusCode);

        // Wrong Content-Type rejected (400)
        var wrongMimeReq = new HttpRequestMessage(HttpMethod.Put, putUrl)
        {
            Content = new ByteArrayContent(payload)
        };
        wrongMimeReq.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        var wrongMimeRes = await client.SendAsync(wrongMimeReq);
        Assert.Equal(HttpStatusCode.BadRequest, wrongMimeRes.StatusCode);

        // Truncated/wrong length rejected (400) and creates no persisted file
        var shortReq = new HttpRequestMessage(HttpMethod.Put, putUrl)
        {
            Content = new ByteArrayContent(payload[..5])
        };
        shortReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var shortRes = await client.SendAsync(shortReq);
        Assert.Equal(HttpStatusCode.BadRequest, shortRes.StatusCode);
        Assert.Null(await storage.GetObjectMetadataAsync(pendingKey));

        // Valid PUT succeeds with ETag & hardening headers
        var validPutReq = new HttpRequestMessage(HttpMethod.Put, putUrl)
        {
            Content = new ByteArrayContent(payload)
        };
        validPutReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var validPutRes = await client.SendAsync(validPutReq);
        Assert.Equal(HttpStatusCode.OK, validPutRes.StatusCode);
        Assert.NotNull(validPutRes.Headers.ETag);

        var disp = MediaConstraints.BuildContentDisposition("contract.pdf", isAttachment: true);
        var meta = await storage.GetObjectMetadataAsync(pendingKey);
        Assert.NotNull(meta);
        await storage.PromoteByCopyAsync(pendingKey, mediaKey, "application/pdf", disp, meta.ETag);

        var getUrl = storage.GeneratePresignedGetUrl(mediaKey, "application/pdf", disp, TimeSpan.FromMinutes(10), now);

        // GET succeeds with hardening headers + Content-Disposition
        var getRes = await client.GetAsync(getUrl);
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        Assert.Equal("nosniff", getRes.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", getRes.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("default-src 'none'; sandbox", getRes.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("same-origin", getRes.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Contains("no-store", getRes.Headers.CacheControl!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(disp, getRes.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal(payload, await getRes.Content.ReadAsByteArrayAsync());

        // Range GET (206 Partial Content)
        var rangeReq = new HttpRequestMessage(HttpMethod.Get, getUrl);
        rangeReq.Headers.Range = new RangeHeaderValue(0, 3);
        var rangeRes = await client.SendAsync(rangeReq);
        Assert.Equal(HttpStatusCode.PartialContent, rangeRes.StatusCode);
        Assert.Equal("0123"u8.ToArray(), await rangeRes.Content.ReadAsByteArrayAsync());

        // HEAD succeeds with Content-Length and empty body
        var headRes = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, getUrl));
        Assert.Equal(HttpStatusCode.OK, headRes.StatusCode);
        Assert.Equal(payload.Length, headRes.Content.Headers.ContentLength);
        Assert.Empty(await headRes.Content.ReadAsByteArrayAsync());

        // Cross-action token rejected (PUT token sent to /download -> 403)
        var crossDownloadUrl = putUrl.Replace("/dev-media/upload", "/dev-media/download", StringComparison.Ordinal);
        var crossRes = await client.GetAsync(crossDownloadUrl);
        Assert.Equal(HttpStatusCode.Forbidden, crossRes.StatusCode);
    }

    [Fact]
    public async Task ProductionStartup_FailsFastWhenR2Missing_AndDisablesDevSimulatorWhenConfigured()
    {
        // Unconfigured Production fails fast via OptionsValidationException
        Assert.Throws<OptionsValidationException>(() =>
        {
            using var factory = new WebApplicationFactory<Program>()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseEnvironment(Environments.Production);
                    builder.ConfigureAppConfiguration((_, config) =>
                    {
                        config.AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                            ["PublicBaseUrl"] = "https://example.com"
                        });
                    });
                });

            _ = factory.Services;
        });

        // Configured Production resolves CloudflareR2MediaStorage and does NOT mount /dev-media/*
        using var prodFactory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Production);
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                        ["PublicBaseUrl"] = "https://example.com",
                        ["R2:ServiceUrl"] = "https://0123456789abcdef.r2.cloudflarestorage.com",
                        ["R2:BucketName"] = "aos-media-prod",
                        ["R2:AccessKeyId"] = "AKIATESTACCESSKEY123",
                        ["R2:SecretAccessKey"] = "SecretTestKeyValue0123456789abcdef"
                    });
                });
            });

        Assert.IsType<CloudflareR2MediaStorage>(prodFactory.Services.GetRequiredService<IMediaStorage>());

        var client = prodFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });
        var devDownloadResp = await client.GetAsync("/dev-media/download?token=invalid");
        var devUploadResp = await client.PutAsync("/dev-media/upload?token=invalid", new ByteArrayContent([1, 2, 3]));
        // With DevMediaStartupFilter disabled, neither /dev-media/* branch is mapped -> 404 or 405 (never 403/200)
        Assert.Equal(HttpStatusCode.NotFound, devDownloadResp.StatusCode);
        Assert.True(
            devUploadResp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"Expected 404 or 405 when DevMediaStartupFilter is disabled, got {(int)devUploadResp.StatusCode}.");
    }

    private sealed class NoLengthStreamContent : HttpContent
    {
        private readonly byte[] _bytes;

        public NoLengthStreamContent(byte[] bytes) => _bytes = bytes;

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(_bytes, 0, _bytes.Length);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public FakeHostEnvironment(string environmentName) => EnvironmentName = environmentName;

        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "AOS.Messenger.Web";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    internal class RecordingAmazonS3Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod!, args);

        public static (IAmazonS3 Proxy, List<(string Method, object? Request)> Calls) Create(
            Func<string, object?, object?> handler)
        {
            var calls = new List<(string Method, object? Request)>();
            var proxy = Create<IAmazonS3, RecordingAmazonS3Proxy>();
            ((RecordingAmazonS3Proxy)(object)proxy).Handler = (method, args) =>
            {
                var req = args is { Length: > 0 } ? args[0] : null;
                calls.Add((method.Name, req));
                return handler(method.Name, req);
            };
            return (proxy, calls);
        }
    }
}
