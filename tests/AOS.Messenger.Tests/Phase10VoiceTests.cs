using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Hubs;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase10VoiceTests : IClassFixture<SqlTestDatabaseFixture>, IDisposable
{
    private readonly SqlTestDatabaseFixture _fixture;
    private readonly string _mediaTempRoot;

    public Phase10VoiceTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _mediaTempRoot = Path.Combine(Path.GetTempPath(), $"aos_voice_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_mediaTempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_mediaTempRoot))
            {
                Directory.Delete(_mediaTempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    // =========================================================================
    // 1. Real SQL LocalDB Constraints, Standalone Enforcement & Concurrency
    // =========================================================================

    [Fact]
    public async Task SqlPersistence_EnforcesVoiceCheckConstraints_StandaloneRules_EditRejection_And_ConcurrentMarkPlayed()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 27, 14, 0, 0, TimeSpan.Zero));
        var captureNotifier = new CapturingVoiceRealtimeNotifier();

        UserId aliceId;
        UserId bobId;
        UserId outsiderId;
        ConversationId convId;

        await using (var setupDb = _fixture.CreateDbContext())
        {
            var alice = await SeedUserAsync(setupDb, "v_alice", "Alice");
            var bob = await SeedUserAsync(setupDb, "v_bob", "Bob");
            var outsider = await SeedUserAsync(setupDb, "v_out", "Outsider");
            aliceId = new UserId(alice.Id);
            bobId = new UserId(bob.Id);
            outsiderId = new UserId(outsider.Id);
            var conv = await SeedConversationAsync(setupDb, aliceId, bobId, clock.GetUtcNow());
            convId = conv.Id;
        }

        // Verify SQL check constraint rejection specifically on CK_Media_Voice_Duration, and confirm valid insert succeeds
        await using (var constraintDb = _fixture.CreateDbContext())
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() => constraintDb.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO [Media] ([Id], [StorageObjectId], [UploaderId], [ConversationId], [DisplayOrder], [Kind], [State], [ObjectKey], [SanitizedFileName], [DeclaredContentType], [DeclaredSizeBytes], [CreatedAtUtc], [DurationMs], [WaveformPeaks])
                   VALUES ({Guid.NewGuid()}, {MediaConstraints.GenerateStorageObjectId()}, {aliceId.Value}, {convId.Value}, {0}, {(int)MediaKind.Voice}, {(int)MediaState.Pending}, {$"pending/{MediaConstraints.GenerateStorageObjectId()}"}, {"bad.webm"}, {"audio/webm"}, {1024L}, {clock.GetUtcNow()}, {(int?)null}, {"10,20"})"));
            Assert.Contains("CK_Media_Voice_Duration", ex.Message, StringComparison.OrdinalIgnoreCase);

            var validInserted = await constraintDb.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO [Media] ([Id], [StorageObjectId], [UploaderId], [ConversationId], [DisplayOrder], [Kind], [State], [ObjectKey], [SanitizedFileName], [DeclaredContentType], [DeclaredSizeBytes], [CreatedAtUtc], [DurationMs], [WaveformPeaks])
                   VALUES ({Guid.NewGuid()}, {MediaConstraints.GenerateStorageObjectId()}, {aliceId.Value}, {convId.Value}, {0}, {(int)MediaKind.Voice}, {(int)MediaState.Pending}, {$"pending/{MediaConstraints.GenerateStorageObjectId()}"}, {"ok.webm"}, {"audio/webm"}, {1024L}, {clock.GetUtcNow()}, {1500}, {"10,20"})");
            Assert.Equal(1, validInserted);
        }

        await using var sp = BuildLocalServices(clock, captureNotifier);
        await using var scope = sp.CreateAsyncScope();
        var mediaSvc = scope.ServiceProvider.GetRequiredService<IMediaService>();
        var msgSvc = scope.ServiceProvider.GetRequiredService<IMessageService>();
        var storage = scope.ServiceProvider.GetRequiredService<IMediaStorage>();
        var blockSvc = scope.ServiceProvider.GetRequiredService<IBlockService>();

        // Upload valid WebM voice + normal image for negative standalone test
        var webmBytes = Phase10VoiceUnitTests.BuildWebmBytes("webm", trackType: 2);
        var initVoice = await mediaSvc.InitiateUploadAsync(
            aliceId,
            convId,
            new InitiateMediaUploadRequestDto("voice-note.webm", "audio/webm;codecs=opus", webmBytes.Length, "voice", 12500, [15, 45, 80, 60, 25]));

        await StageLocalFileAsync(initVoice.MediaId, webmBytes);
        var voiceDto = await mediaSvc.CompleteUploadAsync(aliceId, convId, initVoice.MediaId);
        Assert.Equal("voice", voiceDto.Kind);
        Assert.Equal(12500, voiceDto.DurationMs);
        Assert.Equal([15, 45, 80, 60, 25], voiceDto.WaveformPeaks);
        Assert.Null(voiceDto.PlayedAtUtc);

        // Standalone check 1: reject voice + text caption
        await Assert.ThrowsAsync<DomainException>(() => msgSvc.SendMessageAsync(
            aliceId,
            convId,
            new SendMessageRequestDto("Look at my voice note", Guid.NewGuid().ToString(), null, [voiceDto.Id])));

        // Standalone check 2: reject voice + second image attachment
        var pngBytes = BuildMinimalPngBytes();
        var initImg = await mediaSvc.InitiateUploadAsync(aliceId, convId, new InitiateMediaUploadRequestDto("pic.png", "image/png", pngBytes.Length));
        await StageLocalFileAsync(initImg.MediaId, pngBytes);
        var imgDto = await mediaSvc.CompleteUploadAsync(aliceId, convId, initImg.MediaId);

        await Assert.ThrowsAsync<DomainException>(() => msgSvc.SendMessageAsync(
            aliceId,
            convId,
            new SendMessageRequestDto(null, Guid.NewGuid().ToString(), null, [voiceDto.Id, imgDto.Id])));

        // Successful standalone voice message send
        var sentVoiceMsg = await msgSvc.SendMessageAsync(
            aliceId,
            convId,
            new SendMessageRequestDto(null, Guid.NewGuid().ToString(), null, [voiceDto.Id]));
        var voiceMsgId = sentVoiceMsg.Id;
        Assert.Single(sentVoiceMsg.Attachments!);
        Assert.Equal(voiceDto.Id, sentVoiceMsg.Attachments![0].Id);
        Assert.Equal([15, 45, 80, 60, 25], sentVoiceMsg.Attachments![0].WaveformPeaks);

        // Edit rejection even for null or whitespace content
        await Assert.ThrowsAsync<DomainException>(() => msgSvc.EditMessageAsync(
            aliceId, convId, voiceMsgId, new EditMessageRequestDto(null)));
        await Assert.ThrowsAsync<DomainException>(() => msgSvc.EditMessageAsync(
            aliceId, convId, voiceMsgId, new EditMessageRequestDto("Edited voice caption")));

        // Inline streaming URL check (never attachment disposition by default)
        var streamUrlDto = await mediaSvc.GetDownloadUrlAsync(bobId, convId, voiceMsgId, voiceDto.Id);
        Assert.False(string.IsNullOrWhiteSpace(streamUrlDto.Url));

        // Outsider / own-voice / blocked MarkVoicePlayed checks
        await Assert.ThrowsAsync<ForbiddenException>(() => mediaSvc.MarkVoicePlayedAsync(outsiderId, convId, voiceMsgId, voiceDto.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => mediaSvc.MarkVoicePlayedAsync(aliceId, convId, voiceMsgId, voiceDto.Id));

        await blockSvc.BlockUserAsync(aliceId, bobId);
        await Assert.ThrowsAsync<ForbiddenException>(() => mediaSvc.MarkVoicePlayedAsync(bobId, convId, voiceMsgId, voiceDto.Id));
        await blockSvc.UnblockUserAsync(aliceId, bobId);

        // Parallel concurrent MarkVoicePlayed race across tabs -> monotonic single broadcast
        clock.Advance(TimeSpan.FromSeconds(15));
        var raceTasks = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(async () =>
            {
                await using var raceScope = sp.CreateAsyncScope();
                var svc = raceScope.ServiceProvider.GetRequiredService<IMediaService>();
                return await svc.MarkVoicePlayedAsync(bobId, convId, voiceMsgId, voiceDto.Id);
            }))
            .ToArray();

        var raceResults = await Task.WhenAll(raceTasks);
        var expectedPlayedAt = raceResults[0].PlayedAtUtc;
        Assert.All(raceResults, r => Assert.Equal(expectedPlayedAt, r.PlayedAtUtc));
        Assert.Single(captureNotifier.VoicePlayedEvents);

        // Even after PlayedAtUtc is set, a newly blocked caller cannot obtain AlreadyPlayed
        await blockSvc.BlockUserAsync(aliceId, bobId);
        await using (var blockedDb = _fixture.CreateDbContext())
        {
            var blockedPersistence = new MediaPersistence(blockedDb);
            var blockedAttempt = await blockedPersistence.TryMarkVoicePlayedAsync(bobId, convId, voiceMsgId, voiceDto.Id, clock.GetUtcNow());
            Assert.Equal(MarkVoicePlayedStatus.ForbiddenBlocked, blockedAttempt.Status);
        }
        await blockSvc.UnblockUserAsync(aliceId, bobId);

        // Recipient DeleteForMe hides message -> atomic update & reload reject receipt without setting PlayedAtUtc
        var initVoice2 = await mediaSvc.InitiateUploadAsync(
            aliceId,
            convId,
            new InitiateMediaUploadRequestDto("voice-note-2.webm", "audio/webm;codecs=opus", webmBytes.Length, "voice", 5000, [10, 20, 30, 40]));
        await StageLocalFileAsync(initVoice2.MediaId, webmBytes);
        var voiceDto2 = await mediaSvc.CompleteUploadAsync(aliceId, convId, initVoice2.MediaId);
        var sentVoiceMsg2 = await msgSvc.SendMessageAsync(
            aliceId,
            convId,
            new SendMessageRequestDto(null, Guid.NewGuid().ToString(), null, [voiceDto2.Id]));
        await msgSvc.DeleteForMeAsync(bobId, convId, sentVoiceMsg2.Id);
        await using (var hiddenDb = _fixture.CreateDbContext())
        {
            var hiddenPersistence = new MediaPersistence(hiddenDb);
            var hiddenAttempt = await hiddenPersistence.TryMarkVoicePlayedAsync(bobId, convId, sentVoiceMsg2.Id, voiceDto2.Id, clock.GetUtcNow());
            Assert.Equal(MarkVoicePlayedStatus.NotFound, hiddenAttempt.Status);
            var dbMedia2 = await hiddenDb.Media.AsNoTracking().SingleAsync(m => m.Id == voiceDto2.Id);
            Assert.Null(dbMedia2.PlayedAtUtc);
        }

        // Delete for everyone scrubs cleanly without violating SQL check constraints
        clock.Advance(TimeSpan.FromMinutes(1));
        await msgSvc.DeleteForEveryoneAsync(aliceId, convId, voiceMsgId);
        await Assert.ThrowsAsync<NotFoundException>(() => mediaSvc.MarkVoicePlayedAsync(bobId, convId, voiceMsgId, voiceDto.Id));

        await using var checkDb = _fixture.CreateDbContext();
        var tombstonedMedia = await checkDb.Media.AsNoTracking().SingleAsync(m => m.Id == voiceDto.Id);
        Assert.Equal(MediaState.Deleted, tombstonedMedia.State);
        Assert.Equal("15,45,80,60,25", tombstonedMedia.WaveformPeaks);
        Assert.Equal(12500, tombstonedMedia.DurationMs);
        _ = storage;
    }

    // =========================================================================
    // 4. SignalR ChatHub & Block Suppression Tests
    // =========================================================================

    [Fact]
    public async Task ChatHub_SendRecordingIndicator_And_RealtimeNotifier_SuppressBlockedPeer()
    {
        var convId = ConversationId.New();
        var userA = UserId.New();
        var userB = UserId.New();

        var notifierMock = new CapturingVoiceRealtimeNotifier();
        var typingSvc = new FakeTypingService(userB);
        var tracker = new InMemoryConnectionTracker();
        var hub = new ChatHub(
            new FakeConversationService(),
            tracker,
            new StubCurrentUser(userA),
            NullLogger<ChatHub>.Instance,
            presenceService: null!,
            typingService: typingSvc,
            realtimeNotifier: notifierMock)
        {
            Context = new StubHubCallerContext("conn-rec-1")
        };

        await hub.SendRecordingIndicator(convId.Value, true);
        Assert.Single(notifierMock.RecordingEvents);
        Assert.Equal((userB, true), (notifierMock.RecordingEvents[0].Recipient, notifierMock.RecordingEvents[0].Dto.IsRecording));

        // Now test real SignalRRealtimeNotifier block boundary suppression
        await using var setupDb = _fixture.CreateDbContext();
        var u1 = await SeedUserAsync(setupDb, "sig_a", "SigA");
        var u2 = await SeedUserAsync(setupDb, "sig_b", "SigB");
        var id1 = new UserId(u1.Id);
        var id2 = new UserId(u2.Id);
        var realConv = await SeedConversationAsync(setupDb, id1, id2, DateTimeOffset.UtcNow);
        await using (var sp = BuildLocalServices(new TestTimeProvider(DateTimeOffset.UtcNow), notifierMock))
        {
            await using var scope = sp.CreateAsyncScope();
            var blockSvc = scope.ServiceProvider.GetRequiredService<IBlockService>();
            await blockSvc.BlockUserAsync(id1, id2);
        }

        var realClients = new CapturingChatClients();
        var realNotifier = new SignalRRealtimeNotifier(
            new CapturingHubContext(realClients),
            new ConversationPersistence(setupDb),
            new BlockPersistence(setupDb),
            NullLogger<SignalRRealtimeNotifier>.Instance);

        await realNotifier.NotifyRecordingAsync(id2, new RecordingIndicatorDto(realConv.Id.Value, id1.Value, true));
        Assert.Empty(realClients.SentRecordingIndicators);

        // When blocked, NotifyVoicePlayedAsync still notifies ONLY caller's own tabs (multi-tab sync), NEVER blocked uploader or "all"
        var playedDto = new VoicePlayedDto(realConv.Id.Value, 99, Guid.NewGuid(), id2.Value, DateTimeOffset.UtcNow);
        await realNotifier.NotifyVoicePlayedAsync(realConv.Id, id2, id1, playedDto);
        var target = Assert.Single(realClients.TargetedUsersForVoicePlayed);
        Assert.Equal(id2.Value.ToString(), target);
    }

    // =========================================================================
    // 3. End-to-End Web API Integration Test (CSRF + R2 LocalDisk + Played POST)
    // =========================================================================

    [Fact]
    public async Task WebApi_VoiceUpload_SendMessage_EditRejected_And_MarkPlayed_WorkEndToEnd()
    {
        Assert.Equal(
            "receipts",
            typeof(MediaController).GetMethod(nameof(MediaController.MarkVoicePlayed))!
                .GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);

        using var factory = CreateWebFactory();
        UserId aliceId;
        UserId bobId;
        ConversationId convId;
        string aliceUsername;
        string bobUsername;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var um = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var alice = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"va_{Guid.NewGuid():N}"[..16], Email = $"va_{Guid.NewGuid():N}@example.com", EmailConfirmed = true, DisplayName = "Voice Alice", CreatedAtUtc = DateTimeOffset.UtcNow };
            var bob = new ApplicationUser { Id = Guid.NewGuid(), UserName = $"vb_{Guid.NewGuid():N}"[..16], Email = $"vb_{Guid.NewGuid():N}@example.com", EmailConfirmed = true, DisplayName = "Voice Bob", CreatedAtUtc = DateTimeOffset.UtcNow };
            Assert.True((await um.CreateAsync(alice, "Password123!")).Succeeded);
            Assert.True((await um.CreateAsync(bob, "Password123!")).Succeeded);
            aliceUsername = alice.UserName!;
            bobUsername = bob.UserName!;
            aliceId = new UserId(alice.Id);
            bobId = new UserId(bob.Id);
            var conv = await SeedConversationAsync(db, aliceId, bobId, DateTimeOffset.UtcNow);
            convId = conv.Id;
        }

        using var aliceClient = CreateHttpsClient(factory);
        using var bobClient = CreateHttpsClient(factory);
        var aliceCsrf = await LoginAndGetCsrfAsync(aliceClient, aliceUsername);
        var bobCsrf = await LoginAndGetCsrfAsync(bobClient, bobUsername);

        var oggBytes = Phase10VoiceUnitTests.BuildOggBytes(Phase10VoiceUnitTests.BuildValidOpusHead(), bos: true);
        var initReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/media/initiate",
            aliceCsrf,
            new { fileName = "voice-message.ogg", contentType = "audio/ogg;codecs=opus", sizeBytes = oggBytes.Length, kind = "voice", durationMs = 6200, waveformPeaks = new[] { 10, 35, 70, 90, 40 } });
        var initResp = await aliceClient.SendAsync(initReq);
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var mediaId = Guid.Parse(initDoc.RootElement.GetProperty("mediaId").GetString()!);
        var uploadUrl = initDoc.RootElement.GetProperty("uploadUrl").GetString()!;
        var requiredMime = initDoc.RootElement.GetProperty("requiredContentType").GetString()!;
        Assert.Equal("voice", initDoc.RootElement.GetProperty("kind").GetString());

        var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl)
        {
            Content = new ByteArrayContent(oggBytes)
        };
        putReq.Content.Headers.ContentType = new MediaTypeHeaderValue(requiredMime);
        var putResp = await aliceClient.SendAsync(putReq);
        Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);

        var compResp = await aliceClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/media/{mediaId}/complete",
            aliceCsrf));
        Assert.Equal(HttpStatusCode.OK, compResp.StatusCode);

        // Standalone enforcement via HTTP API
        var captionedSend = await aliceClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/messages",
            aliceCsrf,
            new { content = "Caption", idempotencyKey = Guid.NewGuid().ToString(), mediaIds = new[] { mediaId } }));
        Assert.Equal(HttpStatusCode.BadRequest, captionedSend.StatusCode);

        var voiceSend = await aliceClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/messages",
            aliceCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString(), mediaIds = new[] { mediaId } }));
        Assert.Equal(HttpStatusCode.OK, voiceSend.StatusCode);
        using var sendDoc = JsonDocument.Parse(await voiceSend.Content.ReadAsStringAsync());
        var messageId = sendDoc.RootElement.GetProperty("id").GetInt64();

        // Edit attempt rejected with 400
        var editReq = new HttpRequestMessage(HttpMethod.Put, $"/api/conversations/{convId.Value}/messages/{messageId}")
        {
            Content = JsonContent.Create(new { content = "Changed" })
        };
        editReq.Headers.Add("RequestVerificationToken", aliceCsrf);
        editReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var editResp = await aliceClient.SendAsync(editReq);
        Assert.Equal(HttpStatusCode.BadRequest, editResp.StatusCode);

        // Uploader cannot mark own voice played -> 403
        var alicePlayed = await aliceClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/messages/{messageId}/media/{mediaId}/played",
            aliceCsrf));
        Assert.Equal(HttpStatusCode.Forbidden, alicePlayed.StatusCode);

        // Recipient marks voice played -> 200 OK + idempotent second call
        var bobPlayed1 = await bobClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/messages/{messageId}/media/{mediaId}/played",
            bobCsrf));
        Assert.Equal(HttpStatusCode.OK, bobPlayed1.StatusCode);
        using var playedDoc1 = JsonDocument.Parse(await bobPlayed1.Content.ReadAsStringAsync());
        var playedAt1 = playedDoc1.RootElement.GetProperty("playedAtUtc").GetString();
        Assert.False(string.IsNullOrWhiteSpace(playedAt1));

        var bobPlayed2 = await bobClient.SendAsync(CreateCsrfJsonPost(
            $"/api/conversations/{convId.Value}/messages/{messageId}/media/{mediaId}/played",
            bobCsrf));
        Assert.Equal(HttpStatusCode.OK, bobPlayed2.StatusCode);
        using var playedDoc2 = JsonDocument.Parse(await bobPlayed2.Content.ReadAsStringAsync());
        Assert.Equal(playedAt1, playedDoc2.RootElement.GetProperty("playedAtUtc").GetString());
    }

    // =========================================================================
    // Test Fixture Helpers & Fakes
    // =========================================================================

    private ServiceProvider BuildLocalServices(TestTimeProvider clock, IRealtimeNotifier notifier)
    {
        var services = _fixture.CreateServiceCollection();
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock));
        services.Replace(ServiceDescriptor.Singleton(notifier));
        services.Configure<MediaOptions>(o =>
        {
            o.MaxConcurrentPendingPerUser = 10;
            o.DailyUploadQuotaBytes = 50L * 1024 * 1024;
        });
        services.Replace(ServiceDescriptor.Singleton(sp =>
            new LocalDiskMediaStorage(sp.GetRequiredService<LocalMediaSigningService>(), _mediaTempRoot)));
        services.Replace(ServiceDescriptor.Singleton<IMediaStorage>(sp =>
            sp.GetRequiredService<LocalDiskMediaStorage>()));
        return services.BuildServiceProvider();
    }

    private async Task StageLocalFileAsync(MediaId mediaId, byte[] bytes)
    {
        await using var db = _fixture.CreateDbContext();
        var row = await db.Media.AsNoTracking().SingleAsync(m => m.Id == mediaId);
        var rel = row.ObjectKey.Replace('/', Path.DirectorySeparatorChar);
        var path = Path.Combine(_mediaTempRoot, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
    }

    private WebApplicationFactory<Program> CreateWebFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:DefaultConnection", _fixture.ConnectionString);
            builder.UseSetting("PublicBaseUrl", "https://localhost:7001");
            builder.UseSetting("Media:LocalStoragePath", _mediaTempRoot);
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                    ["PublicBaseUrl"] = "https://localhost:7001",
                    ["Media:LocalStoragePath"] = _mediaTempRoot,
                    ["R2:ServiceUrl"] = "",
                    ["R2:BucketName"] = "",
                    ["R2:AccessKeyId"] = "",
                    ["R2:SecretAccessKey"] = ""
                });
            });
            builder.ConfigureServices(services =>
            {
                var d = services.SingleOrDefault(x => x.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                if (d is not null) services.Remove(d);
                services.AddDbContext<ApplicationDbContext>(opts => opts.UseSqlServer(_fixture.ConnectionString));
            });
        });
    }

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task<string> LoginAndGetCsrfAsync(HttpClient client, string username)
    {
        var loginHtml = await client.GetStringAsync("/Account/Login");
        var token = ExtractCsrf(loginHtml);
        var resp = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserNameOrEmail"] = username,
            ["Password"] = "Password123!",
            ["RememberMe"] = "false",
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var chatHtml = await client.GetStringAsync("/Chat");
        return ExtractCsrf(chatHtml);
    }

    private static string ExtractCsrf(string html)
    {
        var m = Regex.Match(html, @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""", RegexOptions.IgnoreCase);
        Assert.True(m.Success);
        return m.Groups[1].Value;
    }

    private static HttpRequestMessage CreateCsrfJsonPost(string url, string csrf, object? body = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("RequestVerificationToken", csrf);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        if (body is not null) req.Content = JsonContent.Create(body);
        return req;
    }

    private static async Task<ApplicationUser> SeedUserAsync(ApplicationDbContext db, string prefix, string displayName)
    {
        var userName = $"{prefix}_{Guid.NewGuid():N}"[..16];
        var email = $"{userName}@example.com";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            DisplayName = displayName,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Conversation> SeedConversationAsync(ApplicationDbContext db, UserId u1, UserId u2, DateTimeOffset now)
    {
        var conv = Conversation.CreateDirect(u1, u2, now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv;
    }

    private static byte[] BuildMinimalPngBytes() =>
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00,
        0x1F, 0x15, 0xC4, 0x89,
        0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41, 0x54,
        0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00, 0x05, 0x00, 0x01,
        0x0D, 0x0A, 0x2D, 0xB4,
        0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44,
        0xAE, 0x42, 0x60, 0x82
    ];

    private sealed class TestTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class CapturingVoiceRealtimeNotifier : IRealtimeNotifier
    {
        public List<(UserId Recipient, RecordingIndicatorDto Dto)> RecordingEvents { get; } = [];
        public List<(ConversationId ConversationId, UserId Caller, UserId Uploader, VoicePlayedDto Dto)> VoicePlayedEvents { get; } = [];

        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyRecordingAsync(UserId recipientUserId, RecordingIndicatorDto recording, CancellationToken ct = default)
        {
            RecordingEvents.Add((recipientUserId, recording));
            return Task.CompletedTask;
        }
        public Task NotifyVoicePlayedAsync(ConversationId conversationId, UserId callerUserId, UserId uploaderUserId, VoicePlayedDto played, CancellationToken ct = default)
        {
            VoicePlayedEvents.Add((conversationId, callerUserId, uploaderUserId, played));
            return Task.CompletedTask;
        }
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapturingChatClient(List<string> recordingTargets, List<string> playedTargets, string userId) : IChatClient
    {
        public Task ReceiveMessage(MessageDto message) => Task.CompletedTask;
        public Task ReceiveReceiptUpdated(ReceiptUpdateDto receipt) => Task.CompletedTask;
        public Task ReceiveTypingIndicator(TypingIndicatorDto typing) => Task.CompletedTask;
        public Task ReceiveRecordingIndicator(RecordingIndicatorDto recording)
        {
            recordingTargets.Add(userId);
            return Task.CompletedTask;
        }
        public Task ReceiveVoicePlayed(VoicePlayedDto played)
        {
            playedTargets.Add(userId);
            return Task.CompletedTask;
        }
        public Task ReceivePresenceChanged(UserPresenceDto presence) => Task.CompletedTask;
        public Task ReceiveMessageEdited(MessageEditedDto message) => Task.CompletedTask;
        public Task ReceiveMessageDeletedForEveryone(MessageDeletedForEveryoneDto message) => Task.CompletedTask;
        public Task ReceiveMessageDeletedForMe(MessageDeletedForMeDto message) => Task.CompletedTask;
        public Task ReceiveReactionUpdated(ReactionUpdatedDto reaction) => Task.CompletedTask;
        public Task ReceiveUserProfileUpdated(UserProfileUpdatedDto profile) => Task.CompletedTask;
    }

    private sealed class CapturingChatClients : IHubClients<IChatClient>
    {
        public List<string> SentRecordingIndicators { get; } = [];
        public List<string> TargetedUsersForVoicePlayed { get; } = [];

        public IChatClient All => new CapturingChatClient(SentRecordingIndicators, TargetedUsersForVoicePlayed, "all");
        public IChatClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => All;
        public IChatClient Client(string connectionId) => All;
        public IChatClient Clients(IReadOnlyList<string> connectionIds) => All;
        public IChatClient Group(string groupName) => All;
        public IChatClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => All;
        public IChatClient Groups(IReadOnlyList<string> groupNames) => All;
        public IChatClient User(string userId) => new CapturingChatClient(SentRecordingIndicators, TargetedUsersForVoicePlayed, userId);
        public IChatClient Users(IReadOnlyList<string> userIds) => new MultiUserChatClient(SentRecordingIndicators, TargetedUsersForVoicePlayed, userIds);
    }

    private sealed class MultiUserChatClient(List<string> rec, List<string> played, IReadOnlyList<string> users) : IChatClient
    {
        public Task ReceiveMessage(MessageDto message) => Task.CompletedTask;
        public Task ReceiveReceiptUpdated(ReceiptUpdateDto receipt) => Task.CompletedTask;
        public Task ReceiveTypingIndicator(TypingIndicatorDto typing) => Task.CompletedTask;
        public Task ReceiveRecordingIndicator(RecordingIndicatorDto recording) { rec.AddRange(users); return Task.CompletedTask; }
        public Task ReceiveVoicePlayed(VoicePlayedDto dto) { played.AddRange(users); return Task.CompletedTask; }
        public Task ReceivePresenceChanged(UserPresenceDto presence) => Task.CompletedTask;
        public Task ReceiveMessageEdited(MessageEditedDto message) => Task.CompletedTask;
        public Task ReceiveMessageDeletedForEveryone(MessageDeletedForEveryoneDto message) => Task.CompletedTask;
        public Task ReceiveMessageDeletedForMe(MessageDeletedForMeDto message) => Task.CompletedTask;
        public Task ReceiveReactionUpdated(ReactionUpdatedDto reaction) => Task.CompletedTask;
        public Task ReceiveUserProfileUpdated(UserProfileUpdatedDto profile) => Task.CompletedTask;
    }

    private sealed class CapturingHubContext(CapturingChatClients clients) : IHubContext<ChatHub, IChatClient>
    {
        public IHubClients<IChatClient> Clients => clients;
        public IGroupManager Groups => throw new NotImplementedException();
    }

    private sealed class FakeTypingService(UserId recipient) : ITypingService
    {
        public Task<UserId?> ValidateAndGetTypingRecipientAsync(UserId callerId, Guid conversationId, CancellationToken ct = default) =>
            Task.FromResult<UserId?>(recipient);
    }

    private sealed class FakeConversationService : IConversationService
    {
        public Task<ConversationDetailDto> GetOrCreateConversationAsync(UserId currentUserId, UserId targetUserId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ConversationDetailDto> GetConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ConversationSummaryDto>> GetConversationListAsync(UserId currentUserId, bool includeArchived = false, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ConversationSummaryDto>>([]);
        public Task<bool> PinConversationAsync(UserId currentUserId, ConversationId conversationId, bool isPinned, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ArchiveConversationAsync(UserId currentUserId, ConversationId conversationId, bool isArchived, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> MuteConversationAsync(UserId currentUserId, ConversationId conversationId, bool isMuted, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> ClearChatAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> DeleteConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class StubCurrentUser(UserId id) : ICurrentUser
    {
        public UserId? Id => id;
        public string? UserName => "voice_tester";
        public bool IsAuthenticated => true;
    }

    private sealed class StubHubCallerContext(string connId) : HubCallerContext
    {
        public override string ConnectionId => connId;
        public override string? UserIdentifier => null;
        public override ClaimsPrincipal? User => null;
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }
}
