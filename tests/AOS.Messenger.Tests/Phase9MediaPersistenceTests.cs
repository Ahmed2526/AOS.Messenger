using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase9MediaPersistenceTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public Phase9MediaPersistenceTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
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

    private static async Task<Media> SeedUploadedMediaAsync(
        ApplicationDbContext db,
        UserId uploaderId,
        ConversationId conversationId,
        MediaKind kind,
        string fileName,
        string mimeType,
        long sizeBytes,
        DateTimeOffset now)
    {
        var media = Media.CreatePending(uploaderId, conversationId, kind, fileName, mimeType, sizeBytes, now);
        media.MarkUploaded($"media/{media.StorageObjectId}", mimeType, sizeBytes, null, null, null, now);
        db.Media.Add(media);
        await db.SaveChangesAsync();
        return media;
    }

    // =========================================================================
    // 1. MediaPreviewFormatter Unit Tests
    // =========================================================================

    [Fact]
    public void MediaPreviewFormatter_FormatsSingleAndBatchAttachments_WithAndWithoutCaptions()
    {
        var img = new MediaAttachmentDto(MediaId.New(), "image", "photo.jpg", "image/jpeg", 1024, 100, 100, null, 0);
        var vid = new MediaAttachmentDto(MediaId.New(), "video", "clip.mp4", "video/mp4", 2048, null, null, null, 0);
        var doc = new MediaAttachmentDto(MediaId.New(), "document", "report.pdf", "application/pdf", 4096, null, null, null, 0);
        var voice = new MediaAttachmentDto(MediaId.New(), "voice", "note.ogg", "audio/ogg", 512, null, null, 3000, 0);

        Assert.Equal("Hello", MediaPreviewFormatter.FormatPreview("  Hello  ", (IReadOnlyList<MediaAttachmentDto>)[]));
        Assert.Equal(string.Empty, MediaPreviewFormatter.FormatPreview("   ", (IReadOnlyList<MediaAttachmentDto>?)null));

        Assert.Equal("📷 Photo", MediaPreviewFormatter.FormatPreview(null, [img]));
        Assert.Equal("📷 Photo", MediaPreviewFormatter.FormatPreview("\u200B  ", [img]));
        Assert.Equal("🎬 Video", MediaPreviewFormatter.FormatPreview(null, [vid]));
        Assert.Equal("🎤 Voice message", MediaPreviewFormatter.FormatPreview(null, [voice]));
        Assert.Equal("📎 report.pdf", MediaPreviewFormatter.FormatPreview(null, [doc]));

        Assert.Equal("📷 3 photos", MediaPreviewFormatter.FormatPreview(null, [img, img, img]));
        Assert.Equal("🎬 2 videos", MediaPreviewFormatter.FormatPreview(null, [vid, vid]));
        Assert.Equal("🎤 2 voice messages", MediaPreviewFormatter.FormatPreview(null, [voice, voice]));
        Assert.Equal("📎 2 files", MediaPreviewFormatter.FormatPreview(null, [doc, doc]));
        Assert.Equal("📎 3 attachments", MediaPreviewFormatter.FormatPreview(null, [img, vid, doc]));

        Assert.Equal("📷 Sunset", MediaPreviewFormatter.FormatPreview("  Sunset ", [img, img]));
        Assert.Equal("📎 See attached", MediaPreviewFormatter.FormatPreview("See attached", [img, doc]));
    }

    [Fact]
    public void MediaPreviewFormatter_TruncatesWithoutSplittingSurrogatePairs_AndSupportsEntityOverload()
    {
        var user = UserId.New();
        var conv = ConversationId.New();
        var now = DateTimeOffset.UtcNow;

        var img = Media.CreatePending(user, conv, MediaKind.Image, "pic.png", "image/png", 1024, now);
        var emojis = string.Concat(Enumerable.Repeat("😀", 60)); // 120 UTF-16 chars

        var preview = MediaPreviewFormatter.FormatPreview(emojis, [img], maxLength: 20);
        Assert.StartsWith("📷 ", preview, StringComparison.Ordinal);
        Assert.EndsWith("...", preview, StringComparison.Ordinal);
        Assert.True(preview.Length <= 20);

        for (var i = 0; i < preview.Length; i++)
        {
            if (char.IsHighSurrogate(preview[i]))
            {
                Assert.True(i + 1 < preview.Length && char.IsLowSurrogate(preview[i + 1]));
                i++;
            }
            else
            {
                Assert.False(char.IsLowSurrogate(preview[i]));
            }
        }

        // ZWJ grapheme cluster sequence must never be split in the middle of a cluster
        var family = "👨‍👩‍👧‍👦";
        var zwjCaption = string.Concat(Enumerable.Repeat(family, 5));
        var zwjPreview = MediaPreviewFormatter.FormatPreview(zwjCaption, [img], maxLength: 20);
        Assert.EndsWith("...", zwjPreview, StringComparison.Ordinal);
        Assert.True(zwjPreview.Length <= 20);
        var inner = zwjPreview["📷 ".Length..^"...".Length];
        Assert.Equal(0, inner.Length % family.Length);
        Assert.DoesNotContain("\u200D", inner[^1..], StringComparison.Ordinal);
    }

    // =========================================================================
    // 2. sp_getapplock Concurrent Quota & Pending Cap Tests
    // =========================================================================

    [Fact]
    public async Task TryCreatePendingWithQuotaCheckAsync_ConcurrentPendingBurst_EnforcesCapWithoutDeadlocks()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv = await SeedConversationAsync(db, userA, userB, now);
        }

        const int totalRequests = 15;
        const int cap = 10;

        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, totalRequests).Select(async i =>
        {
            await startGate.Task;
            await using var scope = provider.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMediaPersistence>();
            var media = Media.CreatePending(
                userA,
                conv.Id,
                MediaKind.Image,
                $"img-{i}.png",
                "image/png",
                1024,
                now);

            try
            {
                var created = await repo.TryCreatePendingWithQuotaCheckAsync(
                    media,
                    cap,
                    dailyQuotaBytes: 1_000_000_000L,
                    dailyQuotaWindowStartUtc: now.AddHours(-24));
                return (Succeeded: true, Id: (MediaId?)created.Id, Exception: (Exception?)null);
            }
            catch (DomainException ex)
            {
                return (Succeeded: false, Id: (MediaId?)null, Exception: (Exception?)ex);
            }
        }).ToList();

        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(cap, results.Count(r => r.Succeeded));
        Assert.Equal(totalRequests - cap, results.Count(r => !r.Succeeded && r.Exception is DomainException));
        Assert.All(results.Where(r => r.Succeeded), r => Assert.NotNull(r.Id));
        Assert.Equal(cap, results.Where(r => r.Succeeded).Select(r => r.Id!).Distinct().Count());
    }

    [Fact]
    public async Task TryCreatePendingWithQuotaCheckAsync_EnforcesDailyQuota_IncludingFailedVerifiedSizeAndExcludingExpired()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv = await SeedConversationAsync(db, userA, userB, now);

            // Expired (> 24h ago) 50 MB -> must NOT count
            var expired = Media.CreatePending(userA, conv.Id, MediaKind.Document, "old.pdf", "application/pdf", 50_000_000L, now.AddHours(-25));
            expired.MarkUploaded($"media/{expired.StorageObjectId}", "application/pdf", 50_000_000L, null, null, null, now.AddHours(-25));

            // Inside 24h window: Failed with VerifiedSizeBytes = 60 MB -> counts 60 MB (not declared 10 MB)
            var failed = Media.CreatePending(userA, conv.Id, MediaKind.Video, "bad.mp4", "video/mp4", 10_000_000L, now.AddHours(-2));
            failed.MarkFailed("Invalid signature", now.AddHours(-2), verifiedSizeBytes: 60_000_000L);

            // Inside 24h window: Uploaded 40 MB -> counts 40 MB
            var uploaded = Media.CreatePending(userA, conv.Id, MediaKind.Document, "cur.pdf", "application/pdf", 40_000_000L, now.AddHours(-1));
            uploaded.MarkUploaded($"media/{uploaded.StorageObjectId}", "application/pdf", 40_000_000L, null, null, null, now.AddHours(-1));

            db.Media.AddRange(expired, failed, uploaded);
            await db.SaveChangesAsync();
        }

        // Current window usage = 60 + 40 = 100 MB. Quota = 120 MB -> 20 MB remaining.
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 2).Select(async i =>
        {
            await startGate.Task;
            await using var scope = provider.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMediaPersistence>();
            var media = Media.CreatePending(
                userA,
                conv.Id,
                MediaKind.Image,
                $"burst-{i}.jpg",
                "image/jpeg",
                15_000_000L,
                now);

            try
            {
                await repo.TryCreatePendingWithQuotaCheckAsync(
                    media,
                    maxConcurrentPending: 10,
                    dailyQuotaBytes: 120_000_000L,
                    dailyQuotaWindowStartUtc: now.AddHours(-24));
                return true;
            }
            catch (DomainException)
            {
                return false;
            }
        }).ToList();

        startGate.SetResult();
        var outcomes = await Task.WhenAll(tasks);

        Assert.Equal(1, outcomes.Count(ok => ok));
        Assert.Equal(1, outcomes.Count(ok => !ok));
    }

    [Fact]
    public async Task TryCreatePendingWithQuotaCheckAsync_EnforcesInitiatedBytesCeilingAcrossNullVerifiedFailedRows()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv = await SeedConversationAsync(db, userA, userB, now);

            // Seed 2 x 1 MB Failed rows with VerifiedSizeBytes == null (e.g., abandoned before upload)
            // against a 1 MB daily quota (2 MB initiated ceiling = 2x).
            for (var i = 0; i < 2; i++)
            {
                var failedNull = Media.CreatePending(
                    userA,
                    conv.Id,
                    MediaKind.Document,
                    $"abandoned-{i}.pdf",
                    "application/pdf",
                    1_000_000L,
                    now.AddMinutes(-10));
                failedNull.MarkFailed("Upload timed out before completion.", now.AddMinutes(-5), verifiedSizeBytes: null);
                db.Media.Add(failedNull);
            }

            await db.SaveChangesAsync();
        }

        await using var testScope = provider.CreateAsyncScope();
        var repo = testScope.ServiceProvider.GetRequiredService<IMediaPersistence>();
        var next = Media.CreatePending(userA, conv.Id, MediaKind.Document, "extra.pdf", "application/pdf", 1L, now);

        await Assert.ThrowsAsync<DomainException>(() =>
            repo.TryCreatePendingWithQuotaCheckAsync(
                next,
                maxConcurrentPending: 10,
                dailyQuotaBytes: 1_000_000L,
                dailyQuotaWindowStartUtc: now.AddHours(-24)));
    }

    [Fact]
    public async Task UpdateAsync_ConcurrentModification_ThrowsConflictException()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        MediaId mediaId;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = await SeedConversationAsync(db, userA, userB, now);
            var media = Media.CreatePending(userA, conv.Id, MediaKind.Image, "race.png", "image/png", 1024, now);
            db.Media.Add(media);
            await db.SaveChangesAsync();
            mediaId = media.Id;
        }

        await using var scope1 = provider.CreateAsyncScope();
        await using var scope2 = provider.CreateAsyncScope();
        var repo1 = scope1.ServiceProvider.GetRequiredService<IMediaPersistence>();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IMediaPersistence>();

        var copy1 = await repo1.GetByIdAsync(mediaId);
        var copy2 = await repo2.GetByIdAsync(mediaId);
        Assert.NotNull(copy1);
        Assert.NotNull(copy2);

        copy1.MarkUploaded($"media/{copy1.StorageObjectId}", "image/png", 900, 10, 10, null, now.AddSeconds(1));
        await repo1.UpdateAsync(copy1);

        copy2.MarkDeleted(now.AddSeconds(2));
        await Assert.ThrowsAsync<ConflictException>(() => repo2.UpdateAsync(copy2));
    }

    // =========================================================================
    // 3. AddWithMediaAsync Atomicity, Order, Idempotency & Race Tests
    // =========================================================================

    [Fact]
    public async Task AddWithMediaAsync_AttachesMultipleMediaAtomically_PreservingRequestedDisplayOrder()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv;
        Media m1;
        Media m2;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv = await SeedConversationAsync(db, userA, userB, now);
            m1 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Image, "one.png", "image/png", 1000, now);
            m2 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Document, "two.pdf", "application/pdf", 2000, now);
        }

        // Put larger GUID first so canonical lock ordering differs from DisplayOrder [0, 1]
        var requestedOrder = m1.Id.Value.CompareTo(m2.Id.Value) > 0
            ? new[] { m1.Id, m2.Id }
            : new[] { m2.Id, m1.Id };

        await using var actScope = provider.CreateAsyncScope();
        var msgRepo = actScope.ServiceProvider.GetRequiredService<IMessagePersistence>();
        var message = Message.Create(conv.Id, userA, "multi-order-key", "Caption here", now.AddSeconds(1), hasAttachments: true);

        var result = await msgRepo.AddWithMediaAsync(message, requestedOrder, now.AddSeconds(1));

        Assert.True(result.IsNewlyCreated);
        Assert.True(result.Message.HasAttachments);
        Assert.Equal("Caption here", result.Message.Content);
        Assert.Equal(2, result.AttachedMedia.Count);
        Assert.Equal(requestedOrder[0], result.AttachedMedia[0].Id);
        Assert.Equal(0, result.AttachedMedia[0].DisplayOrder);
        Assert.Equal(requestedOrder[1], result.AttachedMedia[1].Id);
        Assert.Equal(1, result.AttachedMedia[1].DisplayOrder);
        Assert.All(result.AttachedMedia, a =>
        {
            Assert.Equal(MediaState.Attached, a.State);
            Assert.Equal(result.Message.Id, a.MessageId);
        });
    }

    [Fact]
    public async Task AddWithMediaAsync_IdempotentRetry_SequentialAndConcurrent_WithAlreadyAttachedMedia_Succeeds()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv;
        Media m1;
        Media m2;
        Media m3;
        Media m4;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv = await SeedConversationAsync(db, userA, userB, now);
            m1 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Image, "a.jpg", "image/jpeg", 1000, now);
            m2 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Image, "b.jpg", "image/jpeg", 1200, now);
            m3 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Video, "c.mp4", "video/mp4", 5000, now);
            m4 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Document, "d.pdf", "application/pdf", 3000, now);
        }

        // Sequential retry after commit (media is already State = Attached!)
        PersistMessageResult first;
        await using (var s1 = provider.CreateAsyncScope())
        {
            var repo = s1.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var msg = Message.Create(conv.Id, userA, "seq-media-idem", null, now, hasAttachments: true);
            first = await repo.AddWithMediaAsync(msg, [m1.Id, m2.Id], now);
        }

        PersistMessageResult second;
        await using (var s2 = provider.CreateAsyncScope())
        {
            var repo = s2.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var retry = Message.Create(conv.Id, userA, "seq-media-idem", "\u200B  ", now.AddSeconds(5), hasAttachments: true);
            second = await repo.AddWithMediaAsync(retry, [m1.Id, m2.Id], now.AddSeconds(5));
        }

        Assert.True(first.IsNewlyCreated);
        Assert.False(second.IsNewlyCreated);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(new[] { m1.Id, m2.Id }, second.AttachedMedia.Select(a => a.Id));

        // Concurrent parallel retry burst (5 requests racing with identical key & media batch)
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var burstTasks = Enumerable.Range(0, 5).Select(async i =>
        {
            await startGate.Task;
            await using var scope = provider.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var msg = Message.Create(conv.Id, userA, "concurrent-media-idem", "Batch", now.AddSeconds(i), hasAttachments: true);
            return await repo.AddWithMediaAsync(msg, [m3.Id, m4.Id], now.AddSeconds(i));
        }).ToList();

        startGate.SetResult();
        var burstResults = await Task.WhenAll(burstTasks);

        Assert.Equal(1, burstResults.Count(r => r.IsNewlyCreated));
        var winningId = burstResults.First(r => r.IsNewlyCreated).Id;
        Assert.All(burstResults, r =>
        {
            Assert.Equal(winningId, r.Id);
            Assert.Equal(new[] { m3.Id, m4.Id }, r.AttachedMedia.Select(a => a.Id));
        });
    }

    [Fact]
    public async Task AddWithMediaAsync_IdempotencyConflict_OnOrderOrSetOrContentOrTombstone_ThrowsConflictException()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv;
        Media m1;
        Media m2;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv = await SeedConversationAsync(db, userA, userB, now);
            m1 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Image, "1.png", "image/png", 500, now);
            m2 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Image, "2.png", "image/png", 600, now);
        }

        MessageId persistedMsgId;
        await using (var s1 = provider.CreateAsyncScope())
        {
            var repo = s1.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var msg = Message.Create(conv.Id, userA, "idem-conflict-media", "Original", now, hasAttachments: true);
            var res = await repo.AddWithMediaAsync(msg, [m1.Id, m2.Id], now);
            persistedMsgId = res.Id;
        }

        // Reversed media order -> ConflictException
        await using (var s2 = provider.CreateAsyncScope())
        {
            var repo = s2.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var m = Message.Create(conv.Id, userA, "idem-conflict-media", "Original", now, hasAttachments: true);
            await Assert.ThrowsAsync<ConflictException>(() => repo.AddWithMediaAsync(m, [m2.Id, m1.Id], now));
        }

        // Different subset -> ConflictException
        await using (var s3 = provider.CreateAsyncScope())
        {
            var repo = s3.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var m = Message.Create(conv.Id, userA, "idem-conflict-media", "Original", now, hasAttachments: true);
            await Assert.ThrowsAsync<ConflictException>(() => repo.AddWithMediaAsync(m, [m1.Id], now));
        }

        // Different caption -> ConflictException
        await using (var s4 = provider.CreateAsyncScope())
        {
            var repo = s4.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var m = Message.Create(conv.Id, userA, "idem-conflict-media", "Changed", now, hasAttachments: true);
            await Assert.ThrowsAsync<ConflictException>(() => repo.AddWithMediaAsync(m, [m1.Id, m2.Id], now));
        }

        // Tombstone then exact replay -> ConflictException
        await using (var s5 = provider.CreateAsyncScope())
        {
            var repo = s5.ServiceProvider.GetRequiredService<IMessagePersistence>();
            await repo.DeleteForEveryoneTransactionalAsync(conv.Id, persistedMsgId, now.AddMinutes(1));
        }

        await using (var s6 = provider.CreateAsyncScope())
        {
            var repo = s6.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var replay = Message.Create(conv.Id, userA, "idem-conflict-media", "Original", now, hasAttachments: true);
            await Assert.ThrowsAsync<ConflictException>(() => repo.AddWithMediaAsync(replay, [m1.Id, m2.Id], now));
        }
    }

    [Fact]
    public async Task AddWithMediaAsync_UnownedCrossConversationOrMissingMedia_ThrowsForbiddenAndRollsBackMessage()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var userC = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv1;
        Conversation conv2;
        Media ownedByUserBInConv1;
        Media ownedByUserAInConv2;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv1 = await SeedConversationAsync(db, userA, userB, now);
            conv2 = await SeedConversationAsync(db, userA, userC, now);
            ownedByUserBInConv1 = await SeedUploadedMediaAsync(db, userB, conv1.Id, MediaKind.Image, "b.png", "image/png", 500, now);
            ownedByUserAInConv2 = await SeedUploadedMediaAsync(db, userA, conv2.Id, MediaKind.Image, "c2.png", "image/png", 500, now);
        }

        await using var actScope = provider.CreateAsyncScope();
        var repo = actScope.ServiceProvider.GetRequiredService<IMessagePersistence>();

        await Assert.ThrowsAsync<ForbiddenException>(() => repo.AddWithMediaAsync(
            Message.Create(conv1.Id, userA, "idor-other-user", null, now, hasAttachments: true),
            [ownedByUserBInConv1.Id],
            now));

        await Assert.ThrowsAsync<ForbiddenException>(() => repo.AddWithMediaAsync(
            Message.Create(conv1.Id, userA, "idor-cross-conv", null, now, hasAttachments: true),
            [ownedByUserAInConv2.Id],
            now));

        await Assert.ThrowsAsync<ForbiddenException>(() => repo.AddWithMediaAsync(
            Message.Create(conv1.Id, userA, "idor-missing", null, now, hasAttachments: true),
            [MediaId.New()],
            now));

        await using var checkScope = provider.CreateAsyncScope();
        var checkDb = checkScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(0, await checkDb.Messages.CountAsync(m => m.ConversationId == conv1.Id));
    }

    [Fact]
    public async Task AddWithMediaAsync_PendingMediaOrConcurrentAttachRace_ThrowsConflictAndRollsBackCleanly()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv;
        Media pendingMedia;
        Media contestedUploadedMedia;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv = await SeedConversationAsync(db, userA, userB, now);
            pendingMedia = Media.CreatePending(userA, conv.Id, MediaKind.Image, "pending.png", "image/png", 500, now);
            db.Media.Add(pendingMedia);
            await db.SaveChangesAsync();

            contestedUploadedMedia = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Image, "shared.jpg", "image/jpeg", 900, now);
        }

        // Pending media cannot be attached
        await using (var s1 = provider.CreateAsyncScope())
        {
            var repo = s1.ServiceProvider.GetRequiredService<IMessagePersistence>();
            await Assert.ThrowsAsync<ConflictException>(() => repo.AddWithMediaAsync(
                Message.Create(conv.Id, userA, "attach-pending", null, now, hasAttachments: true),
                [pendingMedia.Id],
                now));
        }

        // Two distinct messages race to attach the same Uploaded MediaId
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raceTasks = new[] { "race-msg-1", "race-msg-2" }.Select(async key =>
        {
            await startGate.Task;
            await using var scope = provider.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IMessagePersistence>();
            try
            {
                await repo.AddWithMediaAsync(
                    Message.Create(conv.Id, userA, key, null, now, hasAttachments: true),
                    [contestedUploadedMedia.Id],
                    now);
                return true;
            }
            catch (ConflictException)
            {
                return false;
            }
        }).ToList();

        startGate.SetResult();
        var raceOutcomes = await Task.WhenAll(raceTasks);

        Assert.Equal(1, raceOutcomes.Count(ok => ok));
        Assert.Equal(1, raceOutcomes.Count(ok => !ok));

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(1, await verifyDb.Messages.CountAsync(m => m.ConversationId == conv.Id));
    }

    // =========================================================================
    // 4. DeleteForEveryoneTransactionalAsync & Batched Attached-Only Lookups
    // =========================================================================

    [Fact]
    public async Task DeleteForEveryoneTransactionalAsync_MarksMediaDeleted_AndExcludesFromAttachedLookups()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv;
        Media m1;
        Media m2;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv = await SeedConversationAsync(db, userA, userB, now);
            m1 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Image, "del1.jpg", "image/jpeg", 700, now);
            m2 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Document, "del2.pdf", "application/pdf", 800, now);
        }

        MessageId msgId;
        await using (var s1 = provider.CreateAsyncScope())
        {
            var msgRepo = s1.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var reactionRepo = s1.ServiceProvider.GetRequiredService<IReactionPersistence>();
            var persisted = await msgRepo.AddWithMediaAsync(
                Message.Create(conv.Id, userA, "del-with-media", "Will delete", now, hasAttachments: true),
                [m1.Id, m2.Id],
                now);
            msgId = persisted.Id;

            await reactionRepo.ToggleReactionAsync(conv.Id, msgId, userB, "❤️", now.AddSeconds(5));
        }

        // Before delete: GetByMessageIdsAsync returns both Attached media items
        await using (var sBefore = provider.CreateAsyncScope())
        {
            var mediaRepo = sBefore.ServiceProvider.GetRequiredService<IMediaPersistence>();
            var before = await mediaRepo.GetByMessageIdsAsync(conv.Id, [msgId]);
            Assert.True(before.ContainsKey(msgId));
            Assert.Equal(2, before[msgId].Count);
        }

        var deletedAt = now.AddMinutes(2);
        await using (var sDel = provider.CreateAsyncScope())
        {
            var msgRepo = sDel.ServiceProvider.GetRequiredService<IMessagePersistence>();
            await msgRepo.DeleteForEveryoneTransactionalAsync(conv.Id, msgId, deletedAt);
        }

        await using var verifyScope = provider.CreateAsyncScope();
        var dbVerify = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var mediaRepoVerify = verifyScope.ServiceProvider.GetRequiredService<IMediaPersistence>();

        var dbMsg = await dbVerify.Messages.AsNoTracking().SingleAsync(m => m.Id == msgId);
        Assert.True(dbMsg.IsDeletedForEveryone);
        Assert.Null(dbMsg.Content);

        var reactionCount = await dbVerify.MessageReactions.CountAsync(r => r.MessageId == msgId);
        Assert.Equal(0, reactionCount);

        var dbMedia = await dbVerify.Media.AsNoTracking().Where(m => m.MessageId == msgId).ToListAsync();
        Assert.Equal(2, dbMedia.Count);
        Assert.All(dbMedia, item =>
        {
            Assert.Equal(MediaState.Deleted, item.State);
            Assert.Equal(deletedAt, item.DeletedAtUtc);
        });

        // Must NOT leak deleted media from Attached-only read methods
        Assert.Empty(await mediaRepoVerify.GetByMessageIdsAsync(conv.Id, [msgId]));
        Assert.Empty(await mediaRepoVerify.GetByMessageIdsAcrossConversationsAsync([(conv.Id, msgId)]));
    }

    [Fact]
    public async Task GetByMessageIdsAcrossConversationsAsync_ReturnsExactConversationMessageMatchesOrderedByDisplayOrder()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var userC = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv1;
        Conversation conv2;
        Media m1;
        Media m2;
        Media m3;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv1 = await SeedConversationAsync(db, userA, userB, now);
            conv2 = await SeedConversationAsync(db, userA, userC, now);
            m1 = await SeedUploadedMediaAsync(db, userA, conv1.Id, MediaKind.Image, "c1a.png", "image/png", 100, now);
            m2 = await SeedUploadedMediaAsync(db, userA, conv1.Id, MediaKind.Image, "c1b.png", "image/png", 200, now);
            m3 = await SeedUploadedMediaAsync(db, userA, conv2.Id, MediaKind.Video, "c2a.mp4", "video/mp4", 300, now);
            // Unattached Uploaded row in conv1 must never appear
            await SeedUploadedMediaAsync(db, userA, conv1.Id, MediaKind.Document, "draft.pdf", "application/pdf", 400, now);
        }

        MessageId msg1Id;
        MessageId msg2Id;
        await using (var actScope = provider.CreateAsyncScope())
        {
            var msgRepo = actScope.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var r1 = await msgRepo.AddWithMediaAsync(
                Message.Create(conv1.Id, userA, "cross-c1", null, now, hasAttachments: true),
                [m2.Id, m1.Id],
                now);
            var r2 = await msgRepo.AddWithMediaAsync(
                Message.Create(conv2.Id, userA, "cross-c2", null, now, hasAttachments: true),
                [m3.Id],
                now);
            msg1Id = r1.Id;
            msg2Id = r2.Id;
        }

        await using var readScope = provider.CreateAsyncScope();
        var mediaRepo = readScope.ServiceProvider.GetRequiredService<IMediaPersistence>();

        // Also request a mismatched (conv2, msg1Id) pair to prove exact in-memory pair filtering
        var lookup = await mediaRepo.GetByMessageIdsAcrossConversationsAsync(
            [(conv1.Id, msg1Id), (conv2.Id, msg2Id), (conv2.Id, msg1Id)]);

        Assert.Equal(2, lookup.Count);
        Assert.Equal(new[] { m2.Id, m1.Id }, lookup[(conv1.Id, msg1Id)].Select(x => x.Id));
        Assert.Equal(new[] { m3.Id }, lookup[(conv2.Id, msg2Id)].Select(x => x.Id));
        Assert.False(lookup.ContainsKey((conv2.Id, msg1Id)));
    }

    [Fact]
    public async Task AddAsync_ReusedIdempotencyKeyAfterCaptionedMediaSend_ThrowsConflictException()
    {
        using var provider = _fixture.CreateServiceCollection().BuildServiceProvider();
        var userA = UserId.New();
        var userB = UserId.New();
        var now = DateTimeOffset.UtcNow;

        Conversation conv;
        Media m1;
        await using (var setupScope = provider.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            conv = await SeedConversationAsync(db, userA, userB, now);
            m1 = await SeedUploadedMediaAsync(db, userA, conv.Id, MediaKind.Image, "cap.png", "image/png", 1024, now);
        }

        await using (var sendScope = provider.CreateAsyncScope())
        {
            var msgRepo = sendScope.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var original = await msgRepo.AddWithMediaAsync(
                Message.Create(conv.Id, userA, "cross-mode-key", "hello", now, hasAttachments: true),
                [m1.Id],
                now);
            Assert.True(original.IsNewlyCreated);
        }

        await using (var retryScope = provider.CreateAsyncScope())
        {
            var msgRepo = retryScope.ServiceProvider.GetRequiredService<IMessagePersistence>();
            var conflictingTextOnly = Message.Create(conv.Id, userA, "cross-mode-key", "hello", now.AddSeconds(2), hasAttachments: false);
            await Assert.ThrowsAsync<ConflictException>(() => msgRepo.AddAsync(conflictingTextOnly));
        }
    }
}
