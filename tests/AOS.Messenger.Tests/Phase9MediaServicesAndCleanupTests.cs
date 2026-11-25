using System.Runtime.CompilerServices;
using System.Text;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Infrastructure.Storage;
using ImageMagick;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase9MediaServicesAndCleanupTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public Phase9MediaServicesAndCleanupTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    // =========================================================================
    // Test Helpers
    // =========================================================================

    private static async Task<ApplicationUser> SeedUserAsync(
        ApplicationDbContext db,
        string prefix,
        string displayName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userName = $"{prefix}_{suffix}";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = $"{userName}@example.com",
            NormalizedEmail = $"{userName.ToUpperInvariant()}@EXAMPLE.COM",
            DisplayName = displayName
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Conversation> SeedConversationAsync(
        ApplicationDbContext db,
        UserId userA,
        UserId userB,
        DateTimeOffset now)
    {
        var conv = Conversation.CreateDirect(userA, userB, now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv;
    }

    private static byte[] CreateMinimalRotatedExifJpegBytes(out int expectedOrientedWidth, out int expectedOrientedHeight)
    {
        // Raw pixel buffer is 40x20 with Orientation RightTop (6), so after AutoOrient() dimensions flip to 20x40.
        using var image = new MagickImage(new MagickColor(100, 149, 237), 40, 20);
        image.Format = MagickFormat.Jpeg;
        image.Orientation = Orientation.RightTop;

        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.Make, "TestCamera");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        image.SetProfile(exif);

        expectedOrientedWidth = 20;
        expectedOrientedHeight = 40;
        return image.ToByteArray();
    }

    private static byte[] CreateMinimalPngBytes(uint width = 12, uint height = 8)
    {
        using var image = new MagickImage(new MagickColor(10, 200, 100), width, height);
        image.Format = MagickFormat.Png;
        return image.ToByteArray();
    }

    private static byte[] CreateMinimalPdfBytes()
    {
        return Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");
    }

    private static string ResolveDiskPath(string mediaRoot, string objectKey)
    {
        return Path.Combine(mediaRoot, objectKey.Replace('/', Path.DirectorySeparatorChar));
    }

    private static async Task WriteStorageObjectAsync(
        string mediaRoot,
        string objectKey,
        byte[] content,
        DateTimeOffset? lastWriteUtc = null)
    {
        var path = ResolveDiskPath(mediaRoot, objectKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content);
        if (lastWriteUtc.HasValue)
        {
            File.SetLastWriteTimeUtc(path, lastWriteUtc.Value.UtcDateTime);
        }
    }

    private IServiceProvider BuildProvider(
        TestTimeProvider? timeProvider = null,
        Action<MediaOptions>? configureMedia = null,
        IRealtimeNotifier? realtimeNotifier = null,
        string? customMediaRoot = null,
        IMediaStorage? customMediaStorage = null)
    {
        var services = _fixture.CreateServiceCollection();

        if (timeProvider is not null)
        {
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(timeProvider));
        }

        if (configureMedia is not null)
        {
            services.Configure(configureMedia);
        }

        if (realtimeNotifier is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(realtimeNotifier));
        }

        if (customMediaStorage is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(customMediaStorage));
        }
        else if (customMediaRoot is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(sp =>
                new LocalDiskMediaStorage(sp.GetRequiredService<LocalMediaSigningService>(), customMediaRoot)));
            services.Replace(ServiceDescriptor.Singleton<IMediaStorage>(sp =>
                sp.GetRequiredService<LocalDiskMediaStorage>()));
        }

        return services.BuildServiceProvider();
    }

    private sealed class RecordingRealtimeNotifier : IRealtimeNotifier
    {
        public List<MessageDto> NewMessages { get; } = [];
        public List<MessageEditedDto> EditedMessages { get; } = [];
        public List<MessageDeletedForMeDto> DeletedForMe { get; } = [];
        public List<MessageDeletedForEveryoneDto> DeletedForEveryone { get; } = [];

        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default)
        {
            NewMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;

        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default)
        {
            EditedMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default)
        {
            DeletedForEveryone.Add(message);
            return Task.CompletedTask;
        }

        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default)
        {
            DeletedForMe.Add(message);
            return Task.CompletedTask;
        }

        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class InterceptingMediaStorage : IMediaStorage
    {
        private readonly IMediaStorage _inner;
        private readonly Func<string, Task<bool>>? _beforeDelete;
        private readonly Func<string, Task<MediaObjectMetadata?>>? _onHead;
        private readonly Func<Task>? _onPromoteOrVerify;

        public InterceptingMediaStorage(
            IMediaStorage inner,
            Func<string, Task<bool>>? beforeDelete = null,
            Func<string, Task<MediaObjectMetadata?>>? onHead = null,
            Func<Task>? onPromoteOrVerify = null)
        {
            _inner = inner;
            _beforeDelete = beforeDelete;
            _onHead = onHead;
            _onPromoteOrVerify = onPromoteOrVerify;
        }

        public string GeneratePresignedPutUrl(string objectKey, string contentType, long contentLength, TimeSpan expiresIn, DateTimeOffset nowUtc)
            => _inner.GeneratePresignedPutUrl(objectKey, contentType, contentLength, expiresIn, nowUtc);

        public string GeneratePresignedGetUrl(string objectKey, string responseContentType, string responseContentDisposition, TimeSpan expiresIn, DateTimeOffset nowUtc)
            => _inner.GeneratePresignedGetUrl(objectKey, responseContentType, responseContentDisposition, expiresIn, nowUtc);

        public Task<MediaObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct = default)
            => _onHead is not null ? _onHead(objectKey) : _inner.GetObjectMetadataAsync(objectKey, ct);

        public async Task<byte[]> ReadByteRangeAsync(string objectKey, long offset, int length, string? ifMatchETag = null, CancellationToken ct = default)
        {
            if (_onPromoteOrVerify is not null)
            {
                await _onPromoteOrVerify();
            }

            return await _inner.ReadByteRangeAsync(objectKey, offset, length, ifMatchETag, ct);
        }

        public Task DownloadToStreamAsync(string objectKey, Stream destination, long maxBytes, string? ifMatchETag = null, CancellationToken ct = default)
            => _inner.DownloadToStreamAsync(objectKey, destination, maxBytes, ifMatchETag, ct);

        public Task PromoteByCopyAsync(string stagingKey, string permanentKey, string verifiedContentType, string? contentDisposition, string? ifMatchETag = null, CancellationToken ct = default)
            => _inner.PromoteByCopyAsync(stagingKey, permanentKey, verifiedContentType, contentDisposition, ifMatchETag, ct);

        public Task PromoteFromStreamAsync(string stagingKey, string permanentKey, Stream content, long contentLength, string verifiedContentType, string? contentDisposition, CancellationToken ct = default)
            => _inner.PromoteFromStreamAsync(stagingKey, permanentKey, content, contentLength, verifiedContentType, contentDisposition, ct);

        public async Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
        {
            if (_beforeDelete is not null && !await _beforeDelete(objectKey))
            {
                return;
            }

            await _inner.DeleteObjectAsync(objectKey, ct);
        }

        public IAsyncEnumerable<StoredObjectInfo> ListObjectsAsync(string prefix, CancellationToken ct = default)
            => _inner.ListObjectsAsync(prefix, ct);
    }

    // =========================================================================
    // 1. MediaService.InitiateUploadAsync Tests
    // =========================================================================

    [Fact]
    public async Task InitiateUploadAsync_ValidatesAllowlistClipboardFallbackAndQuotas()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero));
        await using var provider = (ServiceProvider)BuildProvider(
            timeProvider: clock,
            configureMedia: o =>
            {
                o.MaxConcurrentPendingPerUser = 2;
                o.DailyUploadQuotaBytes = 5000;
            });

        UserId aliceId;
        UserId bobId;
        UserId outsiderId;
        ConversationId convId;

        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var alice = await SeedUserAsync(db, "init_a", "Alice");
            var bob = await SeedUserAsync(db, "init_b", "Bob");
            var outsider = await SeedUserAsync(db, "init_o", "Outsider");
            aliceId = new UserId(alice.Id);
            bobId = new UserId(bob.Id);
            outsiderId = new UserId(outsider.Id);
            var conv = await SeedConversationAsync(db, aliceId, bobId, clock.GetUtcNow());
            convId = conv.Id;
        }

        await using var scope = provider.CreateAsyncScope();
        var mediaService = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var dbRead = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // 1. Clipboard filename ("image" with no extension) falls back via MIME -> ".png"
        var clipboardResp = await mediaService.InitiateUploadAsync(
            aliceId,
            convId,
            new InitiateMediaUploadRequestDto("image", "image/png", 2000));

        Assert.Equal("image/png", clipboardResp.RequiredContentType);
        Assert.Equal("image.png", clipboardResp.SanitizedFileName);
        Assert.Equal("image", clipboardResp.Kind);
        Assert.Equal(clock.GetUtcNow().AddMinutes(5), clipboardResp.ExpiresAtUtc);
        Assert.StartsWith("/dev-media/upload?token=", clipboardResp.UploadUrl, StringComparison.Ordinal);

        // Pending row persisted and no staging object written yet
        var pendingEntity = await dbRead.Media.AsNoTracking().SingleAsync(m => m.Id == clipboardResp.MediaId);
        Assert.Equal(MediaState.Pending, pendingEntity.State);
        Assert.StartsWith(MediaConstraints.PendingKeyPrefix, pendingEntity.ObjectKey, StringComparison.Ordinal);
        Assert.False(File.Exists(ResolveDiskPath(_fixture.MediaRootDirectory, pendingEntity.ObjectKey)));

        // 2. Empty/octet-stream MIME falls back cleanly from allowlisted extension
        var pdfResp = await mediaService.InitiateUploadAsync(
            aliceId,
            convId,
            new InitiateMediaUploadRequestDto("Quarterly Report.PDF", "application/octet-stream", 1500));

        Assert.Equal("application/pdf", pdfResp.RequiredContentType);
        Assert.Equal("document", pdfResp.Kind);
        Assert.Equal("Quarterly Report.pdf", pdfResp.SanitizedFileName);

        // 3. Concurrent pending limit reached (2) -> DomainException
        await Assert.ThrowsAsync<DomainException>(() =>
            mediaService.InitiateUploadAsync(
                aliceId,
                convId,
                new InitiateMediaUploadRequestDto("third.png", "image/png", 500)));

        // 4. Outsider -> ForbiddenException
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            mediaService.InitiateUploadAsync(
                outsiderId,
                convId,
                new InitiateMediaUploadRequestDto("x.png", "image/png", 500)));

        // 5. Blocked conversation -> ForbiddenException (check Bob's quota also untouched)
        var blockSvc = scope.ServiceProvider.GetRequiredService<IBlockService>();
        await blockSvc.BlockUserAsync(aliceId, bobId);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            mediaService.InitiateUploadAsync(
                bobId,
                convId,
                new InitiateMediaUploadRequestDto("x.png", "image/png", 500)));
        await blockSvc.UnblockUserAsync(aliceId, bobId);

        // 6. Disallowed / 0-byte / Voice rejections -> DomainException
        await Assert.ThrowsAsync<DomainException>(() =>
            mediaService.InitiateUploadAsync(bobId, convId, new InitiateMediaUploadRequestDto("script.svg", "image/svg+xml", 100)));
        await Assert.ThrowsAsync<DomainException>(() =>
            mediaService.InitiateUploadAsync(bobId, convId, new InitiateMediaUploadRequestDto("empty.png", "image/png", 0)));
        await Assert.ThrowsAsync<DomainException>(() =>
            mediaService.InitiateUploadAsync(bobId, convId, new InitiateMediaUploadRequestDto("voice.ogg", "audio/ogg", 1000)));

        // 7. Daily quota exceeded (> 5000 on Bob's upload of 5001) -> DomainException
        await Assert.ThrowsAsync<DomainException>(() =>
            mediaService.InitiateUploadAsync(bobId, convId, new InitiateMediaUploadRequestDto("huge.png", "image/png", 5001)));
    }

    // =========================================================================
    // 2. MediaService.CompleteUploadAsync Tests
    // =========================================================================

    [Fact]
    public async Task CompleteUploadAsync_VerifiesStripsExifAutoOrientsPromotesAndDeletesStagingIdempotently()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 13, 0, 0, TimeSpan.Zero));
        await using var provider = (ServiceProvider)BuildProvider(timeProvider: clock);

        UserId aliceId;
        UserId bobId;
        ConversationId convId;

        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var alice = await SeedUserAsync(db, "comp_a", "Alice");
            var bob = await SeedUserAsync(db, "comp_b", "Bob");
            aliceId = new UserId(alice.Id);
            bobId = new UserId(bob.Id);
            convId = (await SeedConversationAsync(db, aliceId, bobId, clock.GetUtcNow())).Id;
        }

        var jpegBytes = CreateMinimalRotatedExifJpegBytes(out var expectedW, out var expectedH);

        await using var scope = provider.CreateAsyncScope();
        var mediaService = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var dbCtx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var init = await mediaService.InitiateUploadAsync(
            aliceId,
            convId,
            new InitiateMediaUploadRequestDto("portrait.jpg", "image/jpeg", jpegBytes.Length));

        var pendingRow = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == init.MediaId);
        await WriteStorageObjectAsync(_fixture.MediaRootDirectory, pendingRow.ObjectKey, jpegBytes);

        // Non-uploader participant cannot complete Alice's upload
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            mediaService.CompleteUploadAsync(bobId, convId, init.MediaId));

        // Complete upload
        var completed = await mediaService.CompleteUploadAsync(aliceId, convId, init.MediaId);

        Assert.Equal(init.MediaId, completed.Id);
        Assert.Equal("image", completed.Kind);
        Assert.Equal("image/jpeg", completed.ContentType);
        Assert.Equal(expectedW, completed.Width);
        Assert.Equal(expectedH, completed.Height);
        Assert.InRange(completed.SizeBytes, 1, MediaConstraints.MaxImageBytes);

        // Staging deleted strictly post-SQL commit, permanent media/ exists, EXIF stripped
        var uploadedRow = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == init.MediaId);
        Assert.Equal(MediaState.Uploaded, uploadedRow.State);
        Assert.StartsWith(MediaConstraints.MediaKeyPrefix, uploadedRow.ObjectKey, StringComparison.Ordinal);
        Assert.False(File.Exists(ResolveDiskPath(_fixture.MediaRootDirectory, pendingRow.ObjectKey)));
        var permanentPath = ResolveDiskPath(_fixture.MediaRootDirectory, uploadedRow.ObjectKey);
        Assert.True(File.Exists(permanentPath));

        using (var promotedImg = new MagickImage(permanentPath))
        {
            Assert.Null(promotedImg.GetExifProfile());
            Assert.Equal((uint)expectedW, promotedImg.Width);
            Assert.Equal((uint)expectedH, promotedImg.Height);
        }

        // Idempotent retry on Uploaded succeeds cleanly
        var retryDto = await mediaService.CompleteUploadAsync(aliceId, convId, init.MediaId);
        Assert.Equal(completed, retryDto);
    }

    [Fact]
    public async Task CompleteUploadAsync_HandlesMissingStagingCorruptPayloadAndMidUploadBlock()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 14, 0, 0, TimeSpan.Zero));
        await using var provider = (ServiceProvider)BuildProvider(timeProvider: clock);

        UserId aliceId;
        UserId bobId;
        ConversationId convId;

        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var alice = await SeedUserAsync(db, "fail_a", "Alice");
            var bob = await SeedUserAsync(db, "fail_b", "Bob");
            aliceId = new UserId(alice.Id);
            bobId = new UserId(bob.Id);
            convId = (await SeedConversationAsync(db, aliceId, bobId, clock.GetUtcNow())).Id;
        }

        await using var scope = provider.CreateAsyncScope();
        var mediaService = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var blockService = scope.ServiceProvider.GetRequiredService<IBlockService>();
        var dbCtx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        // 1. Missing staging object (404 on HeadObject) -> marks Failed & Purged, throws DomainException
        var missingInit = await mediaService.InitiateUploadAsync(
            aliceId,
            convId,
            new InitiateMediaUploadRequestDto("never-uploaded.png", "image/png", 128));
        await Assert.ThrowsAsync<DomainException>(() =>
            mediaService.CompleteUploadAsync(aliceId, convId, missingInit.MediaId));

        var missingRow = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == missingInit.MediaId);
        Assert.Equal(MediaState.Failed, missingRow.State);
        Assert.NotNull(missingRow.PurgedAtUtc);

        // 2. Disguised SVG bytes uploaded under .png -> verification fails, marks Failed + VerifiedSizeBytes, deletes pending/
        var badPayload = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
        var badInit = await mediaService.InitiateUploadAsync(
            aliceId,
            convId,
            new InitiateMediaUploadRequestDto("bad.png", "image/png", badPayload.Length));
        var badPending = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == badInit.MediaId);
        await WriteStorageObjectAsync(_fixture.MediaRootDirectory, badPending.ObjectKey, badPayload);

        await Assert.ThrowsAsync<DomainException>(() =>
            mediaService.CompleteUploadAsync(aliceId, convId, badInit.MediaId));

        var badRow = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == badInit.MediaId);
        Assert.Equal(MediaState.Failed, badRow.State);
        Assert.Equal(badPayload.Length, badRow.VerifiedSizeBytes);
        Assert.NotNull(badRow.PurgedAtUtc);
        Assert.False(File.Exists(ResolveDiskPath(_fixture.MediaRootDirectory, badPending.ObjectKey)));

        // 3. Blocked mid-upload -> records staged size for quota, deletes pending/ & throws ForbiddenException
        var pngBytes = CreateMinimalPngBytes();
        var blockedInit = await mediaService.InitiateUploadAsync(
            aliceId,
            convId,
            new InitiateMediaUploadRequestDto("blocked.png", "image/png", pngBytes.Length));
        var blockedPending = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == blockedInit.MediaId);
        await WriteStorageObjectAsync(_fixture.MediaRootDirectory, blockedPending.ObjectKey, pngBytes);

        await blockService.BlockUserAsync(bobId, aliceId);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            mediaService.CompleteUploadAsync(aliceId, convId, blockedInit.MediaId));

        Assert.False(File.Exists(ResolveDiskPath(_fixture.MediaRootDirectory, blockedPending.ObjectKey)));
        var blockedFailedRow = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == blockedInit.MediaId);
        Assert.Equal(MediaState.Failed, blockedFailedRow.State);
        Assert.Equal(pngBytes.Length, blockedFailedRow.VerifiedSizeBytes);
        Assert.NotNull(blockedFailedRow.PurgedAtUtc);
    }

    [Fact]
    public async Task BlockUnblockCycle_DoesNotRefundUploadedStagingBytesAgainstDailyQuota()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 14, 15, 0, TimeSpan.Zero));
        await using var provider = (ServiceProvider)BuildProvider(
            timeProvider: clock,
            configureMedia: o =>
            {
                o.DailyUploadQuotaBytes = 2000;
                o.MaxConcurrentPendingPerUser = 5;
            });

        UserId aliceId;
        UserId bobId;
        ConversationId convId;

        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            aliceId = new UserId((await SeedUserAsync(db, "qblk_a", "Alice")).Id);
            bobId = new UserId((await SeedUserAsync(db, "qblk_b", "Bob")).Id);
            convId = (await SeedConversationAsync(db, aliceId, bobId, clock.GetUtcNow())).Id;
        }

        await using var scope = provider.CreateAsyncScope();
        var mediaService = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var blockService = scope.ServiceProvider.GetRequiredService<IBlockService>();
        var dbCtx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var payload = new byte[1500];
        var init = await mediaService.InitiateUploadAsync(
            aliceId,
            convId,
            new InitiateMediaUploadRequestDto("quota-consume.pdf", "application/pdf", payload.Length));
        var row = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == init.MediaId);
        await WriteStorageObjectAsync(_fixture.MediaRootDirectory, row.ObjectKey, payload);

        // Alice blocks Bob, triggers mid-upload failure cleanup, then unblocks Bob
        await blockService.BlockUserAsync(aliceId, bobId);
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            mediaService.CompleteUploadAsync(aliceId, convId, init.MediaId));
        await blockService.UnblockUserAsync(aliceId, bobId);

        // Remaining daily quota = 2000 - 1500 = 500 bytes; attempting 600 bytes must still fail
        await Assert.ThrowsAsync<DomainException>(() =>
            mediaService.InitiateUploadAsync(
                aliceId,
                convId,
                new InitiateMediaUploadRequestDto("second.pdf", "application/pdf", 600)));
    }

    [Fact]
    public async Task CompleteUploadAsync_TransientVerifierConflict_PreservesMessageAndStagingObject()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 14, 20, 0, TimeSpan.Zero));
        await using var baseProvider = (ServiceProvider)BuildProvider(timeProvider: clock);

        UserId aliceId;
        ConversationId convId;
        MediaId mediaId;
        string pendingKey;
        var png = CreateMinimalPngBytes(12, 12);

        await using (var setupScope = baseProvider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            aliceId = new UserId((await SeedUserAsync(db, "conf_v_a", "Alice")).Id);
            var bobId = new UserId((await SeedUserAsync(db, "conf_v_b", "Bob")).Id);
            convId = (await SeedConversationAsync(db, aliceId, bobId, clock.GetUtcNow())).Id;

            var mediaSvc = setupScope.ServiceProvider.GetRequiredService<IMediaService>();
            var init = await mediaSvc.InitiateUploadAsync(
                aliceId,
                convId,
                new InitiateMediaUploadRequestDto("busy.png", "image/png", png.Length));
            mediaId = init.MediaId;
            pendingKey = (await db.Media.AsNoTracking().SingleAsync(m => m.Id == mediaId)).ObjectKey;
            await WriteStorageObjectAsync(_fixture.MediaRootDirectory, pendingKey, png);
        }

        var customServices = _fixture.CreateServiceCollection();
        customServices.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        customServices.Replace(ServiceDescriptor.Singleton<IMediaVerifier>(
            new ThrowingConflictMediaVerifier("Image processing capacity is currently busy. Please try again.")));

        await using var busyProvider = customServices.BuildServiceProvider();
        await using var testScope = busyProvider.CreateAsyncScope();
        var busyMediaSvc = testScope.ServiceProvider.GetRequiredService<IMediaService>();
        var dbVerify = testScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            busyMediaSvc.CompleteUploadAsync(aliceId, convId, mediaId));
        Assert.Equal("Image processing capacity is currently busy. Please try again.", ex.Message);

        var rowAfter = await dbVerify.Media.AsNoTracking().SingleAsync(m => m.Id == mediaId);
        Assert.Equal(MediaState.Pending, rowAfter.State);
        Assert.True(File.Exists(ResolveDiskPath(_fixture.MediaRootDirectory, pendingKey)));
    }

    [Fact]
    public async Task CompleteUploadAsync_Concurrent404OnHeadObject_ReloadsUploadedIdempotently()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 14, 30, 0, TimeSpan.Zero));
        await using var baseProvider = (ServiceProvider)BuildProvider(timeProvider: clock);

        UserId aliceId;
        ConversationId convId;
        MediaId mediaId;
        var pngBytes = CreateMinimalPngBytes(16, 16);

        await using (var setupScope = baseProvider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var alice = await SeedUserAsync(db, "race_a", "Alice");
            var bob = await SeedUserAsync(db, "race_b", "Bob");
            aliceId = new UserId(alice.Id);
            convId = (await SeedConversationAsync(db, aliceId, new UserId(bob.Id), clock.GetUtcNow())).Id;

            var mediaSvc = setupScope.ServiceProvider.GetRequiredService<IMediaService>();
            var init = await mediaSvc.InitiateUploadAsync(
                aliceId,
                convId,
                new InitiateMediaUploadRequestDto("race.png", "image/png", pngBytes.Length));
            mediaId = init.MediaId;
        }

        // Simulate Request A having promoted, committed State = Uploaded, and deleted pending/
        // RIGHT after Request B initially loaded media as State = Pending and before Request B calls HeadObject.
        var realStorage = baseProvider.GetRequiredService<LocalDiskMediaStorage>();
        var racingStorage = new InterceptingMediaStorage(
            realStorage,
            onHead: async objectKey =>
            {
                await using var concurrentScope = baseProvider.CreateAsyncScope();
                var concurrentDb = concurrentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var row = await concurrentDb.Media.SingleAsync(m => m.Id == mediaId);
                if (row.State == MediaState.Pending)
                {
                    row.MarkUploaded(
                        $"{MediaConstraints.MediaKeyPrefix}{row.StorageObjectId}",
                        "image/png",
                        pngBytes.Length,
                        16,
                        16,
                        null,
                        clock.GetUtcNow());
                    await concurrentDb.SaveChangesAsync();
                }

                // Returning null mimics Request A deleting pending/ before Request B's HeadObject runs
                return null;
            });

        await using var racingProvider = (ServiceProvider)BuildProvider(
            timeProvider: clock,
            customMediaStorage: racingStorage);

        await using var scopeB = racingProvider.CreateAsyncScope();
        var mediaSvcB = scopeB.ServiceProvider.GetRequiredService<IMediaService>();

        var result = await mediaSvcB.CompleteUploadAsync(aliceId, convId, mediaId);
        Assert.Equal(mediaId, result.Id);
        Assert.Equal(16, result.Width);
        Assert.Equal(16, result.Height);
    }

    // =========================================================================
    // 3. CancelUploadAsync & GetDownloadUrlAsync Tests
    // =========================================================================

    [Fact]
    public async Task CancelUploadAsync_MarksDeletedLeavesPurgedNullOnStorageErrorAndRejectsAttached()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 15, 0, 0, TimeSpan.Zero));
        await using var baseProvider = (ServiceProvider)BuildProvider(timeProvider: clock);

        UserId aliceId;
        UserId bobId;
        ConversationId convId;

        await using (var setupScope = baseProvider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var alice = await SeedUserAsync(db, "canc_a", "Alice");
            var bob = await SeedUserAsync(db, "canc_b", "Bob");
            aliceId = new UserId(alice.Id);
            bobId = new UserId(bob.Id);
            convId = (await SeedConversationAsync(db, aliceId, bobId, clock.GetUtcNow())).Id;
        }

        // 1. Happy cancel on Uploaded
        await using (var scope = baseProvider.CreateAsyncScope())
        {
            var mediaSvc = scope.ServiceProvider.GetRequiredService<IMediaService>();
            var dbCtx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var png = CreateMinimalPngBytes();

            var init = await mediaSvc.InitiateUploadAsync(aliceId, convId, new InitiateMediaUploadRequestDto("c1.png", "image/png", png.Length));
            var row = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == init.MediaId);
            await WriteStorageObjectAsync(_fixture.MediaRootDirectory, row.ObjectKey, png);
            await mediaSvc.CompleteUploadAsync(aliceId, convId, init.MediaId);

            await mediaSvc.CancelUploadAsync(aliceId, convId, init.MediaId);

            var deletedRow = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == init.MediaId);
            Assert.Equal(MediaState.Deleted, deletedRow.State);
            Assert.NotNull(deletedRow.PurgedAtUtc);
            Assert.False(File.Exists(ResolveDiskPath(_fixture.MediaRootDirectory, deletedRow.ObjectKey)));
        }

        // 2. Transient storage delete failure leaves PurgedAtUtc == null for cleanup retry
        var realStorage = baseProvider.GetRequiredService<LocalDiskMediaStorage>();
        var failingDeleteStorage = new InterceptingMediaStorage(
            realStorage,
            beforeDelete: objectKey =>
                objectKey.StartsWith(MediaConstraints.MediaKeyPrefix, StringComparison.Ordinal)
                    ? throw new MediaStorageException("Transient R2 delete timeout.")
                    : Task.FromResult(true));

        await using var failingProvider = (ServiceProvider)BuildProvider(
            timeProvider: clock,
            customMediaStorage: failingDeleteStorage);

        await using (var scope2 = failingProvider.CreateAsyncScope())
        {
            var mediaSvc = scope2.ServiceProvider.GetRequiredService<IMediaService>();
            var dbCtx = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var png = CreateMinimalPngBytes();

            var init2 = await mediaSvc.InitiateUploadAsync(aliceId, convId, new InitiateMediaUploadRequestDto("c2.png", "image/png", png.Length));
            var row2 = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == init2.MediaId);
            await WriteStorageObjectAsync(_fixture.MediaRootDirectory, row2.ObjectKey, png);
            await mediaSvc.CompleteUploadAsync(aliceId, convId, init2.MediaId);

            // Should NOT throw; should commit Deleted with PurgedAtUtc == null
            await mediaSvc.CancelUploadAsync(aliceId, convId, init2.MediaId);

            var deletedUnpurged = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == init2.MediaId);
            Assert.Equal(MediaState.Deleted, deletedUnpurged.State);
            Assert.Null(deletedUnpurged.PurgedAtUtc);
            Assert.True(File.Exists(ResolveDiskPath(_fixture.MediaRootDirectory, deletedUnpurged.ObjectKey)));
        }

        // 3. Cancelling Attached media -> ConflictException
        await using (var scope3 = baseProvider.CreateAsyncScope())
        {
            var mediaSvc = scope3.ServiceProvider.GetRequiredService<IMediaService>();
            var msgSvc = scope3.ServiceProvider.GetRequiredService<IMessageService>();
            var dbCtx = scope3.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var png = CreateMinimalPngBytes();

            var init3 = await mediaSvc.InitiateUploadAsync(aliceId, convId, new InitiateMediaUploadRequestDto("c3.png", "image/png", png.Length));
            var row3 = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == init3.MediaId);
            await WriteStorageObjectAsync(_fixture.MediaRootDirectory, row3.ObjectKey, png);
            await mediaSvc.CompleteUploadAsync(aliceId, convId, init3.MediaId);

            await msgSvc.SendMessageAsync(
                aliceId,
                convId,
                new SendMessageRequestDto(null, $"attach-{Guid.NewGuid():N}", MediaIds: [init3.MediaId]));

            await Assert.ThrowsAsync<ConflictException>(() =>
                mediaSvc.CancelUploadAsync(aliceId, convId, init3.MediaId));
        }
    }

    [Fact]
    public async Task GetDownloadUrlAsync_EnforcesVisibilityDocumentForceAttachmentAndPreservedBlockHistory()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 16, 0, 0, TimeSpan.Zero));
        await using var provider = (ServiceProvider)BuildProvider(timeProvider: clock);

        UserId aliceId;
        UserId bobId;
        UserId outsiderId;
        ConversationId convId;

        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            aliceId = new UserId((await SeedUserAsync(db, "dl_a", "Alice")).Id);
            bobId = new UserId((await SeedUserAsync(db, "dl_b", "Bob")).Id);
            outsiderId = new UserId((await SeedUserAsync(db, "dl_o", "Outsider")).Id);
            convId = (await SeedConversationAsync(db, aliceId, bobId, clock.GetUtcNow())).Id;
        }

        await using var scope = provider.CreateAsyncScope();
        var mediaSvc = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var msgSvc = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var convSvc = scope.ServiceProvider.GetRequiredService<IConversationService>();
        var blockSvc = scope.ServiceProvider.GetRequiredService<IBlockService>();
        var signer = scope.ServiceProvider.GetRequiredService<LocalMediaSigningService>();
        var dbCtx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var png = CreateMinimalPngBytes();
        var pdf = CreateMinimalPdfBytes();

        var imgInit = await mediaSvc.InitiateUploadAsync(aliceId, convId, new InitiateMediaUploadRequestDto("pic.png", "image/png", png.Length));
        var imgPending = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == imgInit.MediaId);
        await WriteStorageObjectAsync(_fixture.MediaRootDirectory, imgPending.ObjectKey, png);
        await mediaSvc.CompleteUploadAsync(aliceId, convId, imgInit.MediaId);

        var pdfInit = await mediaSvc.InitiateUploadAsync(aliceId, convId, new InitiateMediaUploadRequestDto("contract.pdf", "application/pdf", pdf.Length));
        var pdfPending = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == pdfInit.MediaId);
        await WriteStorageObjectAsync(_fixture.MediaRootDirectory, pdfPending.ObjectKey, pdf);
        await mediaSvc.CompleteUploadAsync(aliceId, convId, pdfInit.MediaId);

        var unattachedInit = await mediaSvc.InitiateUploadAsync(aliceId, convId, new InitiateMediaUploadRequestDto("unattached.png", "image/png", png.Length));
        var unattachedPending = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == unattachedInit.MediaId);
        await WriteStorageObjectAsync(_fixture.MediaRootDirectory, unattachedPending.ObjectKey, png);
        await mediaSvc.CompleteUploadAsync(aliceId, convId, unattachedInit.MediaId);

        var sent = await msgSvc.SendMessageAsync(
            aliceId,
            convId,
            new SendMessageRequestDto("Files", $"dl-{Guid.NewGuid():N}", MediaIds: [imgInit.MediaId, pdfInit.MediaId]));

        // Image inline vs forceDownload=true; PDF always attachment
        var inlineImg = await mediaSvc.GetDownloadUrlAsync(bobId, convId, sent.Id, imgInit.MediaId, forceDownload: false);
        var inlineToken = Uri.UnescapeDataString(inlineImg.Url.Split("token=")[1]);
        Assert.True(signer.TryValidateToken(inlineToken, LocalMediaSigningService.GetAction, clock.GetUtcNow(), out var imgPayload));
        Assert.StartsWith("inline;", imgPayload!.ContentDisposition!, StringComparison.Ordinal);

        var forcedImg = await mediaSvc.GetDownloadUrlAsync(bobId, convId, sent.Id, imgInit.MediaId, forceDownload: true);
        var forcedToken = Uri.UnescapeDataString(forcedImg.Url.Split("token=")[1]);
        Assert.True(signer.TryValidateToken(forcedToken, LocalMediaSigningService.GetAction, clock.GetUtcNow(), out var forcedPayload));
        Assert.StartsWith("attachment;", forcedPayload!.ContentDisposition!, StringComparison.Ordinal);

        var pdfDownload = await mediaSvc.GetDownloadUrlAsync(bobId, convId, sent.Id, pdfInit.MediaId, forceDownload: false);
        var pdfToken = Uri.UnescapeDataString(pdfDownload.Url.Split("token=")[1]);
        Assert.True(signer.TryValidateToken(pdfToken, LocalMediaSigningService.GetAction, clock.GetUtcNow(), out var pdfPayload));
        Assert.StartsWith("attachment;", pdfPayload!.ContentDisposition!, StringComparison.Ordinal);

        // Outsider blocked with ForbiddenException; unattached / wrong-message uniform NotFoundException
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            mediaSvc.GetDownloadUrlAsync(outsiderId, convId, sent.Id, imgInit.MediaId));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            mediaSvc.GetDownloadUrlAsync(bobId, convId, sent.Id, unattachedInit.MediaId));

        // Blocked conversation STILL allows Bob to download preserved history!
        await blockSvc.BlockUserAsync(aliceId, bobId);
        var blockedHistoryDownload = await mediaSvc.GetDownloadUrlAsync(bobId, convId, sent.Id, imgInit.MediaId);
        Assert.StartsWith("/dev-media/download?token=", blockedHistoryDownload.Url, StringComparison.Ordinal);
        await blockSvc.UnblockUserAsync(aliceId, bobId);

        // DeleteForMe hides media for Alice (404), while Bob can still access until Bob clears chat
        await msgSvc.DeleteForMeAsync(aliceId, convId, sent.Id);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            mediaSvc.GetDownloadUrlAsync(aliceId, convId, sent.Id, imgInit.MediaId));
        var stillVisibleToBob = await mediaSvc.GetDownloadUrlAsync(bobId, convId, sent.Id, imgInit.MediaId);
        Assert.NotNull(stillVisibleToBob);

        clock.Advance(TimeSpan.FromSeconds(5));
        await convSvc.ClearChatAsync(bobId, convId);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            mediaSvc.GetDownloadUrlAsync(bobId, convId, sent.Id, imgInit.MediaId));
    }

    // =========================================================================
    // 4. MessageService & ConversationService Media Integration Tests
    // =========================================================================

    [Fact]
    public async Task MessageService_And_ConversationService_HandleMediaSendsRepliesEditsDeletesAndSidebarPreviews()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 17, 0, 0, TimeSpan.Zero));
        var notifier = new RecordingRealtimeNotifier();
        await using var provider = (ServiceProvider)BuildProvider(timeProvider: clock, realtimeNotifier: notifier);

        UserId aliceId;
        UserId bobId;
        ConversationId convId;

        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            aliceId = new UserId((await SeedUserAsync(db, "msg_a", "Alice")).Id);
            bobId = new UserId((await SeedUserAsync(db, "msg_b", "Bob")).Id);
            convId = (await SeedConversationAsync(db, aliceId, bobId, clock.GetUtcNow())).Id;
        }

        await using var scope = provider.CreateAsyncScope();
        var mediaSvc = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var msgSvc = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var convSvc = scope.ServiceProvider.GetRequiredService<IConversationService>();
        var dbCtx = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var png = CreateMinimalPngBytes(24, 12);
        var pdf = CreateMinimalPdfBytes();

        async Task<MediaId> UploadReadyImageAsync(UserId uploader, string name)
        {
            var init = await mediaSvc.InitiateUploadAsync(uploader, convId, new InitiateMediaUploadRequestDto(name, "image/png", png.Length));
            var row = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == init.MediaId);
            await WriteStorageObjectAsync(_fixture.MediaRootDirectory, row.ObjectKey, png);
            await mediaSvc.CompleteUploadAsync(uploader, convId, init.MediaId);
            return init.MediaId;
        }

        var img1 = await UploadReadyImageAsync(aliceId, "one.png");
        var img2 = await UploadReadyImageAsync(bobId, "two.png");

        var pdfInit = await mediaSvc.InitiateUploadAsync(bobId, convId, new InitiateMediaUploadRequestDto("spec.pdf", "application/pdf", pdf.Length));
        var pdfRow = await dbCtx.Media.AsNoTracking().SingleAsync(m => m.Id == pdfInit.MediaId);
        await WriteStorageObjectAsync(_fixture.MediaRootDirectory, pdfRow.ObjectKey, pdf);
        await mediaSvc.CompleteUploadAsync(bobId, convId, pdfInit.MediaId);

        // Validation guards
        await Assert.ThrowsAsync<DomainException>(() =>
            msgSvc.SendMessageAsync(aliceId, convId, new SendMessageRequestDto("\u200B  ", $"empty-{Guid.NewGuid():N}")));
        await Assert.ThrowsAsync<DomainException>(() =>
            msgSvc.SendMessageAsync(aliceId, convId, new SendMessageRequestDto(null, $"dup-{Guid.NewGuid():N}", MediaIds: [img1, img1])));
        await Assert.ThrowsAsync<DomainException>(() =>
            msgSvc.SendMessageAsync(aliceId, convId, new SendMessageRequestDto(
                null,
                $"six-{Guid.NewGuid():N}",
                MediaIds: [MediaId.New(), MediaId.New(), MediaId.New(), MediaId.New(), MediaId.New(), MediaId.New()])));

        // Send captionless photo (invisible caption normalized to empty string in DTO / null in DB)
        var idempKey = $"photo-{Guid.NewGuid():N}";
        var photoMsg = await msgSvc.SendMessageAsync(
            aliceId,
            convId,
            new SendMessageRequestDto("\u200B  ", idempKey, MediaIds: [img1]));

        Assert.Equal(string.Empty, photoMsg.Content);
        Assert.Single(photoMsg.Attachments);
        Assert.Single(notifier.NewMessages);

        // Idempotent retry returns same attachment DTO and does NOT emit duplicate SignalR broadcast
        var idempotentRetry = await msgSvc.SendMessageAsync(
            aliceId,
            convId,
            new SendMessageRequestDto("  ", idempKey, MediaIds: [img1]));
        Assert.Equal(photoMsg.Id, idempotentRetry.Id);
        Assert.Single(idempotentRetry.Attachments);
        Assert.Single(notifier.NewMessages);

        // Sidebar shows "📷 Photo"
        var listAfterPhoto = await convSvc.GetConversationListAsync(bobId);
        Assert.Equal("📷 Photo", Assert.Single(listAfterPhoto).LastMessagePreview);

        // Edit photo message to add caption, then clear caption
        clock.Advance(TimeSpan.FromSeconds(10));
        var editedAddCaption = await msgSvc.EditMessageAsync(aliceId, convId, photoMsg.Id, new EditMessageRequestDto("Look at this"));
        Assert.Equal("Look at this", editedAddCaption.Content);
        Assert.Equal("📷 Look at this", editedAddCaption.PreviewText);

        clock.Advance(TimeSpan.FromSeconds(10));
        var editedClearCaption = await msgSvc.EditMessageAsync(aliceId, convId, photoMsg.Id, new EditMessageRequestDto(null));
        Assert.Equal(string.Empty, editedClearCaption.Content);
        Assert.Equal("📷 Photo", editedClearCaption.PreviewText);

        // Send a middle text message + a Page-1 reply referencing the older captionless photo (cross-page parent media preview)
        clock.Advance(TimeSpan.FromSeconds(10));
        var middleText = await msgSvc.SendMessageAsync(aliceId, convId, new SendMessageRequestDto("filler", $"mid-{Guid.NewGuid():N}"));

        clock.Advance(TimeSpan.FromSeconds(10));
        var mixedMsg = await msgSvc.SendMessageAsync(
            bobId,
            convId,
            new SendMessageRequestDto(null, $"rep-{Guid.NewGuid():N}", ReplyToMessageId: photoMsg.Id, MediaIds: [img2, pdfInit.MediaId]));

        // Sidebar shows mixed attachment count ("📎 2 attachments")
        var listAfterMixed = await convSvc.GetConversationListAsync(aliceId);
        Assert.Equal("📎 2 attachments", Assert.Single(listAfterMixed).LastMessagePreview);

        // Fetch Page 1 with pageSize=1 so photoMsg is NOT in pageItems -> cross-page reply preview still formats "📷 Photo"
        var page1 = await msgSvc.GetMessagesAsync(aliceId, convId, cursor: null, pageSize: 1);
        var topItem = Assert.Single(page1.Messages);
        Assert.Equal(mixedMsg.Id, topItem.Id);
        Assert.NotNull(topItem.ReplyTo);
        Assert.Equal("📷 Photo", topItem.ReplyTo!.PreviewContent);

        // DeleteForMe on mixedMsg and middleText updates preview back to "📷 Photo"
        await msgSvc.DeleteForMeAsync(aliceId, convId, mixedMsg.Id);
        await msgSvc.DeleteForMeAsync(aliceId, convId, middleText.Id);
        var lastDeletedForMe = notifier.DeletedForMe[^1];
        Assert.Equal(photoMsg.Id.Value, lastDeletedForMe.NewLastMessageId);
        Assert.Equal("📷 Photo", lastDeletedForMe.NewLastMessagePreview);

        // DeleteForEveryone on photoMsg scrubs attachments across GetMessages, Sidebar, ReplyTo, and GetDownloadUrlAsync
        clock.Advance(TimeSpan.FromSeconds(10));
        await msgSvc.DeleteForEveryoneAsync(aliceId, convId, photoMsg.Id);

        var pageForBob = await msgSvc.GetMessagesAsync(bobId, convId, cursor: null, pageSize: 10);
        var tombstonedPhoto = pageForBob.Messages.Single(m => m.Id == photoMsg.Id);
        Assert.True(tombstonedPhoto.IsDeletedForEveryone);
        Assert.Empty(tombstonedPhoto.Attachments);

        var replyToTombstone = pageForBob.Messages.Single(m => m.Id == mixedMsg.Id);
        Assert.NotNull(replyToTombstone.ReplyTo);
        Assert.True(replyToTombstone.ReplyTo!.IsDeletedForEveryone);
        Assert.Equal("This message was deleted.", replyToTombstone.ReplyTo.PreviewContent);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            mediaSvc.GetDownloadUrlAsync(bobId, convId, photoMsg.Id, img1));
    }

    // =========================================================================
    // 5. MediaCleanupService (5-Step SQL-First Lifecycle + Races) Tests
    // =========================================================================

    [Fact]
    public async Task MediaCleanupService_ExecutesAllFiveStepsSafelyWithoutBreakingAttachedOrFreshObjects()
    {
        var isolatedMediaRoot = Path.Combine(Path.GetTempPath(), $"aos_cleanup_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedMediaRoot);

        try
        {
            var start = new DateTimeOffset(2026, 11, 24, 18, 0, 0, TimeSpan.Zero);
            var clock = new TestTimeProvider(start);

            await using var provider = (ServiceProvider)BuildProvider(
                timeProvider: clock,
                customMediaRoot: isolatedMediaRoot);

            UserId aliceId;
            UserId bobId;
            ConversationId convId;

            await using (var setupScope = provider.CreateAsyncScope())
            {
                var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                aliceId = new UserId((await SeedUserAsync(db, "cln_a", "Alice")).Id);
                bobId = new UserId((await SeedUserAsync(db, "cln_b", "Bob")).Id);
                convId = (await SeedConversationAsync(db, aliceId, bobId, start)).Id;
            }

            var png = CreateMinimalPngBytes(10, 10);

            Media stalePending;
            Media freshPending;
            Media staleUploaded;
            Media attachedMedia;
            Media unpurgedDeleted;
            Media oldPurgedRow;
            string orphanPendingKey = $"{MediaConstraints.PendingKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";
            string orphanMediaKey = $"{MediaConstraints.MediaKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";
            string freshOrphanKey = $"{MediaConstraints.PendingKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";

            await using (var seedScope = provider.CreateAsyncScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Media.ExecuteDeleteAsync();

                // Step 1 target: Pending created 45m ago
                stalePending = Media.CreatePending(aliceId, convId, MediaKind.Image, "stale.png", "image/png", png.Length, start.AddMinutes(-45));
                db.Media.Add(stalePending);

                // Safe Pending created 5m ago
                freshPending = Media.CreatePending(aliceId, convId, MediaKind.Image, "fresh.png", "image/png", png.Length, start.AddMinutes(-5));
                db.Media.Add(freshPending);

                // Step 2 target: Uploaded (unattached) 3h ago
                staleUploaded = Media.CreatePending(aliceId, convId, MediaKind.Image, "abandon.png", "image/png", png.Length, start.AddHours(-4));
                staleUploaded.MarkUploaded($"{MediaConstraints.MediaKeyPrefix}{staleUploaded.StorageObjectId}", "image/png", png.Length, 10, 10, null, start.AddHours(-3));
                db.Media.Add(staleUploaded);

                // Safe Attached 5h ago (must NEVER be touched!)
                var msg = Message.Create(convId, aliceId, $"keep-{Guid.NewGuid():N}", null, start.AddHours(-5), hasAttachments: true);
                db.Messages.Add(msg);
                await db.SaveChangesAsync();

                attachedMedia = Media.CreatePending(aliceId, convId, MediaKind.Image, "keep.png", "image/png", png.Length, start.AddHours(-5));
                attachedMedia.MarkUploaded($"{MediaConstraints.MediaKeyPrefix}{attachedMedia.StorageObjectId}", "image/png", png.Length, 10, 10, null, start.AddHours(-5));
                attachedMedia.MarkAttached(msg.Id, 0, start.AddHours(-5));
                db.Media.Add(attachedMedia);

                // Step 3 target: Deleted row with PurgedAtUtc == null (failed cancel)
                unpurgedDeleted = Media.CreatePending(aliceId, convId, MediaKind.Image, "unpurged.png", "image/png", png.Length, start.AddMinutes(-20));
                unpurgedDeleted.MarkUploaded($"{MediaConstraints.MediaKeyPrefix}{unpurgedDeleted.StorageObjectId}", "image/png", png.Length, 10, 10, null, start.AddMinutes(-15));
                unpurgedDeleted.MarkDeleted(start.AddMinutes(-10));
                db.Media.Add(unpurgedDeleted);

                // Step 5 target: Failed & purged 50h ago
                oldPurgedRow = Media.CreatePending(aliceId, convId, MediaKind.Image, "ancient.png", "image/png", png.Length, start.AddHours(-52));
                oldPurgedRow.MarkFailed("Old failure", start.AddHours(-51));
                oldPurgedRow.MarkPurged(start.AddHours(-50));
                db.Media.Add(oldPurgedRow);

                await db.SaveChangesAsync();
            }

            // Write storage files + orphans
            await WriteStorageObjectAsync(isolatedMediaRoot, stalePending.ObjectKey, png, start.AddMinutes(-45));
            await WriteStorageObjectAsync(isolatedMediaRoot, freshPending.ObjectKey, png, start.AddMinutes(-5));
            await WriteStorageObjectAsync(isolatedMediaRoot, staleUploaded.ObjectKey, png, start.AddHours(-3));
            await WriteStorageObjectAsync(isolatedMediaRoot, attachedMedia.ObjectKey, png, start.AddHours(-5));
            await WriteStorageObjectAsync(isolatedMediaRoot, unpurgedDeleted.ObjectKey, png, start.AddMinutes(-15));
            await WriteStorageObjectAsync(isolatedMediaRoot, orphanPendingKey, png, start.AddHours(-2));
            await WriteStorageObjectAsync(isolatedMediaRoot, orphanMediaKey, png, start.AddHours(-2));
            await WriteStorageObjectAsync(isolatedMediaRoot, freshOrphanKey, png, start.AddMinutes(-20));

            var cleanupService = provider.GetRequiredService<MediaCleanupService>();
            var summary = await cleanupService.RunCleanupPassAsync();

            Assert.Equal(1, summary.TimedOutPendingCount);
            Assert.Equal(1, summary.ExpiredUploadedCount);
            Assert.Equal(3, summary.StorageObjectsPurgedCount);
            Assert.Equal(2, summary.OrphanObjectsDeletedCount);
            Assert.Equal(1, summary.MetadataRowsDeletedCount);

            await using var verifyScope = provider.CreateAsyncScope();
            var dbVerify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var stalePendingDb = await dbVerify.Media.AsNoTracking().SingleAsync(m => m.Id == stalePending.Id);
            Assert.Equal(MediaState.Failed, stalePendingDb.State);
            Assert.Equal(png.Length, stalePendingDb.VerifiedSizeBytes);
            Assert.NotNull(stalePendingDb.PurgedAtUtc);
            Assert.False(File.Exists(ResolveDiskPath(isolatedMediaRoot, stalePending.ObjectKey)));

            var freshPendingDb = await dbVerify.Media.AsNoTracking().SingleAsync(m => m.Id == freshPending.Id);
            Assert.Equal(MediaState.Pending, freshPendingDb.State);
            Assert.Null(freshPendingDb.PurgedAtUtc);
            Assert.True(File.Exists(ResolveDiskPath(isolatedMediaRoot, freshPending.ObjectKey)));

            var staleUploadedDb = await dbVerify.Media.AsNoTracking().SingleAsync(m => m.Id == staleUploaded.Id);
            Assert.Equal(MediaState.Deleted, staleUploadedDb.State);
            Assert.NotNull(staleUploadedDb.PurgedAtUtc);
            Assert.False(File.Exists(ResolveDiskPath(isolatedMediaRoot, staleUploaded.ObjectKey)));

            var attachedDb = await dbVerify.Media.AsNoTracking().SingleAsync(m => m.Id == attachedMedia.Id);
            Assert.Equal(MediaState.Attached, attachedDb.State);
            Assert.Null(attachedDb.PurgedAtUtc);
            Assert.True(File.Exists(ResolveDiskPath(isolatedMediaRoot, attachedMedia.ObjectKey)));

            var unpurgedDb = await dbVerify.Media.AsNoTracking().SingleAsync(m => m.Id == unpurgedDeleted.Id);
            Assert.NotNull(unpurgedDb.PurgedAtUtc);
            Assert.False(File.Exists(ResolveDiskPath(isolatedMediaRoot, unpurgedDeleted.ObjectKey)));

            Assert.False(File.Exists(ResolveDiskPath(isolatedMediaRoot, orphanPendingKey)));
            Assert.False(File.Exists(ResolveDiskPath(isolatedMediaRoot, orphanMediaKey)));
            Assert.True(File.Exists(ResolveDiskPath(isolatedMediaRoot, freshOrphanKey)));
            Assert.Null(await dbVerify.Media.AsNoTracking().FirstOrDefaultAsync(m => m.Id == oldPurgedRow.Id));
        }
        finally
        {
            if (Directory.Exists(isolatedMediaRoot))
            {
                Directory.Delete(isolatedMediaRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task MediaCleanupService_Step3FailureDoesNotInfiniteLoop_AndProtectsConcurrentStorageError()
    {
        var isolatedMediaRoot = Path.Combine(Path.GetTempPath(), $"aos_cleanup_err_{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedMediaRoot);

        try
        {
            var start = new DateTimeOffset(2026, 11, 24, 19, 0, 0, TimeSpan.Zero);
            var clock = new TestTimeProvider(start);

            var dpProvider = DataProtectionProvider.Create("CleanupTest");
            var realStorage = new LocalDiskMediaStorage(new LocalMediaSigningService(dpProvider), isolatedMediaRoot);

            Media? failDeleteTarget = null;
            Media? succeedDeleteTarget = null;

            var wrappedStorage = new InterceptingMediaStorage(
                realStorage,
                beforeDelete: objectKey =>
                    failDeleteTarget is not null && objectKey == failDeleteTarget.ObjectKey
                        ? throw new MediaStorageException("Simulated storage outage")
                        : Task.FromResult(true));

            await using var provider = (ServiceProvider)BuildProvider(
                timeProvider: clock,
                customMediaStorage: wrappedStorage);

            await using (var seedScope = provider.CreateAsyncScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Media.ExecuteDeleteAsync();
                var aliceId = new UserId((await SeedUserAsync(db, "cerr_a", "Alice")).Id);
                var bobId = new UserId((await SeedUserAsync(db, "cerr_b", "Bob")).Id);
                var convId = (await SeedConversationAsync(db, aliceId, bobId, start)).Id;

                failDeleteTarget = Media.CreatePending(aliceId, convId, MediaKind.Image, "f1.png", "image/png", 100, start.AddMinutes(-20));
                failDeleteTarget.MarkDeleted(start.AddMinutes(-10));

                succeedDeleteTarget = Media.CreatePending(aliceId, convId, MediaKind.Image, "f2.png", "image/png", 100, start.AddMinutes(-20));
                succeedDeleteTarget.MarkDeleted(start.AddMinutes(-10));

                db.Media.AddRange(failDeleteTarget, succeedDeleteTarget);
                await db.SaveChangesAsync();
            }

            await WriteStorageObjectAsync(isolatedMediaRoot, failDeleteTarget.ObjectKey, CreateMinimalPngBytes());
            await WriteStorageObjectAsync(isolatedMediaRoot, succeedDeleteTarget.ObjectKey, CreateMinimalPngBytes());

            var cleanup = new MediaCleanupService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                wrappedStorage,
                timeProvider: clock);

            var summary = await cleanup.RunCleanupPassAsync();

            // Completes cleanly without looping; failing object keeps PurgedAtUtc == null while sibling succeeds
            Assert.Equal(1, summary.StorageObjectsPurgedCount);

            await using var verifyScope = provider.CreateAsyncScope();
            var dbVerify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var failedRow = await dbVerify.Media.AsNoTracking().SingleAsync(m => m.Id == failDeleteTarget.Id);
            var succeededRow = await dbVerify.Media.AsNoTracking().SingleAsync(m => m.Id == succeedDeleteTarget.Id);

            Assert.Null(failedRow.PurgedAtUtc);
            Assert.NotNull(succeededRow.PurgedAtUtc);
        }
        finally
        {
            if (Directory.Exists(isolatedMediaRoot))
            {
                Directory.Delete(isolatedMediaRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CompleteAndCancelUploadAsync_OutsiderProbingNonExistentMediaId_ReceivesForbiddenWithoutExistenceOracle()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 19, 30, 0, TimeSpan.Zero));
        await using var provider = (ServiceProvider)BuildProvider(timeProvider: clock);

        UserId outsiderId;
        ConversationId convId;

        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var aliceId = new UserId((await SeedUserAsync(db, "ora_a", "Alice")).Id);
            var bobId = new UserId((await SeedUserAsync(db, "ora_b", "Bob")).Id);
            outsiderId = new UserId((await SeedUserAsync(db, "ora_o", "Outsider")).Id);
            convId = (await SeedConversationAsync(db, aliceId, bobId, clock.GetUtcNow())).Id;
        }

        await using var scope = provider.CreateAsyncScope();
        var mediaSvc = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var nonexistentId = MediaId.New();

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            mediaSvc.CompleteUploadAsync(outsiderId, convId, nonexistentId));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            mediaSvc.CancelUploadAsync(outsiderId, convId, nonexistentId));
    }

    [Fact]
    public async Task CompleteUploadAsync_ConcurrentTransitionToAttached_ThrowsConflictAndPreservesPermanentObject()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 20, 0, 0, TimeSpan.Zero));
        await using var baseProvider = (ServiceProvider)BuildProvider(timeProvider: clock);

        UserId aliceId;
        ConversationId convId;
        MediaId mediaId;
        string pendingKey;
        string permanentKey;
        var png = CreateMinimalPngBytes(14, 14);

        await using (var setupScope = baseProvider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var alice = await SeedUserAsync(db, "att_race_a", "Alice");
            var bob = await SeedUserAsync(db, "att_race_b", "Bob");
            aliceId = new UserId(alice.Id);
            convId = (await SeedConversationAsync(db, aliceId, new UserId(bob.Id), clock.GetUtcNow())).Id;

            var mediaSvc = setupScope.ServiceProvider.GetRequiredService<IMediaService>();
            var init = await mediaSvc.InitiateUploadAsync(
                aliceId,
                convId,
                new InitiateMediaUploadRequestDto("attached-race.png", "image/png", png.Length));
            mediaId = init.MediaId;

            var row = await db.Media.AsNoTracking().SingleAsync(m => m.Id == mediaId);
            pendingKey = row.ObjectKey;
            permanentKey = $"{MediaConstraints.MediaKeyPrefix}{row.StorageObjectId}";
            await WriteStorageObjectAsync(_fixture.MediaRootDirectory, pendingKey, png);
        }

        var realStorage = baseProvider.GetRequiredService<LocalDiskMediaStorage>();
        var raceSimulated = false;
        string? losingPromotedKey = null;

        // Wrap persistence so right after Request B writes its fresh permanent key and before UpdateAsync runs,
        // Request A finishes with its own permanent object + attaches the row cleanly in SQL.
        var customServices = _fixture.CreateServiceCollection();
        customServices.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        customServices.Replace(ServiceDescriptor.Singleton<IMediaStorage>(new AttachedRaceMediaStorage(
            realStorage,
            async promotedKey =>
            {
                losingPromotedKey = promotedKey;
                if (raceSimulated)
                {
                    return;
                }

                raceSimulated = true;
                await WriteStorageObjectAsync(_fixture.MediaRootDirectory, permanentKey, png);

                await using var concurrentScope = baseProvider.CreateAsyncScope();
                var concurrentDb = concurrentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var msg = Message.Create(convId, aliceId, $"conc-{Guid.NewGuid():N}", null, clock.GetUtcNow(), hasAttachments: true);
                concurrentDb.Messages.Add(msg);
                await concurrentDb.SaveChangesAsync();

                var row = await concurrentDb.Media.SingleAsync(m => m.Id == mediaId);
                row.MarkUploaded(permanentKey, "image/png", png.Length, 14, 14, null, clock.GetUtcNow());
                row.MarkAttached(msg.Id, 0, clock.GetUtcNow());
                await concurrentDb.SaveChangesAsync();
            })));

        await using var racingProvider = customServices.BuildServiceProvider();
        await using var scope = racingProvider.CreateAsyncScope();
        var racingMediaSvc = scope.ServiceProvider.GetRequiredService<IMediaService>();

        await Assert.ThrowsAsync<ConflictException>(() =>
            racingMediaSvc.CompleteUploadAsync(aliceId, convId, mediaId));

        // Winning permanent media/ object MUST remain intact with original bytes while losing permanent key + staging are cleaned up
        Assert.True(File.Exists(ResolveDiskPath(_fixture.MediaRootDirectory, permanentKey)));
        Assert.Equal(png, await File.ReadAllBytesAsync(ResolveDiskPath(_fixture.MediaRootDirectory, permanentKey)));
        Assert.NotNull(losingPromotedKey);
        Assert.NotEqual(permanentKey, losingPromotedKey);
        Assert.False(File.Exists(ResolveDiskPath(_fixture.MediaRootDirectory, losingPromotedKey!)));
        Assert.False(File.Exists(ResolveDiskPath(_fixture.MediaRootDirectory, pendingKey)));
    }

    [Fact]
    public async Task CompleteUploadAsync_MidVerifyStagingDeletionRace_RecoversIdempotentlyWithoutLeakingObjectKey()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 24, 20, 30, 0, TimeSpan.Zero));
        await using var baseProvider = (ServiceProvider)BuildProvider(timeProvider: clock);

        UserId aliceId;
        ConversationId convId;
        MediaId mediaId;
        string pendingKey;
        string winningKey = $"{MediaConstraints.MediaKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";
        var pdfBytes = CreateMinimalPdfBytes();

        await using (var setupScope = baseProvider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            aliceId = new UserId((await SeedUserAsync(db, "mid_race_a", "Alice")).Id);
            var bobId = new UserId((await SeedUserAsync(db, "mid_race_b", "Bob")).Id);
            convId = (await SeedConversationAsync(db, aliceId, bobId, clock.GetUtcNow())).Id;

            var mediaSvc = setupScope.ServiceProvider.GetRequiredService<IMediaService>();
            var init = await mediaSvc.InitiateUploadAsync(
                aliceId,
                convId,
                new InitiateMediaUploadRequestDto("race.pdf", "application/pdf", pdfBytes.Length));
            mediaId = init.MediaId;

            var row = await db.Media.AsNoTracking().SingleAsync(m => m.Id == mediaId);
            pendingKey = row.ObjectKey;
            await WriteStorageObjectAsync(_fixture.MediaRootDirectory, pendingKey, pdfBytes);
        }

        var realStorage = baseProvider.GetRequiredService<LocalDiskMediaStorage>();
        var raceStorage = new InterceptingMediaStorage(
            realStorage,
            onPromoteOrVerify: async () =>
            {
                await WriteStorageObjectAsync(_fixture.MediaRootDirectory, winningKey, pdfBytes);

                await using var concurrentScope = baseProvider.CreateAsyncScope();
                var concurrentDb = concurrentScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var row = await concurrentDb.Media.SingleAsync(m => m.Id == mediaId);
                if (row.State == MediaState.Pending)
                {
                    row.MarkUploaded(winningKey, "application/pdf", pdfBytes.Length, null, null, null, clock.GetUtcNow());
                    await concurrentDb.SaveChangesAsync();
                }

                await realStorage.DeleteObjectAsync(pendingKey);
            });

        await using var racingProvider = (ServiceProvider)BuildProvider(
            timeProvider: clock,
            customMediaStorage: raceStorage);

        await using var scopeB = racingProvider.CreateAsyncScope();
        var mediaSvcB = scopeB.ServiceProvider.GetRequiredService<IMediaService>();

        var result = await mediaSvcB.CompleteUploadAsync(aliceId, convId, mediaId);
        Assert.Equal(mediaId, result.Id);
        Assert.Equal("application/pdf", result.ContentType);
    }

    [Fact]
    public async Task MediaCleanupService_And_AddWithMediaAsync_EnforceCleanConcurrencyInvariant()
    {
        var isolatedMediaRoot = Path.Combine(Path.GetTempPath(), $"aos_cleanup_race_{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolatedMediaRoot);

        try
        {
            var start = new DateTimeOffset(2026, 11, 24, 21, 0, 0, TimeSpan.Zero);
            var clock = new TestTimeProvider(start);

            await using var provider = (ServiceProvider)BuildProvider(
                timeProvider: clock,
                customMediaRoot: isolatedMediaRoot);

            UserId aliceId;
            ConversationId convId;
            var png = CreateMinimalPngBytes(12, 12);
            const int iterations = 6;
            var mediaList = new List<Media>(iterations);

            await using (var seedScope = provider.CreateAsyncScope())
            {
                var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Media.ExecuteDeleteAsync();

                aliceId = new UserId((await SeedUserAsync(db, "crace_a", "Alice")).Id);
                var bobId = new UserId((await SeedUserAsync(db, "crace_b", "Bob")).Id);
                convId = (await SeedConversationAsync(db, aliceId, bobId, start)).Id;

                for (var i = 0; i < iterations; i++)
                {
                    var media = Media.CreatePending(
                        aliceId,
                        convId,
                        MediaKind.Image,
                        $"race-{i}.png",
                        "image/png",
                        png.Length,
                        start.AddHours(-4));
                    media.MarkUploaded(
                        $"{MediaConstraints.MediaKeyPrefix}{media.StorageObjectId}",
                        "image/png",
                        png.Length,
                        12,
                        12,
                        null,
                        start.AddHours(-3));
                    db.Media.Add(media);
                    mediaList.Add(media);
                }

                await db.SaveChangesAsync();
            }

            foreach (var media in mediaList)
            {
                await WriteStorageObjectAsync(isolatedMediaRoot, media.ObjectKey, png, start.AddHours(-3));
            }

            using var barrier = new Barrier(2);
            var cleanupTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                var cleanup = provider.GetRequiredService<MediaCleanupService>();
                await cleanup.RunCleanupPassAsync();
            });

            var attachTask = Task.Run(async () =>
            {
                barrier.SignalAndWait();
                foreach (var media in mediaList)
                {
                    await using var scope = provider.CreateAsyncScope();
                    var msgPersistence = scope.ServiceProvider.GetRequiredService<IMessagePersistence>();
                    var msg = Message.Create(
                        convId,
                        aliceId,
                        $"race-{media.Id.Value:N}",
                        null,
                        start,
                        hasAttachments: true);

                    try
                    {
                        await msgPersistence.AddWithMediaAsync(msg, [media.Id], start);
                    }
                    catch (Exception ex) when (ex is ConflictException or DomainException)
                    {
                        // Cleanup expired the row first; AddWithMediaAsync rolled back cleanly
                    }
                }
            });

            await Task.WhenAll(cleanupTask, attachTask);

            await using var verifyScope = provider.CreateAsyncScope();
            var dbVerify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            var finalRows = await dbVerify.Media.AsNoTracking()
                .Where(m => mediaList.Select(x => x.Id).Contains(m.Id))
                .ToListAsync();

            Assert.Equal(iterations, finalRows.Count);
            foreach (var row in finalRows)
            {
                if (row.State == MediaState.Attached)
                {
                    Assert.NotNull(row.MessageId);
                    Assert.Null(row.PurgedAtUtc);
                    Assert.True(File.Exists(ResolveDiskPath(isolatedMediaRoot, row.ObjectKey)));

                    var msgExists = await dbVerify.Messages.AsNoTracking()
                        .AnyAsync(m => m.ConversationId == convId && m.Id == row.MessageId && m.HasAttachments);
                    Assert.True(msgExists);
                }
                else
                {
                    Assert.Equal(MediaState.Deleted, row.State);
                    Assert.Null(row.MessageId);
                    Assert.NotNull(row.PurgedAtUtc);
                    Assert.False(File.Exists(ResolveDiskPath(isolatedMediaRoot, row.ObjectKey)));
                }
            }

            // Ensure no message exists in this conversation marked HasAttachments == true without an Attached media row
            var convMessages = await dbVerify.Messages.AsNoTracking()
                .Where(m => m.ConversationId == convId && m.HasAttachments)
                .Select(m => m.Id)
                .ToListAsync();
            var attachedMessageIds = finalRows
                .Where(r => r.State == MediaState.Attached)
                .Select(r => r.MessageId!)
                .ToHashSet();
            Assert.True(convMessages.All(id => attachedMessageIds.Contains(id)));
        }
        finally
        {
            if (Directory.Exists(isolatedMediaRoot))
            {
                Directory.Delete(isolatedMediaRoot, recursive: true);
            }
        }
    }

    private sealed class AttachedRaceMediaStorage : IMediaStorage
    {
        private readonly IMediaStorage _inner;
        private readonly Func<string, Task> _onPromote;

        public AttachedRaceMediaStorage(IMediaStorage inner, Func<string, Task> onPromote)
        {
            _inner = inner;
            _onPromote = onPromote;
        }

        public string GeneratePresignedPutUrl(string objectKey, string contentType, long contentLength, TimeSpan expiresIn, DateTimeOffset nowUtc)
            => _inner.GeneratePresignedPutUrl(objectKey, contentType, contentLength, expiresIn, nowUtc);

        public string GeneratePresignedGetUrl(string objectKey, string responseContentType, string responseContentDisposition, TimeSpan expiresIn, DateTimeOffset nowUtc)
            => _inner.GeneratePresignedGetUrl(objectKey, responseContentType, responseContentDisposition, expiresIn, nowUtc);

        public Task<MediaObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct = default)
            => _inner.GetObjectMetadataAsync(objectKey, ct);

        public Task<byte[]> ReadByteRangeAsync(string objectKey, long offset, int length, string? ifMatchETag = null, CancellationToken ct = default)
            => _inner.ReadByteRangeAsync(objectKey, offset, length, ifMatchETag, ct);

        public Task DownloadToStreamAsync(string objectKey, Stream destination, long maxBytes, string? ifMatchETag = null, CancellationToken ct = default)
            => _inner.DownloadToStreamAsync(objectKey, destination, maxBytes, ifMatchETag, ct);

        public async Task PromoteByCopyAsync(string stagingKey, string permanentKey, string verifiedContentType, string? contentDisposition, string? ifMatchETag = null, CancellationToken ct = default)
        {
            await _inner.PromoteByCopyAsync(stagingKey, permanentKey, verifiedContentType, contentDisposition, ifMatchETag, ct);
            await _onPromote(permanentKey);
        }

        public async Task PromoteFromStreamAsync(string stagingKey, string permanentKey, Stream content, long contentLength, string verifiedContentType, string? contentDisposition, CancellationToken ct = default)
        {
            await _inner.PromoteFromStreamAsync(stagingKey, permanentKey, content, contentLength, verifiedContentType, contentDisposition, ct);
            await _onPromote(permanentKey);
        }

        public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
            => _inner.DeleteObjectAsync(objectKey, ct);

        public IAsyncEnumerable<StoredObjectInfo> ListObjectsAsync(string prefix, CancellationToken ct = default)
            => _inner.ListObjectsAsync(prefix, ct);
    }

    private sealed class ThrowingConflictMediaVerifier : IMediaVerifier
    {
        private readonly string _message;

        public ThrowingConflictMediaVerifier(string message) => _message = message;

        public Task<VerifiedMediaResult> VerifyAndSanitizeAsync(
            Media media,
            MediaObjectMetadata stagingMetadata,
            CancellationToken ct = default) =>
            throw new ConflictException(_message);
    }
}
