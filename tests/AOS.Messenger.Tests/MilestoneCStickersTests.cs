using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Storage;
using ImageMagick;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class MilestoneCStickersTests : IClassFixture<SqlTestDatabaseFixture>, IDisposable
{
    private readonly SqlTestDatabaseFixture _fixture;
    private readonly string _mediaTempRoot;

    public MilestoneCStickersTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _mediaTempRoot = Path.Combine(Path.GetTempPath(), $"aos_milestone_c_tests_{Guid.NewGuid():N}");
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
            // Best-effort temp cleanup
        }
    }

    private WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?>? extraSettings = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Development);
                builder.UseSetting("ConnectionStrings:DefaultConnection", _fixture.ConnectionString);
                builder.UseSetting("PublicBaseUrl", "https://localhost:7001");
                builder.UseSetting("Media:LocalStoragePath", _mediaTempRoot);

                if (extraSettings is not null)
                {
                    foreach (var kv in extraSettings)
                    {
                        builder.UseSetting(kv.Key, kv.Value);
                    }
                }

                builder.ConfigureAppConfiguration((_, config) =>
                {
                    var settings = new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                        ["PublicBaseUrl"] = "https://localhost:7001",
                        ["Media:LocalStoragePath"] = _mediaTempRoot,
                        ["R2:ServiceUrl"] = "",
                        ["R2:BucketName"] = "",
                        ["R2:AccessKeyId"] = "",
                        ["R2:SecretAccessKey"] = ""
                    };
                    if (extraSettings is not null)
                    {
                        foreach (var kv in extraSettings)
                        {
                            settings[kv.Key] = kv.Value;
                        }
                    }

                    config.AddInMemoryCollection(settings);
                });

                builder.ConfigureServices(services =>
                {
                    var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
                    if (descriptor is not null)
                    {
                        services.Remove(descriptor);
                    }

                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseSqlServer(_fixture.ConnectionString));
                });
            });
    }

    private static HttpClient CreateHttpsClient(WebApplicationFactory<Program> factory)
    {
        return factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });
    }

    private static async Task<ApplicationUser> SeedConfirmedUserAsync(
        IServiceProvider services,
        string prefix,
        string displayName,
        string password = "Password123!")
    {
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var username = $"{prefix}_{suffix}";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = username,
            Email = $"{username}@example.com",
            EmailConfirmed = true,
            DisplayName = displayName,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Expected __RequestVerificationToken hidden input in HTML.");
        return match.Groups[1].Value;
    }

    private static async Task<string> LoginAndGetCsrfTokenAsync(
        HttpClient client,
        string username,
        string password = "Password123!")
    {
        var loginPage = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        var loginToken = ExtractAntiforgeryToken(await loginPage.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserNameOrEmail"] = username,
            ["Password"] = password,
            ["RememberMe"] = "false",
            ["__RequestVerificationToken"] = loginToken
        });

        var loginResp = await client.PostAsync("/Account/Login", form);
        Assert.Equal(HttpStatusCode.Redirect, loginResp.StatusCode);

        var chatPage = await client.GetAsync("/Chat");
        Assert.Equal(HttpStatusCode.OK, chatPage.StatusCode);
        return ExtractAntiforgeryToken(await chatPage.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage CreateCsrfRequest(HttpMethod method, string url, string csrfToken, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Add("RequestVerificationToken", csrfToken);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }

        return req;
    }

    private static byte[] CreateTransparentPngBytes(uint width, uint height)
    {
        using var img = new MagickImage(MagickColors.Transparent, width, height);
        using var overlay = new MagickImage("gradient:#B3929D-#735761", width / 2, height / 2);
        img.Composite(overlay, Gravity.Center, CompositeOperator.Over);
        using var ms = new MemoryStream();
        img.Write(ms, MagickFormat.Png);
        return ms.ToArray();
    }

    private static byte[] CreateExifJpegBytes(uint width, uint height)
    {
        using var img = new MagickImage("gradient:#A07860-#F0E9DF", width, height);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "LeicaSecretModel");
        img.SetProfile(exif);
        img.Quality = 90;
        using var ms = new MemoryStream();
        img.Write(ms, MagickFormat.Jpeg);
        return ms.ToArray();
    }

    private static byte[] CreateAnimatedGifBytes(int frameCount, uint width, uint height, uint delayTicks = 10)
    {
        using var frames = new MagickImageCollection();
        for (var i = 0; i < frameCount; i++)
        {
            var r = (byte)((i * 37 + 40) % 255);
            var g = (byte)((i * 71 + 80) % 255);
            var b = (byte)((i * 19 + 140) % 255);
            var f = new MagickImage(new MagickColor(r, g, b), width, height)
            {
                AnimationDelay = delayTicks,
                AnimationTicksPerSecond = 100
            };
            frames.Add(f);
        }

        using var ms = new MemoryStream();
        frames.Write(ms, MagickFormat.Gif);
        return ms.ToArray();
    }

    private async Task<JsonElement> UploadCustomStickerThroughApiAsync(
        HttpClient client,
        string csrfToken,
        string fileName,
        string contentType,
        byte[] bytes,
        Guid? packId = null,
        string? emojiTags = null)
    {
        using var initReq = CreateCsrfRequest(
            HttpMethod.Post,
            "/api/stickers/uploads/initiate",
            csrfToken,
            new { packId, fileName, contentType, sizeBytes = bytes.Length, emojiTags });
        var initResp = await client.SendAsync(initReq);
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);

        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var stickerId = initDoc.RootElement.GetProperty("stickerId").GetString()!;
        var uploadUrl = initDoc.RootElement.GetProperty("uploadUrl").GetString()!;
        var requiredMime = initDoc.RootElement.GetProperty("requiredContentType").GetString()!;

        using (var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(bytes) })
        {
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue(requiredMime);
            var putResp = await client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        }

        using var compReq = CreateCsrfRequest(HttpMethod.Post, $"/api/stickers/uploads/{stickerId}/complete", csrfToken);
        var compResp = await client.SendAsync(compReq);
        Assert.Equal(HttpStatusCode.OK, compResp.StatusCode);

        using var compDoc = JsonDocument.Parse(await compResp.Content.ReadAsStringAsync());
        return compDoc.RootElement.Clone();
    }

    // =========================================================================
    // 1. No Built-In Starter Packs & Unified Emoji-Icon-Only Sticker Entry
    // =========================================================================

    [Fact]
    public async Task StickerCatalog_HasNoBuiltInStarterPacks_AndStickersAreAccessedOnlyInsideEmojiPicker()
    {
        var root = FindSolutionRoot();
        var starterDir = Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "stickers", "packs");
        Assert.False(Directory.Exists(starterDir), "Built-in starter sticker packs directory must be removed.");

        var conversationHtml = File.ReadAllText(Path.Combine(root, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml"));
        var emptyPaneHtml = File.ReadAllText(Path.Combine(root, "src", "AOS.Messenger.Web", "Views", "Chat", "_ChatActivePaneEmpty.cshtml"));
        Assert.DoesNotContain("composer-sticker-btn", conversationHtml);
        Assert.DoesNotContain("data-sticker-trigger", conversationHtml);
        Assert.DoesNotContain("composer-sticker-btn", emptyPaneHtml);
        Assert.DoesNotContain("data-sticker-trigger", emptyPaneHtml);
        Assert.Contains("id=\"composer-emoji-btn\"", conversationHtml);
        Assert.Contains("id=\"composer-emoji-btn\"", emptyPaneHtml);

        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);
        string username;
        using (var scope = factory.Services.CreateScope())
        {
            var user = await SeedConfirmedUserAsync(scope.ServiceProvider, "stk_empty", "Empty Sticker User");
            username = user.UserName!;
        }

        _ = await LoginAndGetCsrfTokenAsync(client, username);
        var catalogResp = await client.GetAsync("/api/stickers/catalog");
        Assert.Equal(HttpStatusCode.OK, catalogResp.StatusCode);
        using var catalogDoc = JsonDocument.Parse(await catalogResp.Content.ReadAsStringAsync());
        Assert.Equal(0, catalogDoc.RootElement.GetProperty("systemPacks").GetArrayLength());
        Assert.Equal(0, catalogDoc.RootElement.GetProperty("customPacks").GetArrayLength());
    }

    // =========================================================================
    // 2. Magick.NET StickerProcessor (Static, Animated, Clamping & Security)
    // =========================================================================

    [Fact]
    public async Task StickerProcessor_NormalizesStaticAndAnimated_StripsExifClampsDelaysAndRejectsAbuse()
    {
        var signing = new LocalMediaSigningService(new EphemeralDataProtectionProvider());
        var storage = new LocalDiskMediaStorage(signing, _mediaTempRoot);
        using var processor = new StickerProcessor(storage, Options.Create(new MediaOptions()));

        var packId = StickerPackId.New();
        var uploaderId = UserId.New();
        var now = DateTimeOffset.UtcNow;

        async Task<(Sticker Sticker, MediaObjectMetadata Meta)> StageAsync(string mime, byte[] data)
        {
            var sticker = Sticker.CreatePendingCustom(packId, uploaderId, mime, data.Length, "test", 1, now);
            var diskPath = Path.Combine(_mediaTempRoot, sticker.ObjectKey!.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
            await File.WriteAllBytesAsync(diskPath, data);
            var meta = await storage.GetObjectMetadataAsync(sticker.ObjectKey!);
            Assert.NotNull(meta);
            return (sticker, meta!);
        }

        // 1. Static EXIF JPEG -> 512x512 stripped WebP
        var jpegBytes = CreateExifJpegBytes(700, 420);
        var (jpegSticker, jpegMeta) = await StageAsync("image/jpeg", jpegBytes);
        await using (var res = await processor.ProcessStickerAsync(jpegSticker, jpegMeta))
        {
            Assert.Equal("image/webp", res.ContentType);
            Assert.False(res.IsAnimated);
            Assert.Equal(512, res.Width);
            Assert.Equal(512, res.Height);
            Assert.InRange(res.ProcessedSizeBytes, 100, StickerConstraints.MaxStaticWebpOutputBytes);

            using var outMs = new MemoryStream();
            await res.ProcessedStream.CopyToAsync(outMs);
            var bytes = outMs.ToArray();
            Assert.DoesNotContain("LeicaSecretModel", Encoding.ASCII.GetString(bytes));
            using var decoded = new MagickImage(bytes);
            Assert.Equal(MagickFormat.WebP, decoded.Format);
            Assert.Equal(512u, decoded.Width);
            Assert.Equal(512u, decoded.Height);
            Assert.Null(decoded.GetExifProfile());
        }

        // 2. Animated GIF with 0ms/10ms ultra-fast frames -> 512x512 animated WebP with clamped >= 40ms delays
        var fastGifBytes = CreateAnimatedGifBytes(frameCount: 4, width: 160, height: 120, delayTicks: 0);
        var (gifSticker, gifMeta) = await StageAsync("image/gif", fastGifBytes);
        await using (var res = await processor.ProcessStickerAsync(gifSticker, gifMeta))
        {
            Assert.Equal("image/webp", res.ContentType);
            Assert.True(res.IsAnimated);
            Assert.Equal(512, res.Width);
            Assert.Equal(512, res.Height);
            Assert.InRange(res.ProcessedSizeBytes, 100, StickerConstraints.MaxAnimatedWebpOutputBytes);

            using var outMs = new MemoryStream();
            await res.ProcessedStream.CopyToAsync(outMs);
            using var frames = new MagickImageCollection(outMs.ToArray());
            Assert.Equal(4, frames.Count);
            frames.Coalesce();
            Assert.All(frames, f =>
            {
                Assert.Equal(512u, f.Width);
                Assert.Equal(512u, f.Height);
                var delayMs = f.AnimationTicksPerSecond > 0
                    ? Math.Round(f.AnimationDelay * 1000.0 / f.AnimationTicksPerSecond)
                    : 0;
                Assert.True(delayMs >= StickerConstraints.MinFrameDelayMs, $"Expected >= 40ms frame delay, got {delayMs}ms.");
            });
        }

        // 3. Rejects GIF with > 120 frames (decompression/CPU DoS defense)
        var tooManyFramesGif = CreateAnimatedGifBytes(frameCount: 125, width: 16, height: 16, delayTicks: 5);
        var (bombSticker, bombMeta) = await StageAsync("image/gif", tooManyFramesGif);
        await Assert.ThrowsAsync<DomainException>(() => processor.ProcessStickerAsync(bombSticker, bombMeta));

        // 3b. Rejects large-canvas / tiny-frame Coalesce() decompression bomb BEFORE native allocation ([B5])
        foreach (var (w, h, count) in new (ushort, ushort, int)[]
        {
            (8192, 4028, 120),
            (2048, 2048, 120),
            (1024, 1024, 40)
        })
        {
            var coalesceBomb = BuildLargeCanvasTinyFrameGifBytes(w, h, count);
            var (cbSticker, cbMeta) = await StageAsync("image/gif", coalesceBomb);
            await Assert.ThrowsAsync<DomainException>(() => processor.ProcessStickerAsync(cbSticker, cbMeta));
        }

        // 4. Rejects disguised SVG bytes claiming to be PNG
        var svgBytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>");
        var (svgSticker, svgMeta) = await StageAsync("image/png", svgBytes);
        await Assert.ThrowsAsync<DomainException>(() => processor.ProcessStickerAsync(svgSticker, svgMeta));
    }

    [Fact]
    public async Task StickerUploadComplete_RejectsCoalesceBombWith400AndMarksStickerFailed()
    {
        using var factory = CreateFactory();
        using var aliceClient = CreateHttpsClient(factory);
        string aliceUsername;
        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "stk_bomb", "Bomb Test");
            aliceUsername = alice.UserName!;
        }

        var csrf = await LoginAndGetCsrfTokenAsync(aliceClient, aliceUsername);
        var bombBytes = BuildLargeCanvasTinyFrameGifBytes(canvasWidth: 2048, canvasHeight: 2048, frameCount: 120);

        using var initReq = CreateCsrfRequest(
            HttpMethod.Post,
            "/api/stickers/uploads/initiate",
            csrf,
            new { fileName = "bomb.gif", contentType = "image/gif", sizeBytes = bombBytes.Length, emojiTags = "💣" });
        var initResp = await aliceClient.SendAsync(initReq);
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);

        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var stickerId = Guid.Parse(initDoc.RootElement.GetProperty("stickerId").GetString()!);
        var uploadUrl = initDoc.RootElement.GetProperty("uploadUrl").GetString()!;

        using (var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(bombBytes) })
        {
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("image/gif");
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(putReq)).StatusCode);
        }

        using var compReq = CreateCsrfRequest(HttpMethod.Post, $"/api/stickers/uploads/{stickerId}/complete", csrf);
        var compResp = await aliceClient.SendAsync(compReq);
        Assert.Equal(HttpStatusCode.BadRequest, compResp.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(stickerId));
            Assert.Equal(StickerState.Failed, row.State);
            Assert.NotNull(row.PurgedAtUtc);
        }
    }

    private static byte[] BuildLargeCanvasTinyFrameGifBytes(ushort canvasWidth, ushort canvasHeight, int frameCount)
    {
        using var ms = new MemoryStream();
        ms.Write("GIF89a"u8);

        Span<byte> lsd = stackalloc byte[7];
        lsd[0] = (byte)(canvasWidth & 0xFF);
        lsd[1] = (byte)(canvasWidth >> 8);
        lsd[2] = (byte)(canvasHeight & 0xFF);
        lsd[3] = (byte)(canvasHeight >> 8);
        lsd[4] = 0x80; // Global Color Table present, 2 colors (6 bytes)
        lsd[5] = 0x00;
        lsd[6] = 0x00;
        ms.Write(lsd);
        ms.Write([0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF]);

        // 1x1 LZW image frame (valid minimal GIF raster stream)
        ReadOnlySpan<byte> tinyFrame =
        [
            0x2C,
            0x00, 0x00, 0x00, 0x00, // left, top
            0x01, 0x00, 0x01, 0x00, // width=1, height=1
            0x00,                   // packed
            0x02,                   // LZW minimum code size
            0x02, 0x4C, 0x01,       // sub-block
            0x00                    // block terminator
        ];
        for (var i = 0; i < frameCount; i++)
        {
            ms.Write(tinyFrame);
        }

        ms.WriteByte(0x3B);
        return ms.ToArray();
    }

    // =========================================================================
    // 3. Messaging Invariants, Idempotency, Edit Rejection, Tombstone & All 4 Preview Sites
    // =========================================================================

    [Fact]
    public async Task StickerMessaging_EnforcesMutualExclusionIdempotencyEditRejectionTombstoneAndAll4PreviewSites()
    {
        using var factory = CreateFactory();
        using var aliceClient = CreateHttpsClient(factory);
        using var bobClient = CreateHttpsClient(factory);

        Guid convId;
        string aliceUsername;
        string bobUsername;
        Guid starterSticker1Id;
        Guid starterSticker2Id;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "stk_m_a", "Alice Sticker");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "stk_m_b", "Bob Sticker");
            aliceUsername = alice.UserName!;
            bobUsername = bob.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        var aliceCsrf = await LoginAndGetCsrfTokenAsync(aliceClient, aliceUsername);
        var bobCsrf = await LoginAndGetCsrfTokenAsync(bobClient, bobUsername);

        var uploaded1 = await UploadCustomStickerThroughApiAsync(
            aliceClient,
            aliceCsrf,
            "st1.png",
            "image/png",
            CreateTransparentPngBytes(300, 300),
            null,
            "👋 hi");
        var uploaded2 = await UploadCustomStickerThroughApiAsync(
            aliceClient,
            aliceCsrf,
            "st2.png",
            "image/png",
            CreateTransparentPngBytes(320, 320),
            null,
            "🔥 lit");
        starterSticker1Id = Guid.Parse(uploaded1.GetProperty("id").GetString()!);
        starterSticker2Id = Guid.Parse(uploaded2.GetProperty("id").GetString()!);

        // Mutual exclusion: sticker + text rejected (400)
        using (var bothReq = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convId}/messages",
            aliceCsrf,
            new { content = "hello", idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = starterSticker1Id }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await aliceClient.SendAsync(bothReq)).StatusCode);
        }

        // Send sticker message cleanly
        var idemKey = Guid.NewGuid().ToString("N");
        long stickerMsgId;
        using (var sendReq = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convId}/messages",
            aliceCsrf,
            new { content = (string?)null, idempotencyKey = idemKey, stickerId = starterSticker1Id }))
        {
            var sendResp = await aliceClient.SendAsync(sendReq);
            Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);
            using var doc = JsonDocument.Parse(await sendResp.Content.ReadAsStringAsync());
            stickerMsgId = doc.RootElement.GetProperty("id").GetInt64();
            Assert.Equal(starterSticker1Id.ToString(), doc.RootElement.GetProperty("sticker").GetProperty("id").GetString());
        }

        // Idempotent replay with same stickerId returns exact same message ID
        using (var replayReq = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convId}/messages",
            aliceCsrf,
            new { content = (string?)null, idempotencyKey = idemKey, stickerId = starterSticker1Id }))
        {
            var replayResp = await aliceClient.SendAsync(replayReq);
            Assert.Equal(HttpStatusCode.OK, replayResp.StatusCode);
            using var doc = JsonDocument.Parse(await replayResp.Content.ReadAsStringAsync());
            Assert.Equal(stickerMsgId, doc.RootElement.GetProperty("id").GetInt64());
        }

        // Idempotency conflict: same key with DIFFERENT stickerId -> 409 Conflict
        using (var conflictReq = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convId}/messages",
            aliceCsrf,
            new { content = (string?)null, idempotencyKey = idemKey, stickerId = starterSticker2Id }))
        {
            Assert.Equal(HttpStatusCode.Conflict, (await aliceClient.SendAsync(conflictReq)).StatusCode);
        }

        // Editing a sticker message is forbidden by domain invariant -> 400 BadRequest
        using (var editReq = CreateCsrfRequest(
            HttpMethod.Put,
            $"/api/conversations/{convId}/messages/{stickerMsgId}",
            aliceCsrf,
            new { content = "nope" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await aliceClient.SendAsync(editReq)).StatusCode);
        }

        // PREVIEW SITE 1: ConversationService.GetConversationListAsync -> "🏷️ Sticker"
        var convListResp = await bobClient.GetAsync("/api/conversations");
        Assert.Equal(HttpStatusCode.OK, convListResp.StatusCode);
        using (var listDoc = JsonDocument.Parse(await convListResp.Content.ReadAsStringAsync()))
        {
            var item = listDoc.RootElement.EnumerateArray().Single(c => c.GetProperty("id").GetString() == convId.ToString());
            Assert.Equal(StickerConstraints.StickerPreviewText, item.GetProperty("lastMessagePreview").GetString());
        }

        // PREVIEW SITE 2: MessageService.SendMessageAsync (replying to a sticker) -> "🏷️ Sticker"
        long replyMsgId;
        using (var replyReq = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convId}/messages",
            bobCsrf,
            new { content = "Haha love that!", idempotencyKey = Guid.NewGuid().ToString("N"), replyToMessageId = stickerMsgId }))
        {
            var replyResp = await bobClient.SendAsync(replyReq);
            Assert.Equal(HttpStatusCode.OK, replyResp.StatusCode);
            using var doc = JsonDocument.Parse(await replyResp.Content.ReadAsStringAsync());
            replyMsgId = doc.RootElement.GetProperty("id").GetInt64();
            Assert.Equal(StickerConstraints.StickerPreviewText, doc.RootElement.GetProperty("replyTo").GetProperty("previewContent").GetString());
        }

        // PREVIEW SITE 3: MessageService.GetMessagesAsync (hydrated reply parent + sticker DTO) -> "🏷️ Sticker"
        var pageResp = await bobClient.GetAsync($"/api/conversations/{convId}/messages?pageSize=20");
        Assert.Equal(HttpStatusCode.OK, pageResp.StatusCode);
        using (var pageDoc = JsonDocument.Parse(await pageResp.Content.ReadAsStringAsync()))
        {
            var messages = pageDoc.RootElement.GetProperty("messages").EnumerateArray().ToList();
            var replyMsg = messages.Single(m => m.GetProperty("id").GetInt64() == replyMsgId);
            Assert.Equal(StickerConstraints.StickerPreviewText, replyMsg.GetProperty("replyTo").GetProperty("previewContent").GetString());
            var stickerMsg = messages.Single(m => m.GetProperty("id").GetInt64() == stickerMsgId);
            Assert.Equal(starterSticker1Id.ToString(), stickerMsg.GetProperty("sticker").GetProperty("id").GetString());
        }

        // PREVIEW SITE 4: MessageService.DeleteForMeAsync falls back to sticker preview -> "🏷️ Sticker"
        using (var delMeReq = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convId}/messages/{replyMsgId}/delete-for-me",
            bobCsrf))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await bobClient.SendAsync(delMeReq)).StatusCode);
        }

        var convListAfterDelMe = await bobClient.GetAsync("/api/conversations");
        Assert.Equal(HttpStatusCode.OK, convListAfterDelMe.StatusCode);
        using (var listDoc = JsonDocument.Parse(await convListAfterDelMe.Content.ReadAsStringAsync()))
        {
            var item = listDoc.RootElement.EnumerateArray().Single(c => c.GetProperty("id").GetString() == convId.ToString());
            Assert.Equal(StickerConstraints.StickerPreviewText, item.GetProperty("lastMessagePreview").GetString());
        }

        // DeleteForEveryone scrubs Message.StickerId = NULL while keeping reusable Sticker untouched
        using (var delEveryoneReq = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convId}/messages/{stickerMsgId}/delete-for-everyone",
            aliceCsrf))
        {
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(delEveryoneReq)).StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var msgRow = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == new MessageId(stickerMsgId));
            Assert.True(msgRow.IsDeletedForEveryone);
            Assert.Null(msgRow.StickerId);
            Assert.Null(msgRow.Content);

            var reusableSticker = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(starterSticker1Id));
            Assert.Equal(StickerState.Ready, reusableSticker.State);
        }
    }

    // =========================================================================
    // 4. Custom Stickers E2E: IDOR, Creator Privacy on Forwarding, Safe Archive & Cleanup
    // =========================================================================

    [Fact]
    public async Task CustomStickers_EnforcesIdorCreatorPrivacyForwardingSafeArchiveAndReferenceSafeCleanup()
    {
        using var factory = CreateFactory();
        using var aliceClient = CreateHttpsClient(factory);
        using var bobClient = CreateHttpsClient(factory);
        using var charlieClient = CreateHttpsClient(factory);

        Guid aliceId;
        Guid bobId;
        Guid charlieId;
        Guid convAbId;
        Guid convBcId;
        string aliceUsername;
        string bobUsername;
        string charlieUsername;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "cs_alice", "Alice SecretCreator");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "cs_bob", "Bob Receiver");
            var charlie = await SeedConfirmedUserAsync(scope.ServiceProvider, "cs_charlie", "Charlie Stranger");
            aliceId = alice.Id;
            bobId = bob.Id;
            charlieId = charlie.Id;
            aliceUsername = alice.UserName!;
            bobUsername = bob.UserName!;
            charlieUsername = charlie.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var convAb = Conversation.CreateDirect(new UserId(aliceId), new UserId(bobId), DateTimeOffset.UtcNow);
            var convBc = Conversation.CreateDirect(new UserId(bobId), new UserId(charlieId), DateTimeOffset.UtcNow);
            db.Conversations.AddRange(convAb, convBc);
            await db.SaveChangesAsync();
            convAbId = convAb.Id.Value;
            convBcId = convBc.Id.Value;
        }

        var aliceCsrf = await LoginAndGetCsrfTokenAsync(aliceClient, aliceUsername);
        var bobCsrf = await LoginAndGetCsrfTokenAsync(bobClient, bobUsername);
        var charlieCsrf = await LoginAndGetCsrfTokenAsync(charlieClient, charlieUsername);

        // 1. Alice creates a custom pack and uploads BOTH a static PNG and an animated GIF sticker
        Guid customPackId;
        using (var packReq = CreateCsrfRequest(HttpMethod.Post, "/api/stickers/packs", aliceCsrf, new { name = "Alice Pack" }))
        {
            var packResp = await aliceClient.SendAsync(packReq);
            Assert.Equal(HttpStatusCode.OK, packResp.StatusCode);
            using var packDoc = JsonDocument.Parse(await packResp.Content.ReadAsStringAsync());
            customPackId = Guid.Parse(packDoc.RootElement.GetProperty("id").GetString()!);
        }

        var staticCreated = await UploadCustomStickerThroughApiAsync(
            aliceClient,
            aliceCsrf,
            "cute.png",
            "image/png",
            CreateTransparentPngBytes(400, 300),
            customPackId,
            "😊 cute");
        var customStickerId = Guid.Parse(staticCreated.GetProperty("id").GetString()!);
        Assert.False(staticCreated.GetProperty("isAnimated").GetBoolean());
        Assert.True(staticCreated.GetProperty("isOwnedByCurrentUser").GetBoolean());

        var animCreated = await UploadCustomStickerThroughApiAsync(
            aliceClient,
            aliceCsrf,
            "dance.gif",
            "image/gif",
            CreateAnimatedGifBytes(3, 200, 200, delayTicks: 10),
            customPackId,
            "🎉 dance");
        var animStickerId = Guid.Parse(animCreated.GetProperty("id").GetString()!);
        Assert.True(animCreated.GetProperty("isAnimated").GetBoolean());

        string customObjectKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var row = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(customStickerId));
            Assert.Equal(StickerState.Ready, row.State);
            Assert.StartsWith("stickers/", row.ObjectKey);
            customObjectKey = row.ObjectKey!;
        }

        // 2. STRICT IDOR: Charlie (unrelated user) gets 404 on url, image, favorite, and send!
        Assert.Equal(HttpStatusCode.NotFound, (await charlieClient.GetAsync($"/api/stickers/{customStickerId}/url")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await charlieClient.GetAsync($"/api/stickers/{customStickerId}/image")).StatusCode);

        using (var favStranger = CreateCsrfRequest(
            HttpMethod.Post,
            "/api/stickers/favorites",
            charlieCsrf,
            new { stickerId = customStickerId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await charlieClient.SendAsync(favStranger)).StatusCode);
        }

        using (var sendStranger = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convBcId}/messages",
            charlieCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = customStickerId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await charlieClient.SendAsync(sendStranger)).StatusCode);
        }

        // 3. Alice sends her custom sticker to Bob in Conv AB
        long abMsgId;
        using (var sendAb = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convAbId}/messages",
            aliceCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = customStickerId }))
        {
            var resp = await aliceClient.SendAsync(sendAb);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            abMsgId = doc.RootElement.GetProperty("id").GetInt64();
        }

        // Non-owner Bob can view the received sticker, CANNOT re-send directly via chat history until saved to Favorites/Recents ([B2])
        var bobUrlResp = await bobClient.GetAsync($"/api/stickers/{customStickerId}/url");
        Assert.Equal(HttpStatusCode.OK, bobUrlResp.StatusCode);

        using (var directResendAttempt = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convBcId}/messages",
            bobCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = customStickerId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await bobClient.SendAsync(directResendAttempt)).StatusCode);
        }

        // Bob stars it and forwards it to Charlie — with neutral PackAuthor (Red-Team #5)!
        using (var favBob = CreateCsrfRequest(
            HttpMethod.Post,
            "/api/stickers/favorites",
            bobCsrf,
            new { stickerId = customStickerId, sourceConversationId = convAbId, sourceMessageId = abMsgId }))
        {
            var favResp = await bobClient.SendAsync(favBob);
            Assert.Equal(HttpStatusCode.OK, favResp.StatusCode);
            using var favDoc = JsonDocument.Parse(await favResp.Content.ReadAsStringAsync());
            Assert.Equal(StickerConstraints.NeutralCommunityAuthor, favDoc.RootElement.GetProperty("packAuthor").GetString());
            Assert.DoesNotContain("SecretCreator", await favResp.Content.ReadAsStringAsync());
        }

        long bcMsgId;
        using (var fwdBc = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convBcId}/messages",
            bobCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = customStickerId }))
        {
            var resp = await bobClient.SendAsync(fwdBc);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            bcMsgId = doc.RootElement.GetProperty("id").GetInt64();
            Assert.Equal(StickerConstraints.NeutralCommunityAuthor, doc.RootElement.GetProperty("sticker").GetProperty("packAuthor").GetString());
        }

        // Charlie can now view the forwarded sticker in Conv BC (NEVER sees Alice's name) and CANNOT re-send without favoriting ([B2])
        var charlieMsgs = await charlieClient.GetAsync($"/api/conversations/{convBcId}/messages?pageSize=20");
        Assert.Equal(HttpStatusCode.OK, charlieMsgs.StatusCode);
        var charlieJson = await charlieMsgs.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SecretCreator", charlieJson);
        Assert.Contains(StickerConstraints.NeutralCommunityAuthor, charlieJson);

        using (var charlieResendAttempt = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convBcId}/messages",
            charlieCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = customStickerId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await charlieClient.SendAsync(charlieResendAttempt)).StatusCode);
        }

        // 4. Alice deletes/archives her custom sticker -> pruned from ALL Favorites/Recents & blocked from re-sending, yet Bob & Charlie still see history!
        using (var delCustom = CreateCsrfRequest(HttpMethod.Delete, $"/api/stickers/{customStickerId}", aliceCsrf))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await aliceClient.SendAsync(delCustom)).StatusCode);
        }

        using (var aliceResendArchived = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convAbId}/messages",
            aliceCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = customStickerId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await aliceClient.SendAsync(aliceResendArchived)).StatusCode);
        }

        using (var bobResendArchived = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convBcId}/messages",
            bobCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = customStickerId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await bobClient.SendAsync(bobResendArchived)).StatusCode);
        }

        using (var bobRefavArchived = CreateCsrfRequest(
            HttpMethod.Post,
            "/api/stickers/favorites",
            bobCsrf,
            new { stickerId = customStickerId, sourceConversationId = convAbId, sourceMessageId = abMsgId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await bobClient.SendAsync(bobRefavArchived)).StatusCode);
        }

        foreach (var client in new[] { aliceClient, bobClient })
        {
            var catJson = await (await client.GetAsync("/api/stickers/catalog")).Content.ReadAsStringAsync();
            Assert.DoesNotContain(customStickerId.ToString(), catJson, StringComparison.OrdinalIgnoreCase);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sid = new StickerId(customStickerId);
            Assert.False(await db.UserFavoriteStickers.AnyAsync(f => f.StickerId == sid));
            Assert.False(await db.UserRecentStickers.AnyAsync(r => r.StickerId == sid));
        }

        var diskPath = Path.Combine(_mediaTempRoot, customObjectKey.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(diskPath));

        // Cleanup pass while active Messages reference the archived sticker MUST keep the R2 file!
        using (var scope = factory.Services.CreateScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
            await cleanup.RunCleanupPassAsync();
        }
        Assert.True(File.Exists(diskPath));
        Assert.Equal(HttpStatusCode.OK, (await charlieClient.GetAsync($"/api/stickers/{customStickerId}/url")).StatusCode);

        // Soft-deleting the custom pack preserves starred stickers (animStickerId) and previously sent
        // stickers in chat history (sentPackStickerId) so recipients can view/save/re-send them,
        // while pruning owner's un-starred recents and allowing cleanup of unreferenced stickers (unusedPackStickerId).
        var sentPackCreated = await UploadCustomStickerThroughApiAsync(
            aliceClient,
            aliceCsrf,
            "sent_pack.png",
            "image/png",
            CreateTransparentPngBytes(320, 320),
            customPackId,
            "🌟 sent");
        var sentPackStickerId = Guid.Parse(sentPackCreated.GetProperty("id").GetString()!);

        var unusedPackCreated = await UploadCustomStickerThroughApiAsync(
            aliceClient,
            aliceCsrf,
            "unused_pack.png",
            "image/png",
            CreateTransparentPngBytes(320, 320),
            customPackId,
            "🫥 unused");
        var unusedPackStickerId = Guid.Parse(unusedPackCreated.GetProperty("id").GetString()!);

        long abSentPackMsgId;
        using (var sendPackAb = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convAbId}/messages",
            aliceCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = sentPackStickerId }))
        {
            var resp = await aliceClient.SendAsync(sendPackAb);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            abSentPackMsgId = doc.RootElement.GetProperty("id").GetInt64();
        }

        string animObjectKey;
        string sentPackObjectKey;
        string unusedPackObjectKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            animObjectKey = (await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(animStickerId))).ObjectKey!;
            sentPackObjectKey = (await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(sentPackStickerId))).ObjectKey!;
            unusedPackObjectKey = (await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(unusedPackStickerId))).ObjectKey!;
        }

        using (var favAliceAnim = CreateCsrfRequest(
            HttpMethod.Post,
            "/api/stickers/favorites",
            aliceCsrf,
            new { stickerId = animStickerId }))
        {
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(favAliceAnim)).StatusCode);
        }

        using (var delPack = CreateCsrfRequest(HttpMethod.Delete, $"/api/stickers/packs/{customPackId}", aliceCsrf))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await aliceClient.SendAsync(delPack)).StatusCode);
        }

        // Alice's starred animStickerId stays in Favorites (before even re-sending) & remains re-sendable
        var aliceCatalogImmediateJson = await (await aliceClient.GetAsync("/api/stickers/catalog")).Content.ReadAsStringAsync();
        using (var immediateDoc = JsonDocument.Parse(aliceCatalogImmediateJson))
        {
            Assert.Contains(
                immediateDoc.RootElement.GetProperty("favorites").EnumerateArray(),
                el => el.GetProperty("id").GetGuid() == animStickerId && el.GetProperty("isFavorited").GetBoolean());
        }

        long abAnimMsgId;
        using (var sendAnimDeletedPack = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convAbId}/messages",
            aliceCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = animStickerId }))
        {
            var animResp = await aliceClient.SendAsync(sendAnimDeletedPack);
            Assert.Equal(HttpStatusCode.OK, animResp.StatusCode);
            using var animDoc = JsonDocument.Parse(await animResp.Content.ReadAsStringAsync());
            abAnimMsgId = animDoc.RootElement.GetProperty("id").GetInt64();
        }

        var aliceCatalogAfterPackDel = await (await aliceClient.GetAsync("/api/stickers/catalog")).Content.ReadAsStringAsync();
        Assert.Contains(animStickerId.ToString(), aliceCatalogAfterPackDel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sentPackStickerId.ToString(), aliceCatalogAfterPackDel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(unusedPackStickerId.ToString(), aliceCatalogAfterPackDel, StringComparison.OrdinalIgnoreCase);

        // Un-starred sentPackStickerId & unusedPackStickerId are no longer sendable by Alice after pack deletion
        using (var sendUnstarredPackSticker = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convAbId}/messages",
            aliceCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = sentPackStickerId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, (await aliceClient.SendAsync(sendUnstarredPackSticker)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await aliceClient.GetAsync($"/api/stickers/{unusedPackStickerId}/url")).StatusCode);

        // Bob (recipient of abSentPackMsgId) can still view, save to Favorites, see it in catalog Favorites, and re-send sentPackStickerId after pack deletion
        Assert.Equal(HttpStatusCode.OK, (await bobClient.GetAsync($"/api/stickers/{sentPackStickerId}/url")).StatusCode);
        using (var bobFavSentPack = CreateCsrfRequest(
            HttpMethod.Post,
            "/api/stickers/favorites",
            bobCsrf,
            new { stickerId = sentPackStickerId, sourceConversationId = convAbId, sourceMessageId = abSentPackMsgId }))
        {
            var resp = await bobClient.SendAsync(bobFavSentPack);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.DoesNotContain("SecretCreator", await resp.Content.ReadAsStringAsync());
        }

        var bobCatalogJson = await (await bobClient.GetAsync("/api/stickers/catalog")).Content.ReadAsStringAsync();
        using (var bobCatalogDoc = JsonDocument.Parse(bobCatalogJson))
        {
            Assert.Contains(
                bobCatalogDoc.RootElement.GetProperty("favorites").EnumerateArray(),
                el => el.GetProperty("id").GetGuid() == sentPackStickerId && el.GetProperty("isFavorited").GetBoolean());
        }

        long bcSentPackMsgId;
        using (var bobResendSentPack = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convBcId}/messages",
            bobCsrf,
            new { content = (string?)null, idempotencyKey = Guid.NewGuid().ToString("N"), stickerId = sentPackStickerId }))
        {
            var resp = await bobClient.SendAsync(bobResendSentPack);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            bcSentPackMsgId = doc.RootElement.GetProperty("id").GetInt64();
        }

        // Stranger Charlie still receives 404 for animStickerId
        Assert.Equal(HttpStatusCode.NotFound, (await charlieClient.GetAsync($"/api/stickers/{animStickerId}/url")).StatusCode);

        // Intermediate cleanup: unusedPackStickerId is purged, while animStickerId and sentPackStickerId are preserved
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
            await cleanup.RunCleanupPassAsync();

            var unusedRow = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(unusedPackStickerId));
            var animRow = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(animStickerId));
            var sentRow = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(sentPackStickerId));
            Assert.Equal(StickerState.Archived, unusedRow.State);
            Assert.NotNull(unusedRow.PurgedAtUtc);
            Assert.Null(animRow.PurgedAtUtc);
            Assert.Null(sentRow.PurgedAtUtc);
        }
        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, unusedPackObjectKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(_mediaTempRoot, animObjectKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(_mediaTempRoot, sentPackObjectKey.Replace('/', Path.DirectorySeparatorChar))));

        // 5. Once ALL messages and Favorites referencing stickers are cleared, cleanup purges them too
        using (var delAb = CreateCsrfRequest(HttpMethod.Post, $"/api/conversations/{convAbId}/messages/{abMsgId}/delete-for-everyone", aliceCsrf))
        {
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(delAb)).StatusCode);
        }
        using (var delBc = CreateCsrfRequest(HttpMethod.Post, $"/api/conversations/{convBcId}/messages/{bcMsgId}/delete-for-everyone", bobCsrf))
        {
            Assert.Equal(HttpStatusCode.OK, (await bobClient.SendAsync(delBc)).StatusCode);
        }
        using (var delAbAnim = CreateCsrfRequest(HttpMethod.Post, $"/api/conversations/{convAbId}/messages/{abAnimMsgId}/delete-for-everyone", aliceCsrf))
        {
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(delAbAnim)).StatusCode);
        }
        using (var delAbSentPack = CreateCsrfRequest(HttpMethod.Post, $"/api/conversations/{convAbId}/messages/{abSentPackMsgId}/delete-for-everyone", aliceCsrf))
        {
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(delAbSentPack)).StatusCode);
        }
        using (var delBcSentPack = CreateCsrfRequest(HttpMethod.Post, $"/api/conversations/{convBcId}/messages/{bcSentPackMsgId}/delete-for-everyone", bobCsrf))
        {
            Assert.Equal(HttpStatusCode.OK, (await bobClient.SendAsync(delBcSentPack)).StatusCode);
        }
        using (var unfavBob = CreateCsrfRequest(HttpMethod.Delete, $"/api/stickers/favorites/{customStickerId}", bobCsrf))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await bobClient.SendAsync(unfavBob)).StatusCode);
        }
        using (var unfavBobSentPack = CreateCsrfRequest(HttpMethod.Delete, $"/api/stickers/favorites/{sentPackStickerId}", bobCsrf))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await bobClient.SendAsync(unfavBobSentPack)).StatusCode);
        }

        // While Alice keeps animStickerId starred AND non-owner Bob keeps sentPackStickerId in recents (Step 0 Option A),
        // cleanup preserves both stickers' R2 binaries.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
            await cleanup.RunCleanupPassAsync();

            Assert.Null((await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(animStickerId))).PurgedAtUtc);
            Assert.Null((await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(sentPackStickerId))).PurgedAtUtc);
        }
        Assert.True(File.Exists(Path.Combine(_mediaTempRoot, animObjectKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(_mediaTempRoot, sentPackObjectKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(HttpStatusCode.OK, (await bobClient.GetAsync($"/api/stickers/{sentPackStickerId}/url")).StatusCode);

        // Once Bob's recent reference to sentPackStickerId is cleared, cleanup archives and purges sentPackStickerId cleanly.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.UserRecentStickers
                .Where(r => r.StickerId == new StickerId(sentPackStickerId))
                .ExecuteDeleteAsync();

            var cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
            await cleanup.RunCleanupPassAsync();

            Assert.Null((await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(animStickerId))).PurgedAtUtc);
            Assert.NotNull((await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(sentPackStickerId))).PurgedAtUtc);
        }
        Assert.True(File.Exists(Path.Combine(_mediaTempRoot, animObjectKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, sentPackObjectKey.Replace('/', Path.DirectorySeparatorChar))));

        // Once Alice un-favorites animStickerId as well, cleanup purges both customStickerId and animStickerId
        using (var unfavAliceAnim = CreateCsrfRequest(HttpMethod.Delete, $"/api/stickers/favorites/{animStickerId}", aliceCsrf))
        {
            Assert.Equal(HttpStatusCode.NoContent, (await aliceClient.SendAsync(unfavAliceAnim)).StatusCode);
        }
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
            await cleanup.RunCleanupPassAsync();

            var purgedCustom = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(customStickerId));
            var purgedAnim = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == new StickerId(animStickerId));
            Assert.NotNull(purgedCustom.PurgedAtUtc);
            Assert.NotNull(purgedAnim.PurgedAtUtc);
        }

        Assert.False(File.Exists(diskPath));
        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, animObjectKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    // =========================================================================
    // 5. Frontend Node.js ES Module Tests (Picker, Creator, Frameless Bubble & Contrast)
    // =========================================================================

    [Fact]
    public async Task FrontendModules_StickerPickerCreatorFramelessBubbleAndContrast_WorkAccuratelyInNode()
    {
        var root = FindSolutionRoot();
        string JsUrl(string name) => new Uri(Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", name)).AbsoluteUri;

        var script = $$"""
            import assert from 'node:assert/strict';

            const docListeners = new Map();
            function createClassList(el) {
                const getSet = () => new Set((el.className || '').split(/\s+/).filter(Boolean));
                const writeSet = (set) => { el.className = [...set].join(' '); };
                return {
                    add(...tokens) { const s = getSet(); tokens.forEach(t => s.add(t)); writeSet(s); },
                    remove(...tokens) { const s = getSet(); tokens.forEach(t => s.delete(t)); writeSet(s); },
                    toggle(token, force) {
                        const s = getSet();
                        const shouldAdd = force !== undefined ? !!force : !s.has(token);
                        if (shouldAdd) s.add(token); else s.delete(token);
                        writeSet(s);
                        return shouldAdd;
                    },
                    contains(token) { return getSet().has(token); }
                };
            }

            function matchesSingle(node, part) {
                if (!node || node.nodeType !== 1) return false;
                if (part.startsWith('#')) return node.id === part.slice(1);
                const attrMatch = /^([a-z0-9_.-]+)?((?:\[[^\]]+\])+)$/i.exec(part);
                if (attrMatch) {
                    const base = attrMatch[1] || '';
                    if (base) {
                        if (base.startsWith('.')) {
                            if (!node.classList.contains(base.slice(1))) return false;
                        } else if (node.tagName.toLowerCase() !== base.toLowerCase()) {
                            return false;
                        }
                    }
                    const attrs = [...attrMatch[2].matchAll(/\[([^=\]]+)(?:="([^"]*)")?\]/g)];
                    return attrs.every((m) => {
                        const val = node.getAttribute(m[1]);
                        return m[2] === undefined ? val !== null : val === m[2];
                    });
                }
                if (part.startsWith('.')) return node.classList.contains(part.slice(1));
                return node.tagName.toLowerCase() === part.toLowerCase();
            }

            function walk(node, predicate, out = []) {
                if (!node) return out;
                if (predicate(node)) out.push(node);
                for (const c of node.children || []) walk(c, predicate, out);
                return out;
            }

            function createEl(tagName) {
                const attrs = {};
                const listeners = new Map();
                const el = {
                    nodeType: 1,
                    tagName: String(tagName || 'div').toUpperCase(),
                    children: [],
                    parentNode: null,
                    style: {},
                    _id: '',
                    _className: '',
                    _src: '',
                    disabled: false,
                    value: '',
                    textContent: '',
                    get firstChild() { return this.children[0] || null; },
                    get id() { return this._id; },
                    set id(v) { this._id = String(v || ''); attrs.id = this._id; },
                    get className() { return this._className; },
                    set className(v) { this._className = String(v || ''); attrs.class = this._className; },
                    get src() { return this._src; },
                    set src(v) { this._src = String(v || ''); if (this._src) attrs.src = this._src; else delete attrs.src; },
                    appendChild(c) {
                        if (c && c.nodeType === 11) {
                            for (const kid of c.children.slice()) this.appendChild(kid);
                            c.children = [];
                            return c;
                        }
                        c.parentNode = this;
                        this.children.push(c);
                        return c;
                    },
                    insertBefore(c, ref) {
                        c.parentNode = this;
                        const idx = ref ? this.children.indexOf(ref) : -1;
                        if (idx >= 0) this.children.splice(idx, 0, c);
                        else this.children.push(c);
                        return c;
                    },
                    removeChild(c) { this.children = this.children.filter(x => x !== c); return c; },
                    remove() { if (this.parentNode) this.parentNode.removeChild(this); },
                    contains(node) {
                        let cur = node;
                        while (cur) { if (cur === this) return true; cur = cur.parentNode; }
                        return false;
                    },
                    setAttribute(k, v) {
                        if (k === 'id') this._id = String(v);
                        if (k === 'class') this._className = String(v);
                        if (k === 'src') this._src = String(v);
                        attrs[k] = String(v);
                    },
                    getAttribute(k) { return Object.prototype.hasOwnProperty.call(attrs, k) ? attrs[k] : null; },
                    hasAttribute(k) { return Object.prototype.hasOwnProperty.call(attrs, k); },
                    removeAttribute(k) {
                        if (k === 'src') this._src = '';
                        delete attrs[k];
                    },
                    querySelector(sel) { return this.querySelectorAll(sel)[0] || null; },
                    querySelectorAll(sel) {
                        const part = sel.trim().split(/\s+/).pop();
                        return walk(this, n => n !== this && matchesSingle(n, part));
                    },
                    closest(sel) {
                        let cur = this;
                        while (cur) { if (matchesSingle(cur, sel)) return cur; cur = cur.parentNode; }
                        return null;
                    },
                    addEventListener(type, fn) {
                        if (!listeners.has(type)) listeners.set(type, new Set());
                        listeners.get(type).add(fn);
                    },
                    removeEventListener(type, fn) { listeners.get(type)?.delete(fn); },
                    dispatchEvent(evt) {
                        evt.target = evt.target || this;
                        for (const fn of listeners.get(evt.type) || []) fn(evt);
                    },
                    focus() { document.activeElement = this; }
                };
                el.classList = createClassList(el);
                return el;
            }

            const body = createEl('body');
            globalThis.document = {
                body,
                activeElement: null,
                createElement: (t) => createEl(t),
                createDocumentFragment: () => ({ nodeType: 11, children: [], appendChild(c) { this.children.push(c); return c; } }),
                getElementById: (id) => walk(body, n => n.id === id)[0] || null,
                querySelector: (s) => walk(body, n => n !== body && matchesSingle(n, s))[0] || null,
                querySelectorAll: (s) => walk(body, n => n !== body && matchesSingle(n, s)),
                addEventListener(t, fn) { if (!docListeners.has(t)) docListeners.set(t, new Set()); docListeners.get(t).add(fn); },
                removeEventListener(t, fn) { docListeners.get(t)?.delete(fn); },
                dispatchEvent(e) { for (const fn of docListeners.get(e.type) || []) fn(e); }
            };
            globalThis.window = { innerWidth: 1280, innerHeight: 800, addEventListener() {}, removeEventListener() {} };

            const { createEmojiPicker } = await import('{{JsUrl("emoji-picker.js")}}');
            const { createStickerPicker, searchCatalogStickers, hydrateStickerImages } = await import('{{JsUrl("sticker-picker.js")}}');
            const { detectAnimatedImage, createStickerCreator } = await import('{{JsUrl("sticker-creator.js")}}');
            const {
                formatMessagePreview,
                createMessageBubbleElement,
                applyMessageDeletedForEveryone
            } = await import('{{JsUrl("message-renderer.js")}}');

            // 1. Frameless outgoing sticker rendering, contrast pills, no Edit button & tombstone restoration (Red-Team #16)
            const userId = '11111111-1111-1111-1111-111111111111';
            const convId = '22222222-2222-2222-2222-222222222222';
            const stickerId = '33333333-3333-3333-3333-333333333333';
            const stickerMsg = {
                id: 501,
                conversationId: convId,
                senderId: userId,
                senderName: 'Alice',
                senderHandle: 'alice',
                content: '',
                createdAtUtc: new Date().toISOString(),
                isOutgoing: true,
                reactions: [{ emoji: '🔥', count: 2, reactedByCurrentUser: false }],
                sticker: {
                    id: stickerId,
                    packId: '44444444-4444-4444-4444-444444444444',
                    packName: 'Warm Greetings',
                    packAuthor: 'A♡S Messenger',
                    isAnimated: false,
                    isSystem: true,
                    staticUrl: `/api/stickers/${stickerId}/image`,
                    width: 512,
                    height: 512,
                    emojiTags: '👋 hi hello',
                    isFavoritedByCurrentUser: false
                }
            };

            assert.equal(formatMessagePreview(stickerMsg), '🏷️ Sticker');

            const row = createMessageBubbleElement(stickerMsg, userId);
            body.appendChild(row);
            assert.equal(row.getAttribute('data-is-sticker'), 'true');
            assert.equal(row.querySelector('[data-action="edit"]'), null, 'Sticker message must NOT have an Edit button');
            assert.ok(row.querySelector('[data-sticker-info-trigger="true"]'));

            const stickerImg = row.querySelector('img[data-sticker-img="true"]');
            assert.ok(stickerImg);
            assert.equal(stickerImg.getAttribute('width'), '512');
            assert.equal(stickerImg.getAttribute('height'), '512');
            assert.equal(stickerImg.hasAttribute('onerror'), false);
            assert.equal(stickerImg.hasAttribute('crossorigin'), false);

            // Contrast pill on frameless outgoing sticker timestamp/receipts
            const footer = row.querySelector('.message-meta-footer');
            assert.ok(footer.className.includes('bg-black/55'), 'Frameless sticker footer must have bg-black/55 contrast pill');

            // Contrast on outgoing sticker reaction pills (must NOT use invisible text-white/90 on light cream bg)
            const reactionPill = row.querySelector('.reaction-pill');
            assert.ok(reactionPill);
            assert.ok(reactionPill.className.includes('bg-white'), 'Outgoing sticker reaction pills must use surface-contrasting bg-white');
            assert.equal(reactionPill.className.includes('text-white/90'), false);

            // DeleteForEveryone restores standard rose bubble shell & readable tombstone
            applyMessageDeletedForEveryone(row, 501);
            assert.equal(row.getAttribute('data-is-sticker'), 'false');
            assert.equal(row.querySelector('.message-sticker-container'), null);
            assert.ok(row.querySelector('.message-bubble').className.includes('bg-[#B3929D]'), 'Tombstoned outgoing sticker must restore rose bubble');
            const deletedTextContent = row.querySelector('.message-text-content').children.map(c => c.textContent).join(' ');
            assert.ok(deletedTextContent.includes('This message was deleted'));

            // 2. Unified [Emoji | Stickers] switcher visible on composer, hidden on edit-modal
            const sentStickers = [];
            const mockCatalog = {
                recents: [],
                favorites: [],
                systemPacks: [{
                    id: 'pack-1',
                    slug: 'aos-greetings',
                    name: 'Warm Greetings',
                    authorName: 'A♡S Messenger',
                    isSystemPack: true,
                    sortOrder: 1,
                    stickers: [{
                        id: stickerId,
                        packId: 'pack-1',
                        packName: 'Warm Greetings',
                        packAuthor: 'A♡S Messenger',
                        isAnimated: false,
                        isSystem: true,
                        isOwnedByCurrentUser: false,
                        staticUrl: `/api/stickers/${stickerId}/image`,
                        width: 512,
                        height: 512,
                        emojiTags: '👋 hi hello',
                        isFavorited: false
                    }]
                }],
                customPacks: []
            };

            assert.equal(searchCatalogStickers(mockCatalog, 'hello').length, 1);
            assert.equal(searchCatalogStickers(mockCatalog, 'nonexistent').length, 0);

            const stickerPicker = createStickerPicker({
                getCurrentUserId: () => userId,
                getCatalogFn: async () => mockCatalog,
                onSendSticker: (st) => sentStickers.push(st.id)
            });

            const unifiedPicker = createEmojiPicker({
                getUserId: () => userId,
                loadCatalog: async () => ({ categories: [{ id: 'smileys', label: 'Smileys', icon: 'mood' }], skinTones: [], entries: [], byCategory: new Map([[0, []]]) }),
                stickerPicker
            });

            const triggerBtn = createEl('button');
            const targetInput = createEl('textarea');
            body.appendChild(triggerBtn);
            body.appendChild(targetInput);

            await unifiedPicker.toggle(triggerBtn, targetInput, { surface: 'composer', initialMode: 'stickers' });
            assert.equal(unifiedPicker.getMode(), 'stickers');
            assert.equal(document.getElementById('picker-mode-switcher').classList.contains('hidden'), false);

            // Click sticker tile -> fires onSendSticker
            const tileBtn = document.querySelector(`[data-sticker-pick="${stickerId}"]`);
            assert.ok(tileBtn);
            tileBtn.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            assert.deepEqual(sentStickers, [stickerId]);

            // Clicking the single emoji button while in Stickers mode cleanly toggles the panel closed
            const toggledClosed = await unifiedPicker.toggle(triggerBtn, targetInput, { surface: 'composer', initialMode: 'emoji' });
            assert.equal(toggledClosed, false);
            assert.equal(unifiedPicker.isOpen(), false);

            // Empty catalog (no built-in packs, no custom packs) renders direct Create Sticker CTA
            let emptyCreatorOpened = false;
            const emptyStickerPicker = createStickerPicker({
                getActiveConversationId: () => convId,
                getCurrentUserId: () => userId,
                getCatalogFn: async () => ({ recents: [], favorites: [], systemPacks: [], customPacks: [] }),
                onOpenCreator: () => { emptyCreatorOpened = true; }
            });
            const emptyHost = createEl('div');
            emptyStickerPicker.mount(emptyHost);
            await emptyStickerPicker.ensureLoaded({ force: true });
            const emptyCta = emptyHost.querySelector('#sticker-picker-empty-create-btn');
            assert.ok(emptyCta, 'Empty sticker catalog must render a direct Create Sticker CTA button');
            emptyCta.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            assert.equal(emptyCreatorOpened, true);
            emptyStickerPicker.destroy();

            // Opening on edit-modal hides mode switcher & forces emoji mode
            const editBtn = createEl('button');
            const editInput = createEl('textarea');
            await unifiedPicker.toggle(editBtn, editInput, { surface: 'edit-modal', initialMode: 'stickers' });
            assert.equal(unifiedPicker.getMode(), 'emoji');
            assert.equal(document.getElementById('picker-mode-switcher').classList.contains('hidden'), true);

            // 3. Animated GIF / WebP binary detection
            const animWebpHeader = new Uint8Array([
                0x52, 0x49, 0x46, 0x46, 0x20, 0x00, 0x00, 0x00,
                0x57, 0x45, 0x42, 0x50,
                0x56, 0x50, 0x38, 0x58, 0x0A, 0x00, 0x00, 0x00,
                0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
            ]);
            const fakeAnimWebp = { name: 'a.webp', type: 'image/webp', size: animWebpHeader.length, slice: () => ({ arrayBuffer: async () => animWebpHeader.buffer }) };
            const fakeStaticPng = { name: 's.png', type: 'image/png', size: 10, slice: () => ({ arrayBuffer: async () => new Uint8Array(10).buffer }) };
            assert.equal(await detectAnimatedImage(fakeAnimWebp), true);
            assert.equal(await detectAnimatedImage(fakeStaticPng), false);

            unifiedPicker.destroy();
            stickerPicker.destroy();

            // 4. [W4] & [W17] Sticker picker pack deletion prunes recents/favorites, request tokens prevent stale overwrite, user switch clears isLoading, toggleFavorite serializes clicks
            let activeUserId = userId;
            let catSeq = 0;
            let resolveCat1, resolveCat2;
            const addFavCalls = [];
            let resolveAddFav;
            let deletedPackIds = [];
            const customPackId = '55555555-5555-5555-5555-555555555555';
            const customStickerId = '66666666-6666-6666-6666-666666666666';
            const customSticker = {
                id: customStickerId,
                packId: customPackId,
                packName: 'My Custom Pack',
                isAnimated: false,
                isSystem: false,
                isOwnedByCurrentUser: true,
                staticUrl: `/api/stickers/${customStickerId}/image`,
                width: 512,
                height: 512,
                emojiTags: 'wave',
                isFavorited: true
            };
            const pack2Only = {
                id: '77777777-7777-7777-7777-777777777777',
                name: 'Fresh Pack 2',
                isSystemPack: false,
                stickers: [customSticker]
            };

            const origConfirm = globalThis.window.confirm;
            let nativeConfirmCalls = 0;
            globalThis.window.confirm = () => { nativeConfirmCalls++; return false; };

            const racePicker = createStickerPicker({
                getCurrentUserId: () => activeUserId,
                getCatalogFn: () => {
                    catSeq++;
                    if (catSeq === 1) return new Promise((res) => { resolveCat1 = res; });
                    if (catSeq === 2) return new Promise((res) => { resolveCat2 = res; });
                    return Promise.resolve({ recents: [], favorites: [], systemPacks: [], customPacks: [pack2Only] });
                },
                addFavoriteFn: (payload) => {
                    addFavCalls.push(payload.stickerId);
                    return new Promise((res) => { resolveAddFav = res; });
                },
                deletePackFn: async (pid) => { deletedPackIds.push(pid); }
            });
            const raceHost = createEl('div');
            body.appendChild(raceHost);
            racePicker.mount(raceHost);

            const unstarredPackSticker = {
                ...customSticker,
                id: '66666666-6666-6666-6666-666666666667',
                staticUrl: '/api/stickers/66666666-6666-6666-6666-666666666667/image',
                emojiTags: 'wave2',
                isFavorited: false
            };
            const load1 = racePicker.ensureLoaded();
            const load2 = racePicker.ensureLoaded({ force: true });
            await new Promise(r => setTimeout(r, 10));
            resolveCat2({
                recents: [customSticker, unstarredPackSticker],
                favorites: [customSticker],
                systemPacks: [],
                customPacks: [{ id: customPackId, name: 'My Custom Pack', isSystemPack: false, stickers: [customSticker, unstarredPackSticker] }]
            });
            await load2;
            resolveCat1({
                recents: [],
                favorites: [],
                systemPacks: [],
                customPacks: [{ id: 'stale-pack', name: 'Stale Pack', isSystemPack: false, stickers: [] }]
            });
            await load1;
            assert.deepEqual(racePicker.getCustomPacks().map(p => p.id), [customPackId], 'Older ensureLoaded response must not overwrite newer force reload');

            // Deleting active custom pack prunes un-starred recents while preserving starred stickers (Option A)
            const packTab = raceHost.querySelectorAll('button').find(b => b.getAttribute('data-sticker-tab') === `pack:${customPackId}`);
            assert.ok(packTab);
            packTab.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            const delPackBtn = raceHost.querySelectorAll('button').find(b => b.getAttribute('aria-label') === 'Delete custom pack');
            assert.ok(delPackBtn);
            assert.equal(delPackBtn.onclick, undefined, '[S1] Delete pack button must use single delegated click listener instead of .onclick');
            delPackBtn.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            assert.equal(nativeConfirmCalls, 0, 'Pack deletion must never invoke native window.confirm');
            assert.deepEqual(deletedPackIds, [], 'Pack deletion must wait for custom modal confirmation');
            const confirmModal = document.getElementById('sticker-confirm-modal');
            assert.ok(confirmModal && !confirmModal.classList.contains('hidden'), 'Custom confirmation modal must open');
            const cancelConfirmBtn = document.getElementById('sticker-confirm-cancel-btn');
            assert.ok(cancelConfirmBtn);
            document.dispatchEvent({ type: 'click', target: cancelConfirmBtn, preventDefault() {}, stopPropagation() {} });
            assert.equal(confirmModal.classList.contains('hidden'), true);
            assert.deepEqual(deletedPackIds, []);

            delPackBtn.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            const acceptConfirmBtn = document.getElementById('sticker-confirm-accept-btn');
            assert.ok(acceptConfirmBtn);
            document.dispatchEvent({ type: 'click', target: acceptConfirmBtn, preventDefault() {}, stopPropagation() {} });
            await new Promise(r => setTimeout(r, 15));
            assert.equal(nativeConfirmCalls, 0, 'Confirming deletion must never invoke native window.confirm');
            assert.deepEqual(deletedPackIds, [customPackId]);
            assert.equal(confirmModal.classList.contains('hidden'), true);
            const recentsTab = raceHost.querySelectorAll('button').find(b => b.getAttribute('data-sticker-tab') === 'recents');
            recentsTab.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            assert.equal(raceHost.querySelector(`[data-sticker-pick="${unstarredPackSticker.id}"]`), null, 'Un-starred deleted pack sticker must be pruned from recents');
            assert.ok(raceHost.querySelector(`[data-sticker-pick="${customStickerId}"]`), 'Starred sticker from deleted pack must remain in recents');
            const favTab = raceHost.querySelectorAll('button').find(b => b.getAttribute('data-sticker-tab') === 'favorites');
            favTab.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            assert.ok(raceHost.querySelector(`[data-sticker-pick="${customStickerId}"]`), 'Starred sticker from deleted pack must remain in favorites');

            // Force reload a non-favorited sticker and double-click its star button concurrently
            let resolveStar;
            const starSticker = { ...customSticker, id: '88888888-8888-8888-8888-888888888888', isFavorited: false };
            const starPicker = createStickerPicker({
                getCurrentUserId: () => activeUserId,
                getCatalogFn: async () => ({ recents: [starSticker], favorites: [], systemPacks: [], customPacks: [] }),
                addFavoriteFn: (payload) => {
                    addFavCalls.push(payload.stickerId);
                    return new Promise((res) => { resolveStar = res; });
                }
            });
            const starHost = createEl('div');
            body.appendChild(starHost);
            starPicker.mount(starHost);
            await starPicker.ensureLoaded({ force: true });
            const starBtn = starHost.querySelectorAll('button').find(b => b.getAttribute('data-sticker-star') === starSticker.id);
            assert.ok(starBtn);
            starBtn.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            assert.equal(starBtn.disabled, true, 'Star button must disable while favorite mutation is in flight');
            starBtn.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            assert.equal(addFavCalls.length, 1, 'In-flight favorite toggle must serialize duplicate clicks');
            resolveStar({ ...starSticker, isFavorited: true });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(starSticker.id && starPicker.isFavorited(starSticker.id), true);

            // Switching user mid-fetch must not leave sticker picker stuck in isLoading=true
            let resolveSwitchCat;
            const switchPicker = createStickerPicker({
                getCurrentUserId: () => activeUserId,
                getCatalogFn: () => new Promise((res) => { resolveSwitchCat = res; })
            });
            const switchHost = createEl('div');
            body.appendChild(switchHost);
            switchPicker.mount(switchHost);
            const switchPromise = switchPicker.ensureLoaded();
            await new Promise(r => setTimeout(r, 10));
            activeUserId = '99999999-9999-9999-9999-999999999999';
            resolveSwitchCat({ recents: [], favorites: [], systemPacks: [], customPacks: [] });
            assert.equal(await switchPromise, null);
            assert.equal(switchHost.textContent?.includes('Loading your sticker packs...') || false, false);

            // [W17] emoji-picker toggle closed during stickerPicker.ensureLoaded skips loadCatalog()
            let emojiLoadCalls = 0;
            let resolveDeferredEnsure;
            const deferredEmojiPicker = createEmojiPicker({
                getUserId: () => activeUserId,
                loadCatalog: async () => {
                    emojiLoadCalls++;
                    return { categories: [{ id: 'smileys', label: 'Smileys', icon: 'mood' }], skinTones: [], entries: [], byCategory: new Map([[0, []]]) };
                },
                stickerPicker: {
                    mount() {},
                    ensureLoaded: () => new Promise((res) => { resolveDeferredEnsure = res; })
                }
            });
            const openPromise = deferredEmojiPicker.toggle(triggerBtn, targetInput, { surface: 'composer', initialMode: 'stickers' });
            deferredEmojiPicker.close();
            resolveDeferredEnsure(null);
            assert.equal(await openPromise, false);
            assert.equal(emojiLoadCalls, 0, 'Closed emoji picker must not call loadCatalog after stickerPicker.ensureLoaded resolves');

            // [W15] sticker-creator.js decode-vs-close, AbortController upload cancellation & pointercancel
            const scModal = createEl('div'); scModal.id = 'sticker-creator-modal'; scModal.className = 'hidden';
            const scCloseBtn = createEl('button'); scCloseBtn.id = 'sticker-creator-close-btn';
            const scCancelBtn = createEl('button'); scCancelBtn.id = 'sticker-creator-cancel-btn';
            const scDropzone = createEl('div'); scDropzone.id = 'sticker-creator-dropzone';
            const scFileInput = createEl('input'); scFileInput.id = 'sticker-creator-file-input';
            const scPickBtn = createEl('button'); scPickBtn.id = 'sticker-creator-pick-btn';
            const scPreviewArea = createEl('div'); scPreviewArea.id = 'sticker-creator-preview-area'; scPreviewArea.className = 'hidden';
            const scCanvas = createEl('canvas'); scCanvas.id = 'sticker-creator-canvas';
            const canvasDrawCalls = [];
            let capturedPointerId = null;
            let releasedPointerId = null;
            scCanvas.getContext = () => ({
                clearRect() {},
                save() {},
                restore() {},
                beginPath() {},
                arc() {},
                clip() {},
                drawImage() { canvasDrawCalls.push('draw'); }
            });
            scCanvas.toBlob = (cb) => cb({ size: 128, type: 'image/webp' });
            scCanvas.getBoundingClientRect = () => ({ width: 220 });
            scCanvas.setPointerCapture = (pid) => { capturedPointerId = pid; };
            scCanvas.releasePointerCapture = (pid) => { releasedPointerId = pid; };
            const scAnimImg = createEl('img'); scAnimImg.id = 'sticker-creator-anim-preview';
            const scAnimBadge = createEl('span'); scAnimBadge.id = 'sticker-creator-anim-badge';
            const scStaticControls = createEl('div'); scStaticControls.id = 'sticker-creator-static-controls';
            const scZoomSlider = createEl('input'); scZoomSlider.id = 'sticker-creator-zoom';
            const scChangeBtn = createEl('button'); scChangeBtn.id = 'sticker-creator-change-file-btn';
            const scPackSelect = createEl('select'); scPackSelect.id = 'sticker-creator-pack-select';
            const scNewPackBtn = createEl('button'); scNewPackBtn.id = 'sticker-creator-new-pack-btn';
            const scNewPackRow = createEl('div'); scNewPackRow.id = 'sticker-creator-new-pack-row';
            const scNewPackInput = createEl('input'); scNewPackInput.id = 'sticker-creator-new-pack-input';
            const scNewPackSave = createEl('button'); scNewPackSave.id = 'sticker-creator-new-pack-save-btn';
            const scTagsInput = createEl('input'); scTagsInput.id = 'sticker-creator-tags-input';
            const scProgressWrap = createEl('div'); scProgressWrap.id = 'sticker-creator-progress-wrap';
            const scProgressBar = createEl('div'); scProgressBar.id = 'sticker-creator-progress-bar';
            const scProgressLabel = createEl('span'); scProgressLabel.id = 'sticker-creator-progress-label';
            const scErrorEl = createEl('p'); scErrorEl.id = 'sticker-creator-error';
            const scSubmitBtn = createEl('button'); scSubmitBtn.id = 'sticker-creator-submit-btn';
            for (const kid of [scCloseBtn, scCancelBtn, scDropzone, scFileInput, scPickBtn, scPreviewArea, scCanvas, scAnimImg, scAnimBadge, scStaticControls, scZoomSlider, scChangeBtn, scPackSelect, scNewPackBtn, scNewPackRow, scNewPackInput, scNewPackSave, scTagsInput, scProgressWrap, scProgressBar, scProgressLabel, scErrorEl, scSubmitBtn]) {
                scModal.appendChild(kid);
            }
            body.appendChild(scModal);

            let resolveCreatorBmp;
            let closedCreatorBmps = 0;
            const origCreatorBitmap = globalThis.createImageBitmap;
            globalThis.createImageBitmap = () => new Promise((res) => { resolveCreatorBmp = res; });

            let scInitSignals = [];
            let scCancelCalls = [];
            let scPutCalls = 0;
            let scCompleteCalls = 0;
            let scCreatedCalls = 0;
            let resolveScInit;

            const creator = createStickerCreator({
                onCreated: () => { scCreatedCalls++; },
                initiateUploadFn: (_payload, { signal } = {}) => {
                    scInitSignals.push(signal);
                    return new Promise((res) => { resolveScInit = res; });
                },
                uploadPutFn: async () => { scPutCalls++; },
                completeUploadFn: async () => { scCompleteCalls++; return {}; },
                cancelUploadFn: async (sid) => { scCancelCalls.push(sid); }
            });

            // Decode-vs-close closes late ImageBitmap and keeps modal closed
            creator.open({ customPacks: [] });
            const pngFile = { name: 'stick.png', type: 'image/png', size: 512, slice: () => ({ arrayBuffer: async () => new Uint8Array(10).buffer }) };
            scFileInput.files = [pngFile];
            document.dispatchEvent({ type: 'change', target: scFileInput });
            await new Promise(r => setTimeout(r, 10));
            creator.close();
            resolveCreatorBmp({ width: 256, height: 256, close() { closedCreatorBmps++; } });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(closedCreatorBmps, 1);
            assert.equal(creator.isOpen(), false);
            assert.equal(scPreviewArea.classList.contains('hidden'), true);

            // Re-open with instant bitmap -> verify setPointerCapture + pointercancel stops drag -> cancel mid-initiate
            globalThis.createImageBitmap = async () => ({ width: 256, height: 256, close() { closedCreatorBmps++; } });
            creator.open({ customPacks: [] });
            scFileInput.files = [pngFile];
            document.dispatchEvent({ type: 'change', target: scFileInput });
            await new Promise(r => setTimeout(r, 15));
            const drawsAfterLoad = canvasDrawCalls.length;
            document.dispatchEvent({ type: 'pointerdown', target: scCanvas, clientX: 10, clientY: 10, pointerId: 7 });
            assert.equal(capturedPointerId, 7);
            document.dispatchEvent({ type: 'pointercancel', target: scCanvas, pointerId: 7 });
            assert.equal(releasedPointerId, 7);
            document.dispatchEvent({ type: 'pointermove', target: scCanvas, clientX: 40, clientY: 40, pointerId: 7 });
            assert.equal(canvasDrawCalls.length, drawsAfterLoad, 'pointercancel must stop active canvas drag');

            let finishOldDecode;
            let finishReplacementDecode;
            globalThis.createImageBitmap = (file) => new Promise(resolve => {
                if (file === pngFile) finishOldDecode = resolve;
                else finishReplacementDecode = resolve;
            });
            document.dispatchEvent({ type: 'change', target: scFileInput });
            await Promise.resolve();
            scFileInput.files = [{ ...pngFile, name: 'replacement.png' }];
            document.dispatchEvent({ type: 'change', target: scFileInput });
            await Promise.resolve();
            assert.equal(scSubmitBtn.disabled, true);
            finishOldDecode({ width: 256, height: 256, close() {} });
            await Promise.resolve();
            document.dispatchEvent({ type: 'click', target: scSubmitBtn, preventDefault() {} });
            assert.equal(scInitSignals.length, 0);
            assert.equal(scSubmitBtn.disabled, true);
            finishReplacementDecode({ width: 256, height: 256, close() {} });
            await Promise.resolve();
            assert.equal(scSubmitBtn.disabled, false);

            document.dispatchEvent({ type: 'click', target: scSubmitBtn, preventDefault() {} });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(scInitSignals.length, 1);
            assert.equal(scInitSignals[0].aborted, false);
            creator.close();
            assert.equal(scInitSignals[0].aborted, true);
            resolveScInit({ stickerId: 'st-abort-1', uploadUrl: 'https://r2.test/st', requiredContentType: 'image/webp' });
            await new Promise(r => setTimeout(r, 20));
            assert.deepEqual(scCancelCalls, ['st-abort-1'], 'Late initiate after close must cancel server pending sticker');
            assert.equal(scPutCalls, 0);
            assert.equal(scCompleteCalls, 0);
            assert.equal(scCreatedCalls, 0);

            creator.destroy();
            deferredEmojiPicker.destroy();
            switchPicker.destroy();
            starPicker.destroy();
            racePicker.destroy();

            // [W18] hydrateStickerImages coalesces concurrent expired custom-sticker error retries
            let stickerRetryCalls = 0;
            let resolveStickerRetry;
            const retryWrap = createEl('div');
            const stickyImgs = [];
            for (let i = 0; i < 3; i++) {
                const sImg = createEl('img');
                sImg.setAttribute('data-sticker-img', 'true');
                sImg.setAttribute('data-sticker-id', '99999999-1111-2222-3333-444444444444');
                sImg.setAttribute('data-sticker-static', 'false');
                sImg.src = 'https://r2.test/expired-sticker';
                retryWrap.appendChild(sImg);
                stickyImgs.push(sImg);
            }
            body.appendChild(retryWrap);
            hydrateStickerImages(retryWrap, {
                getUrlFn: () => {
                    stickerRetryCalls++;
                    return new Promise((res) => { resolveStickerRetry = res; });
                }
            });
            for (const sImg of stickyImgs) sImg.dispatchEvent({ type: 'error' });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(stickerRetryCalls, 1, 'Simultaneous expired sticker image errors must coalesce onto one URL request');
            resolveStickerRetry({ url: 'https://r2.test/fresh-sticker', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
            await new Promise(r => setTimeout(r, 20));
            for (const sImg of stickyImgs) {
                assert.equal(sImg.src, 'https://r2.test/fresh-sticker');
            }

            const infoModal = createEl('div'); infoModal.id = 'sticker-info-modal';
            const infoImage = createEl('img'); infoImage.id = 'sticker-info-img';
            const infoFavBtn = createEl('button'); infoFavBtn.id = 'sticker-info-fav-btn';
            const infoFavIcon = createEl('span'); infoFavIcon.id = 'sticker-info-fav-icon';
            const infoFavLabel = createEl('span'); infoFavLabel.id = 'sticker-info-fav-label';
            infoFavBtn.appendChild(infoFavIcon);
            infoFavBtn.appendChild(infoFavLabel);
            infoModal.appendChild(infoImage);
            infoModal.appendChild(infoFavBtn);
            body.appendChild(infoModal);
            const infoLookups = new Map();
            let resolveInfoFavA;
            const infoPicker = createStickerPicker({
                getCurrentUserId: () => userId,
                getUrlFn: (id) => new Promise(resolve => infoLookups.set(id, resolve)),
                addFavoriteFn: () => new Promise(resolve => { resolveInfoFavA = resolve; })
            });
            const infoA = { id: '11111111-2222-3333-4444-555555555555', packName: 'Old', isFavorited: false };
            const infoB = { id: '22222222-3333-4444-5555-666666666666', packName: 'Current', isFavorited: false };
            infoPicker.openStickerInfoModal(infoA);
            const oldLoad = infoImage.onload;
            await Promise.resolve();

            // [W4] Click favorite on infoA, then switch modal to infoB BEFORE infoA resolves -> infoB stays unstarred & enabled!
            document.dispatchEvent({ type: 'click', target: infoFavBtn, preventDefault() {}, stopPropagation() {} });
            assert.equal(infoFavBtn.disabled, true);
            infoPicker.openStickerInfoModal(infoB);
            assert.equal(infoFavBtn.disabled, false, '[W4] Switching modal sticker must re-enable favorite button immediately');
            await Promise.resolve();
            infoLookups.get(infoB.id)({ url: 'https://r2.test/info-b' });
            await new Promise(resolve => setTimeout(resolve, 0));
            infoLookups.get(infoA.id)({ url: 'https://r2.test/info-a' });
            oldLoad();
            resolveInfoFavA({ id: infoA.id, packName: 'Old' });
            await new Promise(resolve => setTimeout(resolve, 10));
            assert.equal(infoImage.alt, 'Current');
            assert.equal(infoImage.src, 'https://r2.test/info-b');
            assert.equal(infoPicker.isFavorited(infoA.id), true);
            assert.equal(infoPicker.isFavorited(infoB.id), false, '[W4] Late favorite resolution from closed modal sticker must not mutate newly opened sticker');
            assert.equal(infoFavLabel.textContent, 'Add to Favorites');
            assert.equal(infoFavBtn.getAttribute('aria-pressed'), 'false');
            assert.equal(infoFavBtn.disabled, false);

            const pendingRetry = infoImage.onerror();
            await Promise.resolve();
            infoPicker.closeStickerInfoModal();
            infoLookups.get(infoB.id)({ url: 'https://r2.test/info-b-retried' });
            await pendingRetry;
            assert.equal(infoImage.src, '');
            infoPicker.destroy();

            // [S3] Proactive coalesced /url resolution for cold custom stickers (no eager /image 302 src)
            const coldStickerId = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
            let coldFetchCalls = 0;
            let resolveColdUrl;
            const coldSticker = {
                id: coldStickerId,
                packId: customPackId,
                packName: 'Cold Custom Pack',
                isAnimated: false,
                isSystem: false,
                isOwnedByCurrentUser: false,
                staticUrl: null,
                width: 512,
                height: 512,
                emojiTags: 'cool'
            };
            const { applyInitialStickerSrc } = await import('{{JsUrl("sticker-picker.js")}}');
            const coldWrap = createEl('div');
            const coldImgs = [];
            for (let i = 0; i < 3; i++) {
                const cImg = createEl('img');
                cImg.setAttribute('data-sticker-img', 'true');
                cImg.setAttribute('data-sticker-id', coldStickerId);
                cImg.setAttribute('data-sticker-static', 'false');
                applyInitialStickerSrc(cImg, coldSticker);
                assert.equal(cImg.getAttribute('src'), null, 'Cold custom sticker must not trigger eager /image 302 redirect');
                coldWrap.appendChild(cImg);
                coldImgs.push(cImg);
            }
            body.appendChild(coldWrap);
            hydrateStickerImages(coldWrap, {
                getUrlFn: () => {
                    coldFetchCalls++;
                    return new Promise((res) => { resolveColdUrl = res; });
                }
            });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(coldFetchCalls, 1, 'Multiple cold custom stickers must coalesce into 1 proactive /url request');
            resolveColdUrl({ url: 'https://r2.test/cold-prefetched', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
            await new Promise(r => setTimeout(r, 20));
            for (const cImg of coldImgs) {
                assert.equal(cImg.src, 'https://r2.test/cold-prefetched');
            }

            // 6. Step 7.5 regressions: static pack path safety, clearStickerUrlCache epoch guard, star icon DOM sync, inline onPackCreated
            const { isSafeMediaUrl } = await import('{{JsUrl("media-lightbox.js")}}');
            const { clearStickerUrlCache, resolveCachedStickerUrl, getCachedStickerUrl } = await import('{{JsUrl("sticker-picker.js")}}');
            assert.equal(isSafeMediaUrl('/stickers/packs/starter/wave.webp'), true);
            assert.equal(isSafeMediaUrl('/stickers/packs/../secret.webp'), false);

            const staticWaveImg = createEl('img');
            applyInitialStickerSrc(staticWaveImg, { id: '77777777-7777-7777-7777-777777777701', staticUrl: '/stickers/packs/starter/wave.webp' });
            assert.equal(staticWaveImg.src, '/stickers/packs/starter/wave.webp');

            let resolveClearedSticker;
            const clearedStickerPromise = resolveCachedStickerUrl('88888888-8888-8888-8888-888888888801', {
                getUrlFn: () => new Promise((res) => { resolveClearedSticker = res; })
            });
            await Promise.resolve();
            clearStickerUrlCache();
            resolveClearedSticker({
                url: 'https://r2.test/should-not-cache',
                expiresAtUtc: new Date(Date.now() + 600_000).toISOString()
            });
            assert.equal(await clearedStickerPromise, null);
            assert.equal(getCachedStickerUrl('88888888-8888-8888-8888-888888888801'), null);

            const rowFavId = '99999999-9999-9999-9999-999999999901';
            const rowTrigger = createEl('button');
            rowTrigger.setAttribute('data-sticker-info-trigger', 'true');
            rowTrigger.setAttribute('data-sticker-id', rowFavId);
            rowTrigger.setAttribute('data-sticker-favorited', 'false');
            const rowStarIcon = createEl('span');
            rowStarIcon.className = 'material-symbols-outlined text-[16px]';
            rowStarIcon.textContent = 'star_outline';
            rowTrigger.appendChild(rowStarIcon);
            body.appendChild(rowTrigger);

            const favSyncPicker = createStickerPicker({
                getCurrentUserId: () => userId,
                getCatalogFn: async () => ({ recents: [], favorites: [], systemPacks: [], customPacks: [] }),
                addFavoriteFn: async () => ({ id: rowFavId, packId: customPackId, isSystem: false, isFavorited: true }),
                removeFavoriteFn: async () => {}
            });
            await favSyncPicker.ensureLoaded({ force: true });
            await favSyncPicker.toggleFavorite({ id: rowFavId, packId: customPackId, isSystem: false, isFavorited: false });
            assert.equal(rowTrigger.getAttribute('data-sticker-favorited'), 'true');
            assert.equal(rowStarIcon.textContent, 'star');
            await favSyncPicker.toggleFavorite({ id: rowFavId, packId: customPackId, isSystem: false, isFavorited: true });
            assert.equal(rowTrigger.getAttribute('data-sticker-favorited'), 'false');
            assert.equal(rowStarIcon.textContent, 'star_outline');
            favSyncPicker.destroy();

            const createdPacksNotified = [];
            const packCreator = createStickerCreator({
                createPackFn: async (packName) => ({ id: 'aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb', name: packName }),
                onPackCreated: (pack) => createdPacksNotified.push(pack)
            });
            packCreator.open({ customPacks: [] });
            scNewPackInput.value = 'Inline Pack Without Upload';
            document.dispatchEvent({ type: 'click', target: scNewPackSave, preventDefault() {}, stopPropagation() {} });
            await new Promise(r => setTimeout(r, 15));
            packCreator.close();
            assert.equal(createdPacksNotified.length, 1);
            assert.equal(createdPacksNotified[0].id, 'aaaaaaaa-1111-2222-3333-bbbbbbbbbbbb');

            globalThis.window.confirm = origConfirm;
            globalThis.createImageBitmap = origCreatorBitmap;
            console.log('OK_MILESTONE_C_FRONTEND');
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node exited {exitCode}:\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        Assert.Contains("OK_MILESTONE_C_FRONTEND", stdout);
    }

    [Fact]
    public async Task StickerCreatorModal_IsolatesDragAndDropEventsFromChatUploader_InNode()
    {
        var root = FindSolutionRoot();
        string JsUrl(string name) => new Uri(Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", name)).AbsoluteUri;

        var script = $$"""
            import assert from 'node:assert/strict';

            function createClassList(el) {
                const getSet = () => new Set((el.className || '').split(/\s+/).filter(Boolean));
                const writeSet = (set) => { el.className = [...set].join(' '); };
                return {
                    add(...tokens) { const s = getSet(); tokens.forEach(t => s.add(t)); writeSet(s); },
                    remove(...tokens) { const s = getSet(); tokens.forEach(t => s.delete(t)); writeSet(s); },
                    toggle(token, force) {
                        const s = getSet();
                        const shouldAdd = force !== undefined ? !!force : !s.has(token);
                        if (shouldAdd) s.add(token); else s.delete(token);
                        writeSet(s);
                        return shouldAdd;
                    },
                    contains(token) { return getSet().has(token); }
                };
            }

            function matchesSingle(node, part) {
                if (!node || node.nodeType !== 1) return false;
                if (part.startsWith('#')) return node.id === part.slice(1);
                const attrMatch = /^([a-z0-9_.-]+)?((?:\[[^\]]+\])+)$/i.exec(part);
                if (attrMatch) {
                    const base = attrMatch[1] || '';
                    if (base) {
                        if (base.startsWith('.')) { if (!node.classList.contains(base.slice(1))) return false; }
                        else if (node.tagName.toLowerCase() !== base.toLowerCase()) return false;
                    }
                    const attrs = [...attrMatch[2].matchAll(/\[([^=\]]+)(?:="([^"]*)")?\]/g)];
                    return attrs.every((m) => {
                        const val = node.getAttribute(m[1]);
                        return m[2] === undefined ? val !== null : val === m[2];
                    });
                }
                if (part.startsWith('.')) return node.classList.contains(part.slice(1));
                return node.tagName.toLowerCase() === part.toLowerCase();
            }

            function walk(node, predicate, out = []) {
                if (!node) return out;
                if (predicate(node)) out.push(node);
                for (const c of node.children || []) walk(c, predicate, out);
                return out;
            }

            const docCapture = new Map();
            const docBubble = new Map();

            function dispatchDomEvent(target, evt) {
                evt.target = evt.target || target;
                evt.defaultPrevented = false;
                evt.cancelBubble = false;
                evt.preventDefault = () => { evt.defaultPrevented = true; };
                evt.stopPropagation = () => { evt.cancelBubble = true; };

                const path = [];
                let cur = target;
                while (cur) {
                    path.unshift(cur);
                    cur = cur.parentNode;
                }

                // Capture phase: document -> ancestors -> target
                for (const fn of docCapture.get(evt.type) || []) {
                    if (evt.cancelBubble) return evt;
                    evt.currentTarget = globalThis.document;
                    fn(evt);
                }
                for (let i = 0; i < path.length; i++) {
                    if (evt.cancelBubble) return evt;
                    const node = path[i];
                    evt.currentTarget = node;
                    for (const fn of node._capListeners?.get(evt.type) || []) {
                        fn(evt);
                    }
                }

                // Bubble phase: target -> ancestors -> document
                for (let i = path.length - 1; i >= 0; i--) {
                    if (evt.cancelBubble) return evt;
                    const node = path[i];
                    evt.currentTarget = node;
                    for (const fn of node._bubListeners?.get(evt.type) || []) {
                        fn(evt);
                    }
                }
                if (!evt.cancelBubble) {
                    for (const fn of docBubble.get(evt.type) || []) {
                        evt.currentTarget = globalThis.document;
                        fn(evt);
                    }
                }
                return evt;
            }

            function createEl(tagName, id = '') {
                const attrs = {};
                const el = {
                    nodeType: 1,
                    tagName: String(tagName || 'div').toUpperCase(),
                    children: [],
                    parentNode: null,
                    style: {},
                    _id: id,
                    _className: '',
                    _capListeners: new Map(),
                    _bubListeners: new Map(),
                    disabled: false,
                    value: '',
                    files: [],
                    textContent: '',
                    get firstChild() { return this.children[0] || null; },
                    get id() { return this._id; },
                    set id(v) { this._id = String(v || ''); attrs.id = this._id; },
                    get className() { return this._className; },
                    set className(v) { this._className = String(v || ''); attrs.class = this._className; },
                    appendChild(c) { c.parentNode = this; this.children.push(c); return c; },
                    removeChild(c) { this.children = this.children.filter(x => x !== c); return c; },
                    remove() { if (this.parentNode) this.parentNode.removeChild(this); },
                    contains(node) {
                        let cur = node;
                        while (cur) { if (cur === this) return true; cur = cur.parentNode; }
                        return false;
                    },
                    setAttribute(k, v) { if (k === 'id') this._id = String(v); if (k === 'class') this._className = String(v); attrs[k] = String(v); },
                    getAttribute(k) { return Object.prototype.hasOwnProperty.call(attrs, k) ? attrs[k] : null; },
                    hasAttribute(k) { return Object.prototype.hasOwnProperty.call(attrs, k); },
                    removeAttribute(k) { delete attrs[k]; },
                    querySelector(sel) { return this.querySelectorAll(sel)[0] || null; },
                    querySelectorAll(sel) { const part = sel.trim().split(/\s+/).pop(); return walk(this, n => n !== this && matchesSingle(n, part)); },
                    closest(sel) { let cur = this; while (cur) { if (matchesSingle(cur, sel)) return cur; cur = cur.parentNode; } return null; },
                    addEventListener(type, fn, useCapture = false) {
                        const map = useCapture ? this._capListeners : this._bubListeners;
                        if (!map.has(type)) map.set(type, new Set());
                        map.get(type).add(fn);
                    },
                    removeEventListener(type, fn, useCapture = false) {
                        const map = useCapture ? this._capListeners : this._bubListeners;
                        map.get(type)?.delete(fn);
                    },
                    dispatchEvent(evt) { return dispatchDomEvent(this, evt); },
                    focus() { document.activeElement = this; }
                };
                if (id) attrs.id = id;
                el.classList = createClassList(el);
                return el;
            }

            const body = createEl('body');
            globalThis.document = {
                body,
                activeElement: null,
                createElement: (t) => createEl(t),
                createDocumentFragment: () => ({ nodeType: 11, children: [], appendChild(c) { this.children.push(c); return c; } }),
                getElementById: (id) => walk(body, n => n.id === id)[0] || null,
                querySelector: (s) => walk(body, n => n !== body && matchesSingle(n, s))[0] || null,
                querySelectorAll: (s) => walk(body, n => n !== body && matchesSingle(n, s)),
                addEventListener(t, fn, useCapture = false) {
                    const map = useCapture ? docCapture : docBubble;
                    if (!map.has(t)) map.set(t, new Set());
                    map.get(t).add(fn);
                },
                removeEventListener(t, fn, useCapture = false) {
                    const map = useCapture ? docCapture : docBubble;
                    map.get(t)?.delete(fn);
                }
            };
            globalThis.window = { addEventListener() {}, removeEventListener() {} };
            globalThis.URL = class extends globalThis.URL {
                static createObjectURL() { return 'blob:mock/1'; }
                static revokeObjectURL() {}
            };
            const origBitmap = globalThis.createImageBitmap;
            globalThis.createImageBitmap = async () => ({ width: 256, height: 256, close() {} });

            // Replicate Conversation.cshtml nesting: #chat-active-pane contains BOTH #chat-dropzone-overlay AND #sticker-creator-modal
            const activePane = createEl('div', 'chat-active-pane');
            const dropzoneOverlay = createEl('div', 'chat-dropzone-overlay');
            dropzoneOverlay.className = 'hidden';
            const tray = createEl('div', 'composer-attachments-tray');
            tray.className = 'hidden';
            const attachBtn = createEl('button', 'composer-attach-btn');
            const attachMenu = createEl('div', 'composer-attach-menu');
            attachMenu.className = 'hidden';
            const pendingContainer = createEl('div', 'pending-uploads-container');
            const inputEl = createEl('textarea', 'message-content-input');

            const scModal = createEl('div', 'sticker-creator-modal');
            scModal.setAttribute('role', 'dialog');
            scModal.setAttribute('aria-modal', 'true');
            scModal.className = 'hidden';
            const scDropzone = createEl('div', 'sticker-creator-dropzone');
            const scDropLabel = createEl('p');
            scDropzone.appendChild(scDropLabel);
            const scPreviewArea = createEl('div', 'sticker-creator-preview-area');
            scPreviewArea.className = 'hidden';
            const scCanvas = createEl('canvas', 'sticker-creator-canvas');
            scCanvas.getContext = () => ({ clearRect() {}, save() {}, restore() {}, beginPath() {}, arc() {}, clip() {}, drawImage() {} });
            const scAnimImg = createEl('img', 'sticker-creator-anim-preview');
            const scAnimBadge = createEl('span', 'sticker-creator-anim-badge');
            const scStaticControls = createEl('div', 'sticker-creator-static-controls');
            const scZoomSlider = createEl('input', 'sticker-creator-zoom');
            const scPackSelect = createEl('select', 'sticker-creator-pack-select');
            const scNewPackRow = createEl('div', 'sticker-creator-new-pack-row');
            const scNewPackInput = createEl('input', 'sticker-creator-new-pack-input');
            const scTagsInput = createEl('input', 'sticker-creator-tags-input');
            const scProgressWrap = createEl('div', 'sticker-creator-progress-wrap');
            const scProgressBar = createEl('div', 'sticker-creator-progress-bar');
            const scProgressLabel = createEl('span', 'sticker-creator-progress-label');
            const scErrorEl = createEl('p', 'sticker-creator-error');
            const scSubmitBtn = createEl('button', 'sticker-creator-submit-btn');
            scSubmitBtn.disabled = true;

            for (const kid of [scDropzone, scPreviewArea, scCanvas, scAnimImg, scAnimBadge, scStaticControls, scZoomSlider, scPackSelect, scNewPackRow, scNewPackInput, scTagsInput, scProgressWrap, scProgressBar, scProgressLabel, scErrorEl, scSubmitBtn]) {
                scModal.appendChild(kid);
            }

            const otherModal = createEl('div', 'sticker-info-modal');
            otherModal.setAttribute('role', 'dialog');
            otherModal.setAttribute('aria-modal', 'true');
            otherModal.className = 'hidden';

            activePane.appendChild(dropzoneOverlay);
            activePane.appendChild(tray);
            activePane.appendChild(attachBtn);
            activePane.appendChild(attachMenu);
            activePane.appendChild(pendingContainer);
            activePane.appendChild(inputEl);
            activePane.appendChild(scModal);
            activePane.appendChild(otherModal);
            body.appendChild(activePane);

            const { createMediaUploader } = await import('{{JsUrl("media-uploader.js")}}');
            const { createStickerCreator } = await import('{{JsUrl("sticker-creator.js")}}');

            const uploader = createMediaUploader({
                state: { activeConversationId: '11111111-1111-1111-1111-111111111111', pendingConversationId: null }
            });
            uploader.init();
            uploader.setLocked(false);

            const creator = createStickerCreator();

            // 1. Open sticker creator modal and drag/drop a PNG onto child <p> inside #sticker-creator-dropzone
            creator.open({ customPacks: [] });
            assert.equal(creator.isOpen(), true);
            assert.equal(scModal.classList.contains('hidden'), false);

            const stickerFile = { name: 'cat.png', type: 'image/png', size: 2048, slice: () => ({ arrayBuffer: async () => new Uint8Array(10).buffer }) };
            const enterEvt = dispatchDomEvent(scDropLabel, { type: 'dragenter', dataTransfer: { types: ['Files'] } });
            assert.equal(enterEvt.defaultPrevented, true);
            assert.equal(enterEvt.cancelBubble, true);
            assert.equal(dropzoneOverlay.classList.contains('hidden'), true, 'Background chat dropzone overlay must remain hidden while sticker modal is open');

            const overEvt = dispatchDomEvent(scDropLabel, { type: 'dragover', dataTransfer: { types: ['Files'], dropEffect: '' } });
            assert.equal(overEvt.defaultPrevented, true);
            assert.equal(overEvt.cancelBubble, true);
            assert.equal(overEvt.dataTransfer.dropEffect, 'copy');

            const dropEvt = dispatchDomEvent(scDropLabel, { type: 'drop', dataTransfer: { types: ['Files'], files: [stickerFile] } });
            await new Promise(r => setTimeout(r, 20));
            assert.equal(dropEvt.defaultPrevented, true);
            assert.equal(dropEvt.cancelBubble, true);
            assert.equal(dropzoneOverlay.classList.contains('hidden'), true);
            assert.equal(uploader.hasPendingFiles(), false, 'Sticker drop must NEVER attach file to background chat uploader');
            assert.equal(uploader.getTrayItems().length, 0);
            assert.equal(scPreviewArea.classList.contains('hidden'), false, 'Sticker drop must load preview in sticker creator');
            assert.equal(scSubmitBtn.disabled, false);

            // 2. Dropping a file on the modal backdrop/card outside #sticker-creator-dropzone also must not leak to chat
            const extraFile = { name: 'leak-check.png', type: 'image/png', size: 1024, slice: () => ({ arrayBuffer: async () => new Uint8Array(10).buffer }) };
            const backdropDropEvt = dispatchDomEvent(scModal, { type: 'drop', dataTransfer: { types: ['Files'], files: [extraFile] } });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(backdropDropEvt.defaultPrevented, true);
            assert.equal(backdropDropEvt.cancelBubble, true);
            assert.equal(uploader.hasPendingFiles(), false, 'Drop on sticker modal backdrop must never reach chat uploader');

            // 3. Even when another modal (e.g., #sticker-info-modal) is open, #chat-active-pane blocks drag-and-drop
            creator.close();
            otherModal.classList.remove('hidden');
            dispatchDomEvent(activePane, { type: 'dragenter', dataTransfer: { types: ['Files'] } });
            assert.equal(dropzoneOverlay.classList.contains('hidden'), true);
            dispatchDomEvent(activePane, { type: 'drop', dataTransfer: { types: ['Files'], files: [extraFile] } });
            assert.equal(uploader.hasPendingFiles(), false, 'Chat dropzone must stay disabled while any modal is open');
            otherModal.classList.add('hidden');

            // 4. Once all modals are closed, dragging/dropping on #chat-active-pane attaches normally to chat
            const chatEnter = dispatchDomEvent(activePane, { type: 'dragenter', dataTransfer: { types: ['Files'] } });
            assert.equal(chatEnter.defaultPrevented, true);
            assert.equal(dropzoneOverlay.classList.contains('hidden'), false);
            const chatDrop = dispatchDomEvent(activePane, { type: 'drop', dataTransfer: { types: ['Files'], files: [extraFile] } });
            assert.equal(chatDrop.defaultPrevented, true);
            assert.equal(dropzoneOverlay.classList.contains('hidden'), true);
            assert.equal(uploader.hasPendingFiles(), true);
            assert.equal(uploader.getTrayItems().length, 1);
            assert.equal(uploader.getTrayItems()[0].fileName, 'leak-check.png');

            creator.destroy();
            uploader.destroy();
            globalThis.createImageBitmap = origBitmap;
            console.log('OK_STICKER_DND_ISOLATION');
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node exited {exitCode}:\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        Assert.Contains("OK_STICKER_DND_ISOLATION", stdout);
    }

    [Fact]
    public async Task StickerPicker_CustomConfirmationModal_ReplacesNativeDialogsForPackAndStickerDeletion_InNode()
    {
        var root = FindSolutionRoot();
        var modalsHtml = File.ReadAllText(Path.Combine(root, "src", "AOS.Messenger.Web", "Views", "Chat", "_ChatModals.cshtml"));
        Assert.Contains("id=\"sticker-confirm-modal\"", modalsHtml);
        Assert.Contains("id=\"sticker-confirm-cancel-btn\"", modalsHtml);
        Assert.Contains("id=\"sticker-confirm-accept-btn\"", modalsHtml);
        Assert.Contains("aria-modal=\"true\"", modalsHtml);

        var pickerJs = File.ReadAllText(Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", "sticker-picker.js"));
        Assert.DoesNotMatch(@"window\.(confirm|prompt|alert)\b", pickerJs);
        Assert.DoesNotMatch(@"\b(confirm|prompt|alert)\s*\(", Regex.Replace(pickerJs, @"function\s+\w+|openConfirmModal|closeConfirmModal|executePendingConfirm|isConfirmModalOpen", ""));

        string JsUrl(string name) => new Uri(Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", name)).AbsoluteUri;

        var script = $$"""
            import assert from 'node:assert/strict';

            const docCapture = new Map();
            const docBubble = new Map();
            function createClassList(el) {
                const getSet = () => new Set((el.className || '').split(/\s+/).filter(Boolean));
                const writeSet = (set) => { el.className = [...set].join(' '); };
                return {
                    add(...tokens) { const s = getSet(); tokens.forEach(t => s.add(t)); writeSet(s); },
                    remove(...tokens) { const s = getSet(); tokens.forEach(t => s.delete(t)); writeSet(s); },
                    toggle(token, force) {
                        const s = getSet();
                        const shouldAdd = force !== undefined ? !!force : !s.has(token);
                        if (shouldAdd) s.add(token); else s.delete(token);
                        writeSet(s);
                        return shouldAdd;
                    },
                    contains(token) { return getSet().has(token); }
                };
            }
            function matchesSingle(node, part) {
                if (!node || node.nodeType !== 1) return false;
                if (part.startsWith('#')) return node.id === part.slice(1);
                const attrMatch = /^([a-z0-9_.-]+)?((?:\[[^\]]+\])+)$/i.exec(part);
                if (attrMatch) {
                    const base = attrMatch[1] || '';
                    if (base) {
                        if (base.startsWith('.')) { if (!node.classList.contains(base.slice(1))) return false; }
                        else if (node.tagName.toLowerCase() !== base.toLowerCase()) return false;
                    }
                    const attrs = [...attrMatch[2].matchAll(/\[([^=\]]+)(?:="([^"]*)")?\]/g)];
                    return attrs.every((m) => {
                        const val = node.getAttribute(m[1]);
                        return m[2] === undefined ? val !== null : val === m[2];
                    });
                }
                if (part.startsWith('.')) return node.classList.contains(part.slice(1));
                return node.tagName.toLowerCase() === part.toLowerCase();
            }
            function walk(node, predicate, out = []) {
                if (!node) return out;
                if (predicate(node)) out.push(node);
                for (const c of node.children || []) walk(c, predicate, out);
                return out;
            }
            function createEl(tagName, id = '') {
                const attrs = {};
                const listeners = new Map();
                const el = {
                    nodeType: 1,
                    tagName: String(tagName || 'div').toUpperCase(),
                    children: [],
                    parentNode: null,
                    style: {},
                    _id: id,
                    _className: '',
                    disabled: false,
                    value: '',
                    textContent: '',
                    get isConnected() {
                        let cur = this;
                        while (cur) { if (cur === body) return true; cur = cur.parentNode; }
                        return false;
                    },
                    get firstChild() { return this.children[0] || null; },
                    get id() { return this._id; },
                    set id(v) { this._id = String(v || ''); attrs.id = this._id; },
                    get className() { return this._className; },
                    set className(v) { this._className = String(v || ''); attrs.class = this._className; },
                    appendChild(c) {
                        if (c && c.nodeType === 11) {
                            for (const kid of c.children.slice()) this.appendChild(kid);
                            c.children = [];
                            return c;
                        }
                        c.parentNode = this; this.children.push(c); return c;
                    },
                    removeChild(c) { this.children = this.children.filter(x => x !== c); c.parentNode = null; return c; },
                    remove() { if (this.parentNode) this.parentNode.removeChild(this); },
                    contains(node) { let cur = node; while (cur) { if (cur === this) return true; cur = cur.parentNode; } return false; },
                    setAttribute(k, v) { if (k === 'id') this._id = String(v); if (k === 'class') this._className = String(v); attrs[k] = String(v); },
                    getAttribute(k) { return Object.prototype.hasOwnProperty.call(attrs, k) ? attrs[k] : null; },
                    hasAttribute(k) { return Object.prototype.hasOwnProperty.call(attrs, k); },
                    removeAttribute(k) { delete attrs[k]; },
                    querySelector(sel) { return this.querySelectorAll(sel)[0] || null; },
                    querySelectorAll(sel) { const part = sel.trim().split(/\s+/).pop(); return walk(this, n => n !== this && matchesSingle(n, part)); },
                    closest(sel) { let cur = this; while (cur) { if (matchesSingle(cur, sel)) return cur; cur = cur.parentNode; } return null; },
                    addEventListener(t, fn) { if (!listeners.has(t)) listeners.set(t, new Set()); listeners.get(t).add(fn); },
                    removeEventListener(t, fn) { listeners.get(t)?.delete(fn); },
                    dispatchEvent(evt) {
                        evt.target = evt.target || this;
                        for (const fn of listeners.get(evt.type) || []) fn(evt);
                        if (!evt.cancelBubble) {
                            for (const fn of docBubble.get(evt.type) || []) fn(evt);
                        }
                    },
                    focus() { document.activeElement = this; }
                };
                if (id) attrs.id = id;
                el.classList = createClassList(el);
                return el;
            }
            const body = createEl('body');
            globalThis.document = {
                body,
                activeElement: null,
                createElement: (t) => createEl(t),
                createDocumentFragment: () => ({ nodeType: 11, children: [], appendChild(c) { this.children.push(c); return c; } }),
                getElementById: (id) => walk(body, n => n.id === id)[0] || null,
                querySelector: (s) => walk(body, n => n !== body && matchesSingle(n, s))[0] || null,
                querySelectorAll: (s) => walk(body, n => n !== body && matchesSingle(n, s)),
                addEventListener(t, fn, useCapture = false) {
                    const map = useCapture ? docCapture : docBubble;
                    if (!map.has(t)) map.set(t, new Set());
                    map.get(t).add(fn);
                },
                removeEventListener(t, fn, useCapture = false) {
                    const map = useCapture ? docCapture : docBubble;
                    map.get(t)?.delete(fn);
                },
                dispatchEvent(e) {
                    let stopped = false;
                    e.stopImmediatePropagation = () => { stopped = true; e.cancelBubble = true; };
                    e.stopPropagation = () => { e.cancelBubble = true; };
                    for (const fn of docCapture.get(e.type) || []) { if (stopped) return; fn(e); }
                    if (stopped) return;
                    for (const fn of docBubble.get(e.type) || []) { if (stopped) return; fn(e); }
                }
            };
            let nativeDialogCalls = 0;
            globalThis.window = {
                innerWidth: 1280,
                innerHeight: 800,
                addEventListener() {},
                removeEventListener() {},
                confirm: () => { nativeDialogCalls++; return true; },
                prompt: () => { nativeDialogCalls++; return ''; },
                alert: () => { nativeDialogCalls++; }
            };

            const { createEmojiPicker } = await import('{{JsUrl("emoji-picker.js")}}');
            const { createStickerPicker } = await import('{{JsUrl("sticker-picker.js")}}');

            const userId = '11111111-1111-1111-1111-111111111111';
            const packId = 'aaaaaaaa-1111-1111-1111-111111111111';
            const stickerId1 = 'bbbbbbbb-1111-1111-1111-111111111111';
            const stickerId2 = 'cccccccc-1111-1111-1111-111111111111';
            const xssPackName = '<img src=x onerror=alert(1)> Pack';
            const deletedPacks = [];
            const archivedStickers = [];
            let failDeletePackOnce = true;

            const picker = createStickerPicker({
                getCurrentUserId: () => userId,
                getCatalogFn: async () => ({
                    recents: [
                        { id: stickerId2, packId, packName: xssPackName, isOwnedByCurrentUser: true, staticUrl: `/api/stickers/${stickerId2}/image`, emojiTags: 'bye' }
                    ],
                    favorites: [
                        { id: stickerId2, packId, packName: xssPackName, isOwnedByCurrentUser: true, staticUrl: `/api/stickers/${stickerId2}/image`, emojiTags: 'bye' }
                    ],
                    systemPacks: [],
                    customPacks: [{
                        id: packId,
                        name: xssPackName,
                        isSystemPack: false,
                        stickers: [
                            { id: stickerId1, packId, packName: xssPackName, isOwnedByCurrentUser: true, staticUrl: `/api/stickers/${stickerId1}/image`, emojiTags: 'hi' },
                            { id: stickerId2, packId, packName: xssPackName, isOwnedByCurrentUser: true, staticUrl: `/api/stickers/${stickerId2}/image`, emojiTags: 'bye' }
                        ]
                    }]
                }),
                archiveStickerFn: async (sid) => { archivedStickers.push(sid); },
                deletePackFn: async (pid) => {
                    if (failDeletePackOnce) {
                        failDeletePackOnce = false;
                        throw new Error('Server unavailable');
                    }
                    deletedPacks.push(pid);
                }
            });

            const emojiPicker = createEmojiPicker({
                getUserId: () => userId,
                loadCatalog: async () => ({ categories: [{ id: 'smileys', label: 'Smileys', icon: 'mood' }], skinTones: [], entries: [], byCategory: new Map([[0, []]]) }),
                stickerPicker: picker
            });

            const triggerBtn = createEl('button');
            const inputEl = createEl('textarea');
            body.appendChild(triggerBtn);
            body.appendChild(inputEl);

            await emojiPicker.toggle(triggerBtn, inputEl, { surface: 'composer', initialMode: 'stickers' });
            assert.equal(emojiPicker.isOpen(), true);
            assert.equal(emojiPicker.getMode(), 'stickers');

            // 1. Switch to custom pack tab, then custom sticker removal opens custom confirmation modal (never native confirm)
            const customPackTab = document.querySelector(`[data-sticker-tab="pack:${packId}"]`);
            assert.ok(customPackTab);
            customPackTab.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            const delStickerBtn = document.querySelector(`[data-sticker-delete="${stickerId1}"]`);
            assert.ok(delStickerBtn);
            delStickerBtn.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            const modal = document.getElementById('sticker-confirm-modal');
            const titleEl = document.getElementById('sticker-confirm-title');
            const msgEl = document.getElementById('sticker-confirm-message');
            const errEl = document.getElementById('sticker-confirm-error');
            const cancelBtn = document.getElementById('sticker-confirm-cancel-btn');
            const acceptBtn = document.getElementById('sticker-confirm-accept-btn');
            assert.ok(modal && !modal.classList.contains('hidden'));
            assert.equal(nativeDialogCalls, 0);
            assert.equal(document.activeElement, cancelBtn, 'Focus must land safely on Cancel button');

            // Escape closes confirmation modal while keeping emoji/sticker picker OPEN & restoring focus
            let escPrevented = false;
            document.dispatchEvent({ type: 'keydown', key: 'Escape', preventDefault() { escPrevented = true; } });
            assert.equal(escPrevented, true);
            assert.equal(modal.classList.contains('hidden'), true);
            assert.equal(emojiPicker.isOpen(), true, 'Escape on confirmation modal must not close background sticker picker');
            assert.equal(document.activeElement, delStickerBtn);
            assert.deepEqual(archivedStickers, []);

            // Confirming removes the single sticker
            delStickerBtn.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            acceptBtn.dispatchEvent({ type: 'click', target: acceptBtn, preventDefault() {}, stopPropagation() {} });
            await new Promise(r => setTimeout(r, 15));
            assert.deepEqual(archivedStickers, [stickerId1]);
            assert.equal(modal.classList.contains('hidden'), true);
            assert.equal(document.querySelector(`[data-sticker-pick="${stickerId1}"]`), null);

            // 2. Pack deletion opens custom modal, renders pack name safely as textContent, handles server error, then confirms
            const delPackBtn = document.querySelectorAll('button').find(b => b.getAttribute('aria-label') === 'Delete custom pack');
            assert.ok(delPackBtn);
            delPackBtn.dispatchEvent({ type: 'click', preventDefault() {}, stopPropagation() {} });
            assert.equal(modal.classList.contains('hidden'), false);
            assert.equal(titleEl.textContent, 'Delete sticker pack?');
            assert.ok(msgEl.textContent.includes(xssPackName));
            assert.equal(msgEl.children.length, 0, 'Pack name must be rendered as textContent only (zero HTML nodes)');

            // Outside mousedown on modal does NOT close emoji/sticker picker underneath
            document.dispatchEvent({ type: 'mousedown', target: modal });
            assert.equal(emojiPicker.isOpen(), true);

            // First confirm fails -> error shown inside modal, buttons re-enabled, modal stays open
            acceptBtn.dispatchEvent({ type: 'click', target: acceptBtn, preventDefault() {}, stopPropagation() {} });
            await new Promise(r => setTimeout(r, 15));
            assert.equal(modal.classList.contains('hidden'), false);
            assert.equal(errEl.classList.contains('hidden'), false);
            assert.equal(errEl.textContent, 'Server unavailable');
            assert.equal(acceptBtn.disabled, false);
            assert.equal(cancelBtn.disabled, false);
            assert.deepEqual(deletedPacks, []);

            // Retry confirm succeeds -> pack deleted, modal closed, starred sticker stays in Favorites
            acceptBtn.dispatchEvent({ type: 'click', target: acceptBtn, preventDefault() {}, stopPropagation() {} });
            await new Promise(r => setTimeout(r, 15));
            assert.deepEqual(deletedPacks, [packId]);
            assert.equal(modal.classList.contains('hidden'), true);
            assert.equal(picker.getCustomPacks().length, 0);
            assert.equal(picker.isFavorited(stickerId2), true);
            assert.equal(nativeDialogCalls, 0);

            emojiPicker.destroy();
            picker.destroy();
            assert.equal(document.getElementById('sticker-confirm-modal'), null, 'destroy() must remove fallback confirm modal');
            console.log('OK_STICKER_CONFIRM_MODAL');
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node exited {exitCode}:\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        Assert.Contains("OK_STICKER_CONFIRM_MODAL", stdout);
    }

    [Fact]
    public async Task MobileWhatsAppActionSheet_HidesInlineBarOnMobile_AndOpensOnLongPressOrContextMenu()
    {
        var root = FindSolutionRoot();
        var conversationHtml = File.ReadAllText(Path.Combine(root, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml"));
        var modalsHtml = File.ReadAllText(Path.Combine(root, "src", "AOS.Messenger.Web", "Views", "Chat", "_ChatModals.cshtml"));

        Assert.DoesNotContain("message-actions-bar opacity-100", conversationHtml);
        Assert.Contains("message-actions-bar hidden sm:flex", conversationHtml);
        Assert.Contains("id=\"mobile-message-action-sheet\"", modalsHtml);

        string JsUrl(string name) => new Uri(Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", name)).AbsoluteUri;

        var script = $$"""
            import assert from 'node:assert/strict';

            const docListeners = new Map();
            const winListeners = new Map();
            function createClassList(el) {
                const getSet = () => new Set((el.className || '').split(/\s+/).filter(Boolean));
                const writeSet = (set) => { el.className = [...set].join(' '); };
                return {
                    add(...tokens) { const s = getSet(); tokens.forEach(t => s.add(t)); writeSet(s); },
                    remove(...tokens) { const s = getSet(); tokens.forEach(t => s.delete(t)); writeSet(s); },
                    contains(token) { return getSet().has(token); }
                };
            }
            function matchesSingle(node, part) {
                if (!node || node.nodeType !== 1) return false;
                if (part.startsWith('#')) return node.id === part.slice(1);
                const attrMatch = /^([a-z0-9_.-]+)?((?:\[[^\]]+\])+)$/i.exec(part);
                if (attrMatch) {
                    const base = attrMatch[1] || '';
                    if (base) {
                        if (base.startsWith('.')) { if (!node.classList.contains(base.slice(1))) return false; }
                        else if (node.tagName.toLowerCase() !== base.toLowerCase()) return false;
                    }
                    const attrs = [...attrMatch[2].matchAll(/\[([^=\]]+)(?:="([^"]*)")?\]/g)];
                    return attrs.every((m) => {
                        const val = node.getAttribute(m[1]);
                        return m[2] === undefined ? val !== null : val === m[2];
                    });
                }
                if (part.startsWith('.')) return node.classList.contains(part.slice(1));
                return node.tagName.toLowerCase() === part.toLowerCase();
            }
            function walk(node, predicate, out = []) {
                if (!node) return out;
                if (predicate(node)) out.push(node);
                for (const c of node.children || []) walk(c, predicate, out);
                return out;
            }
            function createEl(tagName, id = '') {
                const attrs = {};
                const listeners = new Map();
                const el = {
                    nodeType: 1,
                    tagName: String(tagName || 'div').toUpperCase(),
                    children: [],
                    parentNode: null,
                    style: {},
                    _id: id,
                    _className: '',
                    disabled: false,
                    value: '',
                    textContent: '',
                    get id() { return this._id; },
                    set id(v) { this._id = String(v || ''); attrs.id = this._id; },
                    get className() { return this._className; },
                    set className(v) { this._className = String(v || ''); attrs.class = this._className; },
                    appendChild(c) { c.parentNode = this; this.children.push(c); return c; },
                    insertBefore(c, ref) {
                        c.parentNode = this;
                        const idx = ref ? this.children.indexOf(ref) : -1;
                        if (idx >= 0) this.children.splice(idx, 0, c); else this.children.push(c);
                        return c;
                    },
                    removeChild(c) { this.children = this.children.filter(x => x !== c); return c; },
                    remove() { if (this.parentNode) this.parentNode.removeChild(this); },
                    setAttribute(k, v) { if (k === 'id') this._id = String(v); if (k === 'class') this._className = String(v); attrs[k] = String(v); },
                    getAttribute(k) { return Object.prototype.hasOwnProperty.call(attrs, k) ? attrs[k] : null; },
                    hasAttribute(k) { return Object.prototype.hasOwnProperty.call(attrs, k); },
                    removeAttribute(k) { delete attrs[k]; },
                    querySelector(sel) { return this.querySelectorAll(sel)[0] || null; },
                    querySelectorAll(sel) { const part = sel.trim().split(/\s+/).pop(); return walk(this, n => n !== this && matchesSingle(n, part)); },
                    closest(sel) { let cur = this; while (cur) { if (matchesSingle(cur, sel)) return cur; cur = cur.parentNode; } return null; },
                    addEventListener(t, fn) { if (!listeners.has(t)) listeners.set(t, new Set()); listeners.get(t).add(fn); },
                    removeEventListener(t, fn) { listeners.get(t)?.delete(fn); },
                    dispatchEvent(evt) { evt.target = evt.target || this; for (const fn of listeners.get(evt.type) || []) fn(evt); },
                    focus() {}
                };
                if (id) attrs.id = id;
                el.classList = createClassList(el);
                return el;
            }

            const body = createEl('body');
            globalThis.document = {
                body,
                createElement: (t) => createEl(t),
                getElementById: (id) => walk(body, n => n.id === id)[0] || null,
                querySelector: (s) => walk(body, n => n !== body && matchesSingle(n, s))[0] || null,
                querySelectorAll: (s) => walk(body, n => n !== body && matchesSingle(n, s)),
                addEventListener(t, fn) { if (!docListeners.has(t)) docListeners.set(t, new Set()); docListeners.get(t).add(fn); },
                removeEventListener(t, fn) { docListeners.get(t)?.delete(fn); },
                dispatchEvent(e) { for (const fn of docListeners.get(e.type) || []) fn(e); }
            };
            globalThis.window = {
                innerWidth: 390,
                addEventListener(t, fn) { if (!winListeners.has(t)) winListeners.set(t, new Set()); winListeners.get(t).add(fn); },
                removeEventListener(t, fn) { winListeners.get(t)?.delete(fn); }
            };

            // Build mobile action sheet & edit/delete modals
            const sheet = createEl('div', 'mobile-message-action-sheet');
            sheet.className = 'hidden';
            const title = createEl('p', 'mobile-action-sheet-title');
            const reactWrap = createEl('div', 'mobile-action-reactions');
            const fireBtn = createEl('button');
            fireBtn.setAttribute('data-mobile-react-emoji', '🔥');
            reactWrap.appendChild(fireBtn);
            const replyBtn = createEl('button', 'mobile-action-reply-btn');
            const copyBtn = createEl('button', 'mobile-action-copy-btn');
            copyBtn.className = 'hidden';
            const stickerInfoBtn = createEl('button', 'mobile-action-sticker-info-btn');
            stickerInfoBtn.className = 'hidden';
            const stickerInfoIcon = createEl('span', 'mobile-action-sticker-info-icon');
            stickerInfoBtn.appendChild(stickerInfoIcon);
            const editBtn = createEl('button', 'mobile-action-edit-btn');
            editBtn.className = 'hidden';
            const deleteBtn = createEl('button', 'mobile-action-delete-btn');
            const cancelBtn = createEl('button', 'mobile-action-cancel-btn');
            for (const el of [title, reactWrap, replyBtn, copyBtn, stickerInfoBtn, editBtn, deleteBtn, cancelBtn]) sheet.appendChild(el);
            body.appendChild(sheet);

            const editModal = createEl('div', 'edit-message-modal');
            editModal.className = 'hidden';
            const editInput = createEl('textarea', 'edit-message-input');
            editModal.appendChild(editInput);
            body.appendChild(editModal);

            const deleteModal = createEl('div', 'delete-message-modal');
            deleteModal.className = 'hidden';
            body.appendChild(deleteModal);

            const { createMessageBubbleElement } = await import('{{JsUrl("message-renderer.js")}}');
            const { setupMessageActions } = await import('{{JsUrl("message-actions.js")}}');

            const userId = '11111111-1111-1111-1111-111111111111';
            const peerId = '22222222-2222-2222-2222-222222222222';
            const convId = '33333333-3333-3333-3333-333333333333';

            const incomingRow = createMessageBubbleElement({
                id: 701,
                conversationId: convId,
                senderId: peerId,
                senderName: 'Ahmed',
                senderHandle: 'ahmed',
                content: 'hi',
                createdAtUtc: new Date().toISOString(),
                isOutgoing: false
            }, userId);
            const outgoingRow = createMessageBubbleElement({
                id: 702,
                conversationId: convId,
                senderId: userId,
                senderName: 'Me',
                senderHandle: 'me',
                content: 'hello back',
                createdAtUtc: new Date().toISOString(),
                isOutgoing: true
            }, userId);
            const stickerRow = createMessageBubbleElement({
                id: 703,
                conversationId: convId,
                senderId: userId,
                senderName: 'Me',
                senderHandle: 'me',
                content: '',
                createdAtUtc: new Date().toISOString(),
                isOutgoing: true,
                sticker: {
                    id: '44444444-4444-4444-4444-444444444444',
                    packId: '55555555-5555-5555-5555-555555555555',
                    packName: 'Warm Greetings',
                    packAuthor: 'A♡S Messenger',
                    isAnimated: false,
                    isSystem: true,
                    staticUrl: '/api/stickers/44444444-4444-4444-4444-444444444444/image',
                    width: 512,
                    height: 512,
                    emojiTags: '👋 hi'
                }
            }, userId);
            body.appendChild(incomingRow);
            body.appendChild(outgoingRow);
            body.appendChild(stickerRow);

            // Inline action bar is hidden on mobile (hidden sm:flex)
            assert.ok(incomingRow.querySelector('.message-actions-bar').classList.contains('hidden'));
            assert.ok(incomingRow.querySelector('.message-actions-bar').classList.contains('sm:flex'));

            const toggledReactions = [];
            const replies = [];
            let isMobile = true;
            const actions = setupMessageActions(() => convId, () => userId, {
                longPressDelayMs: 25,
                isMobileViewport: () => isMobile,
                onToggleReaction: (msgId, emoji) => toggledReactions.push({ msgId, emoji }),
                onReply: (id, sender, preview) => replies.push({ id, sender, preview })
            });

            // 1. Drag > 10px cancels long-press so scrolling never opens the sheet
            const incomingBubble = incomingRow.querySelector('.message-bubble');
            document.dispatchEvent({ type: 'pointerdown', target: incomingBubble, clientX: 50, clientY: 100, button: 0, isPrimary: true });
            document.dispatchEvent({ type: 'pointermove', target: incomingBubble, clientX: 50, clientY: 125 });
            await new Promise(r => setTimeout(r, 45));
            assert.equal(sheet.classList.contains('hidden'), true);

            // 2. Clean long-press opens sheet for incoming text message (Reply & Copy shown, Edit & Sticker Info hidden)
            document.dispatchEvent({ type: 'pointerdown', target: incomingBubble, clientX: 50, clientY: 100, button: 0, isPrimary: true });
            await new Promise(r => setTimeout(r, 45));
            assert.equal(sheet.classList.contains('hidden'), false);
            assert.equal(title.textContent, 'hi');
            assert.equal(replyBtn.classList.contains('hidden'), false);
            assert.equal(copyBtn.classList.contains('hidden'), false);
            assert.equal(editBtn.classList.contains('hidden'), true);
            assert.equal(stickerInfoBtn.classList.contains('hidden'), true);

            // Tap quick reaction 🔥 -> calls onToggleReaction and closes sheet
            reactWrap.dispatchEvent({ type: 'click', target: fireBtn, preventDefault() {}, stopPropagation() {} });
            assert.deepEqual(toggledReactions, [{ msgId: '701', emoji: '🔥' }]);
            assert.equal(sheet.classList.contains('hidden'), true);

            // 3. Mobile contextmenu on outgoing text message shows Edit and opens Edit Modal on tap
            const outgoingBubble = outgoingRow.querySelector('.message-bubble');
            let prevented = false;
            document.dispatchEvent({ type: 'contextmenu', target: outgoingBubble, preventDefault() { prevented = true; } });
            assert.equal(prevented, true);
            assert.equal(sheet.classList.contains('hidden'), false);
            assert.equal(editBtn.classList.contains('hidden'), false);
            editBtn.dispatchEvent({ type: 'click', preventDefault() {} });
            assert.equal(sheet.classList.contains('hidden'), true);
            assert.equal(editModal.classList.contains('hidden'), false);
            assert.equal(editInput.value, 'hello back');
            actions.closeEditModal();

            // 4. Sticker message shows Sticker Info and hides Edit & Copy; Reply works
            const stickerBubble = stickerRow.querySelector('.message-bubble');
            document.dispatchEvent({ type: 'contextmenu', target: stickerBubble, preventDefault() {} });
            assert.equal(sheet.classList.contains('hidden'), false);
            assert.equal(editBtn.classList.contains('hidden'), true);
            assert.equal(copyBtn.classList.contains('hidden'), true);
            assert.equal(stickerInfoBtn.classList.contains('hidden'), false);
            replyBtn.dispatchEvent({ type: 'click', preventDefault() {} });
            assert.equal(sheet.classList.contains('hidden'), true);
            assert.equal(replies.length, 1);
            assert.equal(replies[0].id, '703');
            assert.equal(replies[0].preview, '🏷️ Sticker');

            // 5. closeMobileActionSheetIfMessage closes when target message is deleted
            document.dispatchEvent({ type: 'contextmenu', target: incomingBubble, preventDefault() {} });
            assert.equal(sheet.classList.contains('hidden'), false);
            actions.closeMobileActionSheetIfMessage('701');
            assert.equal(sheet.classList.contains('hidden'), true);

            // 6. Desktop viewport does NOT intercept contextmenu or open mobile sheet
            isMobile = false;
            let desktopPrevented = false;
            document.dispatchEvent({ type: 'contextmenu', target: incomingBubble, preventDefault() { desktopPrevented = true; } });
            assert.equal(desktopPrevented, false);
            assert.equal(sheet.classList.contains('hidden'), true);

            actions.destroy();
            console.log('OK_MOBILE_WHATSAPP_SHEET');
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node exited {exitCode}:\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        Assert.Contains("OK_MOBILE_WHATSAPP_SHEET", stdout);
    }

    private static string FindSolutionRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null &&
               !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.slnx")) &&
               !Directory.Exists(Path.Combine(current.FullName, "src", "AOS.Messenger.Web")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new InvalidOperationException("Could not locate solution root.");
    }

    [Fact]
    public async Task MessagePersistence_AddAsync_AtomicallyRejectsArchivedOrDeletedPackSticker_AndPreservesIdempotentReplay()
    {
        var services = _fixture.CreateServiceCollection();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var stickerPersistence = new AOS.Messenger.Infrastructure.Persistence.Repositories.StickerPersistence(db);
        var messagePersistence = new AOS.Messenger.Infrastructure.Persistence.Repositories.MessagePersistence(db);
        var mediaDefaults = new AOS.Messenger.Application.Common.MediaOptions();
        var now = new DateTimeOffset(2026, 6, 1, 15, 0, 0, TimeSpan.Zero);

        var userA = await SeedConfirmedUserAsync(scope.ServiceProvider, "stick_a", "Stick A");
        var userB = await SeedConfirmedUserAsync(scope.ServiceProvider, "stick_b", "Stick B");
        var userAId = userA.ToDomainUserId();
        var userBId = userB.ToDomainUserId();

        var conv = Conversation.Create(userAId, userBId, now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var pack = await stickerPersistence.GetOrCreateDefaultCustomPackAsync(userAId, "Stick A", now);
        var pending = Sticker.CreatePendingCustom(pack.Id, userAId, "image/webp", 128, "atomic1", 1, now);
        var createdSticker = await stickerPersistence.TryCreatePendingStickerWithQuotaCheckAsync(
            pending,
            StickerConstraints.MaxStickersPerCustomPack,
            StickerConstraints.MaxTotalCustomStickersPerUser,
            mediaDefaults.MaxConcurrentPendingPerUser,
            mediaDefaults.DailyUploadQuotaBytes,
            now.AddHours(-24));

        var objId1 = MediaConstraints.GenerateStorageObjectId();
        var objKey1 = $"{MediaConstraints.StickerKeyPrefix}{objId1}";
        createdSticker.MarkReady(objId1, objKey1, 128, 128, 128, false, now);
        await stickerPersistence.UpdateStickerAsync(createdSticker);

        // 1. Unauthorized non-owner (neither favorited nor recented) must be rejected atomically inside AddAsync
        var unauthorizedMsg = Message.Create(
            conv.Id,
            userBId,
            "idem-atomic-unauth",
            null,
            now,
            stickerId: createdSticker.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => messagePersistence.AddAsync(unauthorizedMsg));
        Assert.False(await db.Messages.AnyAsync(m => m.IdempotencyKey == "idem-atomic-unauth"));

        // 2. Owner sending Ready sticker succeeds
        var ownerMsg = Message.Create(
            conv.Id,
            userAId,
            "idem-atomic-ok",
            null,
            now,
            stickerId: createdSticker.Id);
        var sentResult = await messagePersistence.AddAsync(ownerMsg);
        Assert.True(sentResult.IsNewlyCreated);
        Assert.Equal(createdSticker.Id, sentResult.Message.StickerId);

        // 3. Archive sticker -> new send referencing archived sticker fails atomically inside AddAsync
        await stickerPersistence.ArchiveStickerAndPruneReferencesAsync(createdSticker.Id, userAId, now.AddMinutes(1));
        var afterArchiveMsg = Message.Create(
            conv.Id,
            userAId,
            "idem-atomic-archived",
            null,
            now.AddMinutes(2),
            stickerId: createdSticker.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => messagePersistence.AddAsync(afterArchiveMsg));
        Assert.False(await db.Messages.AnyAsync(m => m.IdempotencyKey == "idem-atomic-archived"));

        // 4. Replaying the previously committed idempotency key after archival remains idempotent
        var replayResult = await messagePersistence.AddAsync(
            Message.Create(conv.Id, userAId, "idem-atomic-ok", null, now, stickerId: createdSticker.Id));
        Assert.False(replayResult.IsNewlyCreated);
        Assert.Equal(sentResult.Message.Id, replayResult.Message.Id);

        // 5. Soft-deleted pack with a Ready sticker must also be rejected atomically inside AddAsync
        var pending2 = Sticker.CreatePendingCustom(pack.Id, userAId, "image/webp", 128, "atomic2", 2, now);
        var sticker2 = await stickerPersistence.TryCreatePendingStickerWithQuotaCheckAsync(
            pending2,
            StickerConstraints.MaxStickersPerCustomPack,
            StickerConstraints.MaxTotalCustomStickersPerUser,
            mediaDefaults.MaxConcurrentPendingPerUser,
            mediaDefaults.DailyUploadQuotaBytes,
            now.AddHours(-24));
        var objId2 = MediaConstraints.GenerateStorageObjectId();
        var objKey2 = $"{MediaConstraints.StickerKeyPrefix}{objId2}";
        sticker2.MarkReady(objId2, objKey2, 128, 128, 128, false, now);
        await stickerPersistence.UpdateStickerAsync(sticker2);
        await stickerPersistence.SoftDeletePackAndArchiveStickersAsync(pack.Id, userAId, now.AddMinutes(3));

        var deletedPackMsg = Message.Create(
            conv.Id,
            userAId,
            "idem-atomic-delpack",
            null,
            now.AddMinutes(4),
            stickerId: sticker2.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => messagePersistence.AddAsync(deletedPackMsg));
        Assert.False(await db.Messages.AnyAsync(m => m.IdempotencyKey == "idem-atomic-delpack"));
    }

    [Fact]
    public async Task StickerService_CompleteUploadAsync_WhenFinalSaveConflictsWithWinner_CallsHandleConcurrentConflictBeforeCleanup()
    {
        var services = _fixture.CreateServiceCollection();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var realPersistence = new AOS.Messenger.Infrastructure.Persistence.Repositories.StickerPersistence(db);
        var userReader = scope.ServiceProvider.GetRequiredService<IUserReader>();
        var mediaDefaults = new AOS.Messenger.Application.Common.MediaOptions();
        var userA = await SeedConfirmedUserAsync(scope.ServiceProvider, "stick_race", "Stick Race");
        var callerId = userA.ToDomainUserId();
        var now = new DateTimeOffset(2026, 6, 1, 16, 0, 0, TimeSpan.Zero);

        var pack = await realPersistence.GetOrCreateDefaultCustomPackAsync(callerId, "Stick Race", now);
        var pending = Sticker.CreatePendingCustom(pack.Id, callerId, "image/webp", 128, "race", 1, now);
        var created = await realPersistence.TryCreatePendingStickerWithQuotaCheckAsync(
            pending,
            StickerConstraints.MaxStickersPerCustomPack,
            StickerConstraints.MaxTotalCustomStickersPerUser,
            mediaDefaults.MaxConcurrentPendingPerUser,
            mediaDefaults.DailyUploadQuotaBytes,
            now.AddHours(-24));
        var stagingKey = created.ObjectKey!;

        var events = new List<string>();
        var storage = new RecordingStickerRaceMediaStorage(events, contentLength: 128);
        var winnerObjId = MediaConstraints.GenerateStorageObjectId();
        var winnerObjKey = $"{MediaConstraints.StickerKeyPrefix}{winnerObjId}";
        var spyPersistence = new ConflictRaceStickerPersistence(realPersistence, events, async () =>
        {
            await using var winnerScope = provider.CreateAsyncScope();
            var winnerDb = winnerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var winnerSticker = await winnerDb.Stickers.FirstAsync(s => s.Id == created.Id);
            winnerSticker.MarkReady(winnerObjId, winnerObjKey, 64, 512, 512, false, now);
            await winnerDb.SaveChangesAsync();
        });

        var stickerService = new AOS.Messenger.Application.Services.StickerService(
            spyPersistence,
            storage,
            new StubStickerRaceProcessor(),
            userReader,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AOS.Messenger.Application.Services.StickerService>.Instance);

        var completed = await stickerService.CompleteUploadAsync(callerId, created.Id);

        Assert.Equal(created.Id, completed.Id);
        Assert.False(completed.IsAnimated);
        Assert.Equal(512, completed.Width);

        var conflictReadIdx = events.IndexOf("GetStickerWithPack");
        var firstDeleteIdx = events.FindIndex(e => e.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.True(conflictReadIdx >= 0, "Expected HandleConcurrentConflictAsync to inspect sticker state.");
        Assert.True(firstDeleteIdx > conflictReadIdx, "Orphan object deletion must occur after HandleConcurrentConflictAsync.");
        Assert.DoesNotContain(events, e => e == $"Delete:{winnerObjKey}");
        Assert.Contains(events, e => e == $"Delete:{stagingKey}");
        Assert.Contains(events, e => e.StartsWith($"Delete:{MediaConstraints.StickerKeyPrefix}", StringComparison.Ordinal) && e != $"Delete:{stagingKey}" && e != $"Delete:{winnerObjKey}");
    }

    [Fact]
    public async Task StickerService_CancelAndPackDeleteRaces_AndControllerJsonValidation_EnforceInvariants()
    {
        var services = _fixture.CreateServiceCollection();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var realPersistence = new AOS.Messenger.Infrastructure.Persistence.Repositories.StickerPersistence(db);
        var userReader = scope.ServiceProvider.GetRequiredService<IUserReader>();
        var signing = new LocalMediaSigningService(DataProtectionProvider.Create("Step3StickerTest"));
        var storage = new LocalDiskMediaStorage(signing, _mediaTempRoot);
        var processor = new StickerProcessor(storage, new ImageProcessingGate(Options.Create(new MediaOptions())));
        var stickerService = new AOS.Messenger.Application.Services.StickerService(
            realPersistence,
            storage,
            processor,
            userReader,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AOS.Messenger.Application.Services.StickerService>.Instance);

        var user = await SeedConfirmedUserAsync(scope.ServiceProvider, "stick_b3_w2", "Sticker B3W2");
        var callerId = user.ToDomainUserId();
        var now = DateTimeOffset.UtcNow;
        var pack = await realPersistence.CreateCustomPackWithLimitCheckAsync(
            StickerPack.CreateCustom(callerId, "Cancel Pack", "User", 0, now),
            StickerConstraints.MaxCustomPacksPerUser);

        // 1. Upload staged bytes -> CancelUploadAsync charges staged bytes to daily quota
        var init1 = await stickerService.InitiateUploadAsync(
            callerId,
            new InitiateStickerUploadRequestDto(pack.Id, "s1.webp", "image/webp", 4096, "✨"));
        var row1 = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == init1.StickerId);
        var stagingPath1 = Path.Combine(_mediaTempRoot, row1.ObjectKey!.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(stagingPath1)!);
        await File.WriteAllBytesAsync(stagingPath1, new byte[2048]);

        await stickerService.CancelUploadAsync(callerId, init1.StickerId);
        var cancelledRow = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == init1.StickerId);
        Assert.Equal(StickerState.Failed, cancelledRow.State);
        Assert.Equal(2048L, cancelledRow.VerifiedSizeBytes);
        Assert.NotNull(cancelledRow.PurgedAtUtc);

        // 2. Cancelling a sticker already committed to Ready throws ConflictException
        var initReady = await stickerService.InitiateUploadAsync(
            callerId,
            new InitiateStickerUploadRequestDto(pack.Id, "s2.webp", "image/webp", 128, "🔥"));
        var readyObjId = MediaConstraints.GenerateStorageObjectId();
        await using (var winnerScope = provider.CreateAsyncScope())
        {
            var winnerDb = winnerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var winner = await winnerDb.Stickers.SingleAsync(s => s.Id == initReady.StickerId);
            winner.MarkReady(readyObjId, $"{MediaConstraints.StickerKeyPrefix}{readyObjId}", 64, 512, 512, false, now);
            await winnerDb.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<ConflictException>(() => stickerService.CancelUploadAsync(callerId, initReady.StickerId));

        // 3. Deleted pack before final commit rejects completion cleanly & leaves zero Ready stickers in deleted pack
        var initDel = await stickerService.InitiateUploadAsync(
            callerId,
            new InitiateStickerUploadRequestDto(pack.Id, "s3.webp", "image/webp", 128, "🌊"));
        var rowDel = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == initDel.StickerId);
        await File.WriteAllBytesAsync(Path.Combine(_mediaTempRoot, rowDel.ObjectKey!.Replace('/', Path.DirectorySeparatorChar)), new byte[128]);

        await realPersistence.SoftDeletePackAndArchiveStickersAsync(pack.Id, callerId, now.AddMinutes(1));
        await Assert.ThrowsAsync<DomainException>(() =>
            new AOS.Messenger.Application.Services.StickerService(
                realPersistence,
                storage,
                new StubStickerRaceProcessor(),
                userReader,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AOS.Messenger.Application.Services.StickerService>.Instance)
            .CompleteUploadAsync(callerId, initDel.StickerId));
        var staleDelRow = await db.Stickers.AsNoTracking().SingleAsync(s => s.Id == initDel.StickerId);
        Assert.NotEqual(StickerState.Ready, staleDelRow.State);

        // 4. POST /api/stickers/favorites rejects non-integer & overflow sourceMessageId with 400 (never 500)
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);
        using var seedHttpScope = factory.Services.CreateScope();
        var httpUser = await SeedConfirmedUserAsync(seedHttpScope.ServiceProvider, "stick_json", "Json Test");
        var csrf = await LoginAndGetCsrfTokenAsync(client, httpUser.UserName!);

        var badJsonReq = new HttpRequestMessage(HttpMethod.Post, "/api/stickers/favorites");
        badJsonReq.Headers.Add("RequestVerificationToken", csrf);
        badJsonReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        badJsonReq.Content = new StringContent(
            $"{{\"stickerId\":\"{Guid.NewGuid()}\",\"sourceConversationId\":\"{Guid.NewGuid()}\",\"sourceMessageId\":1.5}}",
            Encoding.UTF8,
            "application/json");
        var badJsonResp = await client.SendAsync(badJsonReq);
        Assert.Equal(HttpStatusCode.BadRequest, badJsonResp.StatusCode);

        var overflowReq = new HttpRequestMessage(HttpMethod.Post, "/api/stickers/favorites");
        overflowReq.Headers.Add("RequestVerificationToken", csrf);
        overflowReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        overflowReq.Content = new StringContent(
            $"{{\"stickerId\":\"{Guid.NewGuid()}\",\"sourceConversationId\":\"{Guid.NewGuid()}\",\"sourceMessageId\":99999999999999999999999}}",
            Encoding.UTF8,
            "application/json");
        var overflowResp = await client.SendAsync(overflowReq);
        Assert.Equal(HttpStatusCode.BadRequest, overflowResp.StatusCode);

        // 5. MessageService idempotent replay of a sticker message after archival succeeds cleanly
        var peer = await SeedConfirmedUserAsync(scope.ServiceProvider, "stick_peer", "Sticker Peer");
        var conv = Conversation.CreateDirect(callerId, peer.ToDomainUserId(), now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        var activePack = await realPersistence.CreateCustomPackWithLimitCheckAsync(
            StickerPack.CreateCustom(callerId, "Active Replay Pack", "User", 0, now),
            StickerConstraints.MaxCustomPacksPerUser);
        var msgSticker = Sticker.CreatePendingCustom(activePack.Id, callerId, "image/webp", 128, "🎉", 0, now);
        var createdMsgSticker = await realPersistence.TryCreatePendingStickerWithQuotaCheckAsync(
            msgSticker,
            StickerConstraints.MaxStickersPerCustomPack,
            StickerConstraints.MaxTotalCustomStickersPerUser,
            10,
            1_000_000,
            now.AddHours(-24));
        var msgPermId = MediaConstraints.GenerateStorageObjectId();
        createdMsgSticker.MarkReady(msgPermId, $"{MediaConstraints.StickerKeyPrefix}{msgPermId}", 64, 512, 512, false, now);
        await realPersistence.CommitReadyStickerIfPackActiveAsync(createdMsgSticker, now);
        await db.SaveChangesAsync();

        var msgService = new AOS.Messenger.Application.Services.MessageService(
            new AOS.Messenger.Infrastructure.Persistence.Repositories.MessagePersistence(db),
            new AOS.Messenger.Infrastructure.Persistence.Repositories.ConversationPersistence(db),
            new AOS.Messenger.Infrastructure.Persistence.Repositories.BlockPersistence(db),
            userReader,
            scope.ServiceProvider.GetService<IRealtimeNotifier>() ?? NoOpReplayNotifier.Instance,
            new AOS.Messenger.Infrastructure.Persistence.Repositories.ReactionPersistence(db),
            new AOS.Messenger.Infrastructure.Persistence.Repositories.MediaPersistence(db),
            realPersistence,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AOS.Messenger.Application.Services.MessageService>.Instance,
            TimeProvider.System);
        var idemKey = Guid.NewGuid().ToString("N");
        var sentDto = await msgService.SendMessageAsync(callerId, conv.Id, new SendMessageRequestDto(null, idemKey, StickerId: createdMsgSticker.Id));
        await realPersistence.ArchiveStickerAndPruneReferencesAsync(createdMsgSticker.Id, callerId, now.AddMinutes(1));
        var replayedDto = await msgService.SendMessageAsync(callerId, conv.Id, new SendMessageRequestDto(null, idemKey, StickerId: createdMsgSticker.Id));
        Assert.Equal(sentDto.Id, replayedDto.Id);
    }

    private sealed class NoOpReplayNotifier : IRealtimeNotifier
    {
        public static readonly NoOpReplayNotifier Instance = new();
        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubStickerRaceProcessor : IStickerProcessor
    {
        public Task<ProcessedStickerResult> ProcessStickerAsync(
            Sticker sticker,
            MediaObjectMetadata stagingMetadata,
            CancellationToken ct = default) =>
            Task.FromResult(new ProcessedStickerResult(
                StickerConstraints.CanonicalStickerContentType,
                128,
                new MemoryStream(new byte[] { 1, 2, 3, 4 }),
                4,
                512,
                512,
                false));
    }

    private sealed class RecordingStickerRaceMediaStorage : IMediaStorage
    {
        private readonly List<string> _events;
        private readonly long _contentLength;

        public RecordingStickerRaceMediaStorage(List<string> events, long contentLength)
        {
            _events = events;
            _contentLength = contentLength;
        }

        public string GeneratePresignedPutUrl(string objectKey, string contentType, long contentLength, TimeSpan expiresIn, DateTimeOffset nowUtc) =>
            $"https://r2.test/put/{objectKey}";
        public string GeneratePresignedGetUrl(string objectKey, string responseContentType, string responseContentDisposition, TimeSpan expiresIn, DateTimeOffset nowUtc) =>
            $"https://r2.test/get/{objectKey}";
        public Task<MediaObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct = default) =>
            Task.FromResult<MediaObjectMetadata?>(new MediaObjectMetadata(_contentLength, "\"etag\"", "image/webp"));
        public Task<byte[]> ReadByteRangeAsync(string objectKey, long offset, int length, string? ifMatchETag = null, CancellationToken ct = default) =>
            Task.FromResult(Array.Empty<byte>());
        public Task DownloadToStreamAsync(string objectKey, Stream destination, long maxBytes, string? ifMatchETag = null, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task PromoteByCopyAsync(string stagingKey, string permanentKey, string verifiedContentType, string? contentDisposition, string? ifMatchETag = null, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task PromoteFromStreamAsync(string stagingKey, string permanentKey, Stream content, long contentLength, string verifiedContentType, string? contentDisposition, CancellationToken ct = default)
        {
            _events.Add($"Promote:{permanentKey}");
            return Task.CompletedTask;
        }
        public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default)
        {
            _events.Add($"Delete:{objectKey}");
            return Task.CompletedTask;
        }
        public async IAsyncEnumerable<StoredObjectInfo> ListObjectsAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class ConflictRaceStickerPersistence : IStickerPersistence
    {
        private readonly IStickerPersistence _inner;
        private readonly List<string> _events;
        private readonly Func<Task> _onUpdateRaceAsync;

        public ConflictRaceStickerPersistence(
            IStickerPersistence inner,
            List<string> events,
            Func<Task> onUpdateRaceAsync)
        {
            _inner = inner;
            _events = events;
            _onUpdateRaceAsync = onUpdateRaceAsync;
        }

        public Task<IReadOnlyList<StickerPack>> GetSystemPacksAsync(CancellationToken ct = default) =>
            _inner.GetSystemPacksAsync(ct);
        public Task<IReadOnlyList<StickerPack>> GetUserCustomPacksAsync(UserId ownerUserId, CancellationToken ct = default) =>
            _inner.GetUserCustomPacksAsync(ownerUserId, ct);
        public Task<StickerPack?> GetPackByIdAsync(StickerPackId packId, CancellationToken ct = default) =>
            _inner.GetPackByIdAsync(packId, ct);
        public Task<StickerPack> CreateCustomPackWithLimitCheckAsync(StickerPack pack, int maxCustomPacksPerUser, CancellationToken ct = default) =>
            _inner.CreateCustomPackWithLimitCheckAsync(pack, maxCustomPacksPerUser, ct);
        public Task UpdatePackAsync(StickerPack pack, CancellationToken ct = default) =>
            _inner.UpdatePackAsync(pack, ct);
        public Task SoftDeletePackAndArchiveStickersAsync(StickerPackId packId, UserId ownerUserId, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            _inner.SoftDeletePackAndArchiveStickersAsync(packId, ownerUserId, nowUtc, ct);
        public Task<StickerPack> GetOrCreateDefaultCustomPackAsync(UserId ownerUserId, string authorName, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            _inner.GetOrCreateDefaultCustomPackAsync(ownerUserId, authorName, nowUtc, ct);
        public Task<Sticker> TryCreatePendingStickerWithQuotaCheckAsync(Sticker sticker, int maxStickersPerPack, int maxTotalCustomStickersPerUser, int maxConcurrentPending, long dailyQuotaBytes, DateTimeOffset dailyQuotaWindowStartUtc, CancellationToken ct = default) =>
            _inner.TryCreatePendingStickerWithQuotaCheckAsync(sticker, maxStickersPerPack, maxTotalCustomStickersPerUser, maxConcurrentPending, dailyQuotaBytes, dailyQuotaWindowStartUtc, ct);
        public Task<Sticker?> GetStickerByIdAsync(StickerId stickerId, CancellationToken ct = default) =>
            _inner.GetStickerByIdAsync(stickerId, ct);
        public Task<Sticker?> GetStickerByIdNoTrackingAsync(StickerId stickerId, CancellationToken ct = default) =>
            _inner.GetStickerByIdNoTrackingAsync(stickerId, ct);
        public async Task<StickerWithPackRecord?> GetStickerWithPackAsync(StickerId stickerId, CancellationToken ct = default)
        {
            _events.Add("GetStickerWithPack");
            return await _inner.GetStickerWithPackAsync(stickerId, ct);
        }
        public Task<IReadOnlyDictionary<StickerId, StickerWithPackRecord>> GetStickersWithPacksByIdsAsync(IEnumerable<StickerId> stickerIds, CancellationToken ct = default) =>
            _inner.GetStickersWithPacksByIdsAsync(stickerIds, ct);
        public Task<IReadOnlyList<Sticker>> GetReadyStickersByPackIdsAsync(IEnumerable<StickerPackId> packIds, CancellationToken ct = default) =>
            _inner.GetReadyStickersByPackIdsAsync(packIds, ct);
        public Task<int> GetNextDisplayOrderInPackAsync(StickerPackId packId, CancellationToken ct = default) =>
            _inner.GetNextDisplayOrderInPackAsync(packId, ct);
        public async Task UpdateStickerAsync(Sticker sticker, CancellationToken ct = default)
        {
            await _onUpdateRaceAsync();
            throw new ConflictException("Simulated concurrent sticker update conflict.");
        }
        public async Task CommitReadyStickerIfPackActiveAsync(Sticker sticker, DateTimeOffset nowUtc, CancellationToken ct = default)
        {
            await _onUpdateRaceAsync();
            throw new ConflictException("Simulated concurrent sticker commit conflict.");
        }
        public Task ArchiveStickerAndPruneReferencesAsync(StickerId stickerId, UserId ownerUserId, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            _inner.ArchiveStickerAndPruneReferencesAsync(stickerId, ownerUserId, nowUtc, ct);
        public Task<IReadOnlyList<StickerWithPackRecord>> GetUserFavoriteStickersAsync(UserId userId, CancellationToken ct = default) =>
            _inner.GetUserFavoriteStickersAsync(userId, ct);
        public Task<IReadOnlyList<StickerWithPackRecord>> GetUserRecentStickersAsync(UserId userId, int maxCount, CancellationToken ct = default) =>
            _inner.GetUserRecentStickersAsync(userId, maxCount, ct);
        public Task<HashSet<StickerId>> GetUserFavoriteStickerIdsAsync(UserId userId, IEnumerable<StickerId> candidateIds, CancellationToken ct = default) =>
            _inner.GetUserFavoriteStickerIdsAsync(userId, candidateIds, ct);
        public Task<bool> IsStickerFavoritedByUserAsync(UserId userId, StickerId stickerId, CancellationToken ct = default) =>
            _inner.IsStickerFavoritedByUserAsync(userId, stickerId, ct);
        public Task AddFavoriteWithLimitCheckAsync(UserId userId, StickerId stickerId, int maxFavoritesPerUser, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            _inner.AddFavoriteWithLimitCheckAsync(userId, stickerId, maxFavoritesPerUser, nowUtc, ct);
        public Task RemoveFavoriteAsync(UserId userId, StickerId stickerId, CancellationToken ct = default) =>
            _inner.RemoveFavoriteAsync(userId, stickerId, ct);
        public Task UpsertRecentStickerAsync(UserId userId, StickerId stickerId, DateTimeOffset nowUtc, int maxRecentCount, CancellationToken ct = default) =>
            _inner.UpsertRecentStickerAsync(userId, stickerId, nowUtc, maxRecentCount, ct);
        public Task<bool> CanUserAccessStickerAsync(UserId callerId, StickerId stickerId, bool forSending, CancellationToken ct = default) =>
            _inner.CanUserAccessStickerAsync(callerId, stickerId, forSending, ct);
        public Task<bool> CanUserAccessStickerAsync(UserId callerId, StickerWithPackRecord record, bool forSending, CancellationToken ct = default) =>
            _inner.CanUserAccessStickerAsync(callerId, record, forSending, ct);
        public Task<bool> IsStickerVisibleInMessageAsync(UserId callerId, ConversationId conversationId, MessageId messageId, StickerId stickerId, CancellationToken ct = default) =>
            _inner.IsStickerVisibleInMessageAsync(callerId, conversationId, messageId, stickerId, ct);
    }
}
