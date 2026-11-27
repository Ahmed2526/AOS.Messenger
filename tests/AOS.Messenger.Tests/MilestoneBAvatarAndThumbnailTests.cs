using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
using ImageMagick;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class MilestoneBAvatarAndThumbnailTests : IClassFixture<SqlTestDatabaseFixture>, IDisposable
{
    private readonly SqlTestDatabaseFixture _fixture;
    private readonly string _mediaTempRoot;

    public MilestoneBAvatarAndThumbnailTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _mediaTempRoot = Path.Combine(Path.GetTempPath(), $"aos_milestone_b_tests_{Guid.NewGuid():N}");
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

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public AdjustableTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
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
        bool emailConfirmed = true,
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
            EmailConfirmed = emailConfirmed,
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

    private static byte[] CreateGradientPngBytes(uint width, uint height)
    {
        using var img = new MagickImage(new MagickColor(120, 80, 140), width, height);
        using var gradient = new MagickImage("gradient:#B3929D-#4B3B40", width, height);
        gradient.Format = MagickFormat.Png;
        return gradient.ToByteArray();
    }

    private static byte[] CreateExifJpegBytes(uint width, uint height)
    {
        using var img = new MagickImage("gradient:#A07860-#F0E9DF", width, height);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "LeicaSecretModel");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        img.SetProfile(exif);
        img.Quality = 90;
        using var ms = new MemoryStream();
        img.Write(ms, MagickFormat.Jpeg);
        return ms.ToArray();
    }

    private static byte[] CreateAnimatedWebpBytes()
    {
        using var frames = new MagickImageCollection();
        var f1 = new MagickImage(new MagickColor(200, 50, 50), 64, 64) { AnimationDelay = 10 };
        var f2 = new MagickImage(new MagickColor(50, 50, 200), 64, 64) { AnimationDelay = 10 };
        frames.Add(f1);
        frames.Add(f2);
        using var ms = new MemoryStream();
        frames.Write(ms, MagickFormat.WebP);
        return ms.ToArray();
    }

    private static byte[] CreateAnimatedGifBytes()
    {
        using var frames = new MagickImageCollection();
        var f1 = new MagickImage(new MagickColor(220, 120, 80), 640, 640) { AnimationDelay = 12 };
        var f2 = new MagickImage(new MagickColor(80, 120, 220), 640, 640) { AnimationDelay = 12 };
        frames.Add(f1);
        frames.Add(f2);
        using var ms = new MemoryStream();
        frames.Write(ms, MagickFormat.Gif);
        return ms.ToArray();
    }

    private async Task<AvatarUpdatedResponseDto> UploadAvatarThroughApiAsync(
        HttpClient client,
        string csrfToken,
        string fileName,
        string contentType,
        byte[] bytes)
    {
        using var initReq = CreateCsrfRequest(
            HttpMethod.Post,
            "/api/profile/avatar/initiate",
            csrfToken,
            new { fileName, contentType, sizeBytes = bytes.Length });
        var initResp = await client.SendAsync(initReq);
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);

        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var uploadId = initDoc.RootElement.GetProperty("uploadId").GetString()!;
        var uploadUrl = initDoc.RootElement.GetProperty("uploadUrl").GetString()!;
        var requiredType = initDoc.RootElement.GetProperty("requiredContentType").GetString()!;

        using (var putReq = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(bytes) })
        {
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue(requiredType);
            var putResp = await client.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        }

        var statusUrl = $"/api/profile/avatar/{uploadId}/status";
        using var pendingStatus = await client.GetAsync(statusUrl);
        Assert.Equal(HttpStatusCode.NoContent, pendingStatus.StatusCode);

        using var compReq = CreateCsrfRequest(HttpMethod.Post, $"/api/profile/avatar/{uploadId}/complete", csrfToken);
        var compResp = await client.SendAsync(compReq);
        Assert.Equal(HttpStatusCode.OK, compResp.StatusCode);

        var body = await compResp.Content.ReadAsStringAsync();
        using var compDoc = JsonDocument.Parse(body);
        using var completedStatus = await client.GetAsync(statusUrl);
        Assert.Equal(HttpStatusCode.OK, completedStatus.StatusCode);
        var status = await completedStatus.Content.ReadFromJsonAsync<AvatarUpdatedResponseDto>();
        Assert.Equal(compDoc.RootElement.GetProperty("avatarVersion").GetString(), status?.AvatarVersion);

        return new AvatarUpdatedResponseDto(
            compDoc.RootElement.GetProperty("avatarVersion").GetString(),
            compDoc.RootElement.GetProperty("updatedAtUtc").GetDateTimeOffset());
    }

    // =========================================================================
    // 1. Avatar Dual-Variant E2E Lifecycle, Privacy, EXIF Strip & Removal
    // =========================================================================

    [Fact]
    public async Task AvatarLifecycle_DualVariantProcessingReplaceRemoveAndPrivacyIsolation_WorksEndToEnd()
    {
        using var factory = CreateFactory();
        using var aliceClient = CreateHttpsClient(factory);
        using var bobClient = CreateHttpsClient(factory);
        using var anonClient = CreateHttpsClient(factory);

        Guid aliceId;
        Guid bobId;
        Guid blockedUserId;
        Guid unverifiedUserId;
        string aliceUsername;
        string bobUsername;
        Guid convId;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "av_alice", "Alice Avatar");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "av_bob", "Bob Avatar");
            var blocked = await SeedConfirmedUserAsync(scope.ServiceProvider, "av_blk", "Blocked Avatar");
            var unverified = await SeedConfirmedUserAsync(scope.ServiceProvider, "av_unv", "Unverified", emailConfirmed: false);

            aliceId = alice.Id;
            bobId = bob.Id;
            blockedUserId = blocked.Id;
            unverifiedUserId = unverified.Id;
            aliceUsername = alice.UserName!;
            bobUsername = bob.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(aliceId), new UserId(bobId), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            db.UserBlocks.Add(UserBlock.Create(new UserId(aliceId), new UserId(blockedUserId), DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        // Unauthenticated requests are rejected (401 / redirect)
        var anonResp = await anonClient.GetAsync($"/api/users/{aliceId}/avatar/url?size=thumb");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        var aliceCsrf = await LoginAndGetCsrfTokenAsync(aliceClient, aliceUsername);
        var bobCsrf = await LoginAndGetCsrfTokenAsync(bobClient, bobUsername);

        // Upload large 1400x1200 JPEG with EXIF
        var jpegBytes = CreateExifJpegBytes(1400, 1200);
        var uploaded1 = await UploadAvatarThroughApiAsync(aliceClient, aliceCsrf, "selfie.jpg", "image/jpeg", jpegBytes);
        Assert.False(string.IsNullOrWhiteSpace(uploaded1.AvatarVersion));

        string oldFullKey;
        string oldThumbKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var aliceRow = await db.Users.AsNoTracking().SingleAsync(u => u.Id == aliceId);
            oldFullKey = aliceRow.AvatarObjectKey!;
            oldThumbKey = aliceRow.AvatarThumbnailObjectKey!;
            Assert.StartsWith("avatars/", oldFullKey);
            Assert.StartsWith("avatars/", oldThumbKey);
            Assert.NotEqual(oldFullKey, oldThumbKey);
            Assert.Equal("image/webp", aliceRow.AvatarContentType);
            Assert.NotNull(aliceRow.AvatarUpdatedAtUtc);
            Assert.True(aliceRow.AvatarThumbnailSizeBytes < aliceRow.AvatarSizeBytes);
        }

        // Bob fetches thumb (default) and full separately and verifies dimensions & stripped EXIF
        var thumbRedirect = await bobClient.GetAsync($"/api/users/{aliceId}/avatar");
        Assert.Equal(HttpStatusCode.Redirect, thumbRedirect.StatusCode);
        var thumbBytes = await (await bobClient.GetAsync(thumbRedirect.Headers.Location!)).Content.ReadAsByteArrayAsync();
        using (var thumbImg = new MagickImage(thumbBytes))
        {
            Assert.Equal(MagickFormat.WebP, thumbImg.Format);
            Assert.Equal(128u, thumbImg.Width);
            Assert.Equal(128u, thumbImg.Height);
            Assert.Null(thumbImg.GetExifProfile());
        }

        var fullUrlResp = await bobClient.GetAsync($"/api/users/{aliceId}/avatar/url?size=full");
        Assert.Equal(HttpStatusCode.OK, fullUrlResp.StatusCode);
        using var fullUrlJson = JsonDocument.Parse(await fullUrlResp.Content.ReadAsStringAsync());
        Assert.Equal("full", fullUrlJson.RootElement.GetProperty("size").GetString());
        var fullSignedUrl = fullUrlJson.RootElement.GetProperty("url").GetString()!;
        var fullBytes = await (await bobClient.GetAsync(fullSignedUrl)).Content.ReadAsByteArrayAsync();
        using (var fullImg = new MagickImage(fullBytes))
        {
            Assert.Equal(MagickFormat.WebP, fullImg.Format);
            // Server guarantees center square crop & max 1024px edge
            Assert.Equal(1024u, fullImg.Width);
            Assert.Equal(1024u, fullImg.Height);
            Assert.Null(fullImg.GetExifProfile());
            Assert.DoesNotContain("LeicaSecretModel", Encoding.ASCII.GetString(fullBytes));
        }

        // Invalid size query fails cleanly with 400
        var badSizeResp = await bobClient.GetAsync($"/api/users/{aliceId}/avatar/url?size=huge");
        Assert.Equal(HttpStatusCode.BadRequest, badSizeResp.StatusCode);

        // Conversation DTO & Profile DTO include Alice's AvatarVersion for unblocked Bob
        var convResp = await bobClient.GetAsync($"/api/conversations/{convId}/bundle");
        Assert.Equal(HttpStatusCode.OK, convResp.StatusCode);
        using (var convDoc = JsonDocument.Parse(await convResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal(
                uploaded1.AvatarVersion,
                convDoc.RootElement.GetProperty("conversation").GetProperty("otherParticipantAvatarVersion").GetString());
        }

        // Unverified user has no accessible avatar -> 404
        var unvResp = await bobClient.GetAsync($"/api/users/{unverifiedUserId}/avatar/url");
        Assert.Equal(HttpStatusCode.NotFound, unvResp.StatusCode);

        // Blocked peer gets 404 on blocked user's avatar & zero oracle leakage
        var blkClient = CreateHttpsClient(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var blkUser = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>()
                .Users.AsNoTracking().SingleAsync(u => u.Id == blockedUserId);
            await LoginAndGetCsrfTokenAsync(blkClient, blkUser.UserName!);
        }

        var blkAvatarResp = await blkClient.GetAsync($"/api/users/{aliceId}/avatar/url?size=thumb");
        Assert.Equal(HttpStatusCode.NotFound, blkAvatarResp.StatusCode);
        var blkProfileResp = await blkClient.GetAsync($"/api/users/{aliceId}");
        Assert.Equal(HttpStatusCode.NotFound, blkProfileResp.StatusCode);

        // Replace avatar with 600x600 PNG -> both old R2 files are deleted
        var uploaded2 = await UploadAvatarThroughApiAsync(aliceClient, aliceCsrf, "new.png", "image/png", CreateGradientPngBytes(600, 600));
        Assert.NotEqual(uploaded1.AvatarVersion, uploaded2.AvatarVersion);

        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, oldFullKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, oldThumbKey.Replace('/', Path.DirectorySeparatorChar))));

        string secondFullKey;
        string secondThumbKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var aliceRow = await db.Users.AsNoTracking().SingleAsync(u => u.Id == aliceId);
            secondFullKey = aliceRow.AvatarObjectKey!;
            secondThumbKey = aliceRow.AvatarThumbnailObjectKey!;
        }

        // Delete avatar -> clears SQL columns and removes both R2 files
        using var delReq = CreateCsrfRequest(HttpMethod.Delete, "/api/profile/avatar", aliceCsrf);
        var delResp = await aliceClient.SendAsync(delReq);
        Assert.Equal(HttpStatusCode.NoContent, delResp.StatusCode);

        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, secondFullKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, secondThumbKey.Replace('/', Path.DirectorySeparatorChar))));

        var afterDelUrl = await bobClient.GetAsync($"/api/users/{aliceId}/avatar/url?size=thumb");
        Assert.Equal(HttpStatusCode.NotFound, afterDelUrl.StatusCode);
    }

    // =========================================================================
    // 2. Avatar Security Validation (SVG, Animated WebP, Non-Owner Complete)
    // =========================================================================

    [Fact]
    public async Task AvatarSecurity_RejectsSvgAnimatedWebpAndNonOwnerCompletion()
    {
        using var factory = CreateFactory();
        using var aliceClient = CreateHttpsClient(factory);
        using var bobClient = CreateHttpsClient(factory);

        string aliceUsername;
        string bobUsername;
        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "avsec_a", "Alice Sec");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "avsec_b", "Bob Sec");
            aliceUsername = alice.UserName!;
            bobUsername = bob.UserName!;
        }

        var aliceCsrf = await LoginAndGetCsrfTokenAsync(aliceClient, aliceUsername);
        var bobCsrf = await LoginAndGetCsrfTokenAsync(bobClient, bobUsername);

        // SVG rejected at initiate
        using var svgReq = CreateCsrfRequest(
            HttpMethod.Post,
            "/api/profile/avatar/initiate",
            aliceCsrf,
            new { fileName = "evil.svg", contentType = "image/svg+xml", sizeBytes = 128 });
        var svgResp = await aliceClient.SendAsync(svgReq);
        Assert.Equal(HttpStatusCode.BadRequest, svgResp.StatusCode);

        // Animated WebP rejected on complete
        var animWebp = CreateAnimatedWebpBytes();
        using var animInit = CreateCsrfRequest(
            HttpMethod.Post,
            "/api/profile/avatar/initiate",
            aliceCsrf,
            new { fileName = "anim.webp", contentType = "image/webp", sizeBytes = animWebp.Length });
        var animInitResp = await aliceClient.SendAsync(animInit);
        Assert.Equal(HttpStatusCode.OK, animInitResp.StatusCode);
        using var animDoc = JsonDocument.Parse(await animInitResp.Content.ReadAsStringAsync());
        var animId = animDoc.RootElement.GetProperty("uploadId").GetString()!;
        var animPutUrl = animDoc.RootElement.GetProperty("uploadUrl").GetString()!;

        using (var putReq = new HttpRequestMessage(HttpMethod.Put, animPutUrl) { Content = new ByteArrayContent(animWebp) })
        {
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("image/webp");
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(putReq)).StatusCode);
        }

        using var bobStatus = await bobClient.GetAsync($"/api/profile/avatar/{animId}/status");
        Assert.Equal(HttpStatusCode.NotFound, bobStatus.StatusCode);

        // Bob tries to complete Alice's upload -> 403 Forbidden
        using var bobComp = CreateCsrfRequest(HttpMethod.Post, $"/api/profile/avatar/{animId}/complete", bobCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await bobClient.SendAsync(bobComp)).StatusCode);

        // Alice completes animated WebP -> 400 DomainException
        using var aliceComp = CreateCsrfRequest(HttpMethod.Post, $"/api/profile/avatar/{animId}/complete", aliceCsrf);
        Assert.Equal(HttpStatusCode.BadRequest, (await aliceClient.SendAsync(aliceComp)).StatusCode);
    }

    // =========================================================================
    // 3. Chat Image Thumbnail Pipeline (?variant=thumb vs ?variant=original)
    // =========================================================================

    [Fact]
    public async Task ChatMedia_Generates480pxThumbnailForLargeStaticPhoto_FallsBackCleanlyAndPurgesBoth()
    {
        using var factory = CreateFactory();
        using var aliceClient = CreateHttpsClient(factory);
        using var bobClient = CreateHttpsClient(factory);

        Guid convId;
        string aliceUsername;
        string bobUsername;
        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "chth_a", "Alice Photo");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "chth_b", "Bob Photo");
            aliceUsername = alice.UserName!;
            bobUsername = bob.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        var aliceCsrf = await LoginAndGetCsrfTokenAsync(aliceClient, aliceUsername);
        await LoginAndGetCsrfTokenAsync(bobClient, bobUsername);

        // 1. Static large photo (1200x800 PNG) -> generates thumbs/ (<= 480px) AND media/
        var largePhotoBytes = CreateGradientPngBytes(1200, 800);
        using var initPhoto = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convId}/media/initiate",
            aliceCsrf,
            new { fileName = "landscape.png", contentType = "image/png", sizeBytes = largePhotoBytes.Length });
        var initPhotoResp = await aliceClient.SendAsync(initPhoto);
        Assert.Equal(HttpStatusCode.OK, initPhotoResp.StatusCode);
        using var initPhotoDoc = JsonDocument.Parse(await initPhotoResp.Content.ReadAsStringAsync());
        var photoMediaId = initPhotoDoc.RootElement.GetProperty("mediaId").GetString()!;
        var photoPutUrl = initPhotoDoc.RootElement.GetProperty("uploadUrl").GetString()!;

        using (var putReq = new HttpRequestMessage(HttpMethod.Put, photoPutUrl) { Content = new ByteArrayContent(largePhotoBytes) })
        {
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(putReq)).StatusCode);
        }

        using var compPhoto = CreateCsrfRequest(HttpMethod.Post, $"/api/conversations/{convId}/media/{photoMediaId}/complete", aliceCsrf);
        var compPhotoResp = await aliceClient.SendAsync(compPhoto);
        Assert.Equal(HttpStatusCode.OK, compPhotoResp.StatusCode);
        using var compPhotoDoc = JsonDocument.Parse(await compPhotoResp.Content.ReadAsStringAsync());
        Assert.True(compPhotoDoc.RootElement.GetProperty("hasThumbnail").GetBoolean());

        // 2. Animated GIF (640x640) -> HasThumbnail = false, ?variant=thumb falls back to original GIF
        var gifBytes = CreateAnimatedGifBytes();
        using var initGif = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convId}/media/initiate",
            aliceCsrf,
            new { fileName = "party.gif", contentType = "image/gif", sizeBytes = gifBytes.Length });
        var initGifResp = await aliceClient.SendAsync(initGif);
        Assert.Equal(HttpStatusCode.OK, initGifResp.StatusCode);
        using var initGifDoc = JsonDocument.Parse(await initGifResp.Content.ReadAsStringAsync());
        var gifMediaId = initGifDoc.RootElement.GetProperty("mediaId").GetString()!;
        var gifPutUrl = initGifDoc.RootElement.GetProperty("uploadUrl").GetString()!;

        using (var putReq = new HttpRequestMessage(HttpMethod.Put, gifPutUrl) { Content = new ByteArrayContent(gifBytes) })
        {
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("image/gif");
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(putReq)).StatusCode);
        }

        using var compGif = CreateCsrfRequest(HttpMethod.Post, $"/api/conversations/{convId}/media/{gifMediaId}/complete", aliceCsrf);
        var compGifResp = await aliceClient.SendAsync(compGif);
        Assert.Equal(HttpStatusCode.OK, compGifResp.StatusCode);
        using var compGifDoc = JsonDocument.Parse(await compGifResp.Content.ReadAsStringAsync());
        Assert.False(compGifDoc.RootElement.GetProperty("hasThumbnail").GetBoolean());

        // 3. Send message containing both attachments
        using var sendReq = CreateCsrfRequest(
            HttpMethod.Post,
            $"/api/conversations/{convId}/messages",
            aliceCsrf,
            new
            {
                content = "",
                idempotencyKey = Guid.NewGuid().ToString("N"),
                mediaIds = new[] { Guid.Parse(photoMediaId), Guid.Parse(gifMediaId) }
            });
        var sendResp = await aliceClient.SendAsync(sendReq);
        Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);
        using var sendDoc = JsonDocument.Parse(await sendResp.Content.ReadAsStringAsync());
        var msgId = sendDoc.RootElement.GetProperty("id").GetInt64();

        // Bob downloads ?variant=thumb -> 480x320 WebP thumbnail
        var thumbRedir = await bobClient.GetAsync($"/api/conversations/{convId}/messages/{msgId}/media/{photoMediaId}?variant=thumb");
        Assert.Equal(HttpStatusCode.Redirect, thumbRedir.StatusCode);
        var thumbBytes = await (await bobClient.GetAsync(thumbRedir.Headers.Location!)).Content.ReadAsByteArrayAsync();
        using (var thumbImg = new MagickImage(thumbBytes))
        {
            Assert.Equal(MagickFormat.WebP, thumbImg.Format);
            Assert.Equal(480u, thumbImg.Width);
            Assert.Equal(320u, thumbImg.Height);
        }

        // Bob fetches omitted variant (original default) and ?variant=original -> 1200x800 PNG
        var origRedir = await bobClient.GetAsync($"/api/conversations/{convId}/messages/{msgId}/media/{photoMediaId}");
        Assert.Equal(HttpStatusCode.Redirect, origRedir.StatusCode);
        var origBytes = await (await bobClient.GetAsync(origRedir.Headers.Location!)).Content.ReadAsByteArrayAsync();
        using (var origImg = new MagickImage(origBytes))
        {
            Assert.Equal(MagickFormat.Png, origImg.Format);
            Assert.Equal(1200u, origImg.Width);
            Assert.Equal(800u, origImg.Height);
        }

        // Force-download with ?variant=thumb&download=true ALWAYS returns original
        var dlRedir = await bobClient.GetAsync($"/api/conversations/{convId}/messages/{msgId}/media/{photoMediaId}?variant=thumb&download=true");
        Assert.Equal(HttpStatusCode.Redirect, dlRedir.StatusCode);
        var dlBytes = await (await bobClient.GetAsync(dlRedir.Headers.Location!)).Content.ReadAsByteArrayAsync();
        using (var dlImg = new MagickImage(dlBytes))
        {
            Assert.Equal(1200u, dlImg.Width);
        }

        // Animated GIF ?variant=thumb falls back cleanly to original GIF
        var gifFallbackRedir = await bobClient.GetAsync($"/api/conversations/{convId}/messages/{msgId}/media/{gifMediaId}?variant=thumb");
        Assert.Equal(HttpStatusCode.Redirect, gifFallbackRedir.StatusCode);
        var gifDlBytes = await (await bobClient.GetAsync(gifFallbackRedir.Headers.Location!)).Content.ReadAsByteArrayAsync();
        using (var gifFrames = new MagickImageCollection(gifDlBytes))
        {
            Assert.Equal(2, gifFrames.Count);
        }

        // Invalid variant fails with 400 Bad Request
        var invalidVarResp = await bobClient.GetAsync($"/api/conversations/{convId}/messages/{msgId}/media/{photoMediaId}?variant=micro");
        Assert.Equal(HttpStatusCode.BadRequest, invalidVarResp.StatusCode);

        // Delete-for-everyone scrubs both media/ and thumbs/ files
        string photoObjKey;
        string photoThumbKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var mediaRow = await db.Media.AsNoTracking().SingleAsync(m => m.Id == new MediaId(Guid.Parse(photoMediaId)));
            photoObjKey = mediaRow.ObjectKey;
            photoThumbKey = mediaRow.ThumbnailObjectKey!;
            Assert.StartsWith("thumbs/", photoThumbKey);
        }

        using var delMsgReq = CreateCsrfRequest(HttpMethod.Post, $"/api/conversations/{convId}/messages/{msgId}/delete-for-everyone", aliceCsrf);
        Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(delMsgReq)).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
            await cleanup.RunCleanupPassAsync();
        }

        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, photoObjKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, photoThumbKey.Replace('/', Path.DirectorySeparatorChar))));
    }

    // =========================================================================
    // 4. Concurrency Race Guard & 30-Day Soft-Deleted Recovery Cleanup Safety
    // =========================================================================

    [Fact]
    public async Task AvatarConcurrencyAndCleanup_ProtectsNewerRemoveAnd30DaySoftDeletedUsers()
    {
        var baseTime = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(baseTime);

        var services = _fixture.CreateServiceCollection();
        services.AddScoped<IAvatarPersistence, AvatarPersistence>();
        services.TryAddSingleton<ImageProcessingGate>();
        services.TryAddSingleton<IAvatarProcessor, AvatarProcessor>();
        services.Replace(ServiceDescriptor.Singleton<TimeProvider>(timeProvider));
        services.Replace(ServiceDescriptor.Singleton(sp =>
            new LocalDiskMediaStorage(sp.GetRequiredService<LocalMediaSigningService>(), _mediaTempRoot)));
        services.Replace(ServiceDescriptor.Singleton<IMediaStorage>(sp =>
            sp.GetRequiredService<LocalDiskMediaStorage>()));

        await using var sp = services.BuildServiceProvider();

        Guid userAId;
        Guid softDeletedUserId;
        string softDelFullKey = $"{MediaConstraints.AvatarKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";
        string softDelThumbKey = $"{MediaConstraints.AvatarKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";
        string orphanAvatarKey = $"{MediaConstraints.AvatarKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";
        string orphanThumbKey = $"{MediaConstraints.ThumbnailKeyPrefix}{MediaConstraints.GenerateStorageObjectId()}";

        using (var scope = sp.CreateScope())
        {
            var userA = await SeedConfirmedUserAsync(scope.ServiceProvider, "race_a", "Race A");
            var softDel = await SeedConfirmedUserAsync(scope.ServiceProvider, "soft_rec", "Soft Rec");
            userAId = userA.Id;
            softDeletedUserId = softDel.Id;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var softEntity = await db.Users.SingleAsync(u => u.Id == softDeletedUserId);
            softEntity.IsDeleted = true;
            softEntity.DeletedAtUtc = baseTime.AddDays(-5);
            softEntity.ScheduledPermanentDeletionAtUtc = baseTime.AddDays(25);
            softEntity.AvatarObjectKey = softDelFullKey;
            softEntity.AvatarThumbnailObjectKey = softDelThumbKey;
            softEntity.AvatarVersion = "keepme1234567890";
            softEntity.AvatarUpdatedAtUtc = baseTime.AddDays(-10);
            await db.SaveChangesAsync();
        }

        // Write aged files on disk (> orphan grace window)
        var oldModified = baseTime.AddHours(-2).UtcDateTime;
        foreach (var k in new[] { softDelFullKey, softDelThumbKey, orphanAvatarKey, orphanThumbKey })
        {
            var path = Path.Combine(_mediaTempRoot, k.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, CreateGradientPngBytes(64, 64));
            File.SetLastWriteTimeUtc(path, oldModified);
        }

        // 1. Concurrent upload vs remove ordering guard (Red-Team #7)
        AvatarUploadId uploadId;
        using (var scope = sp.CreateScope())
        {
            var avatarSvc = scope.ServiceProvider.GetRequiredService<IAvatarService>();
            await Assert.ThrowsAsync<DomainException>(() => avatarSvc.InitiateUploadAsync(
                new UserId(userAId),
                new InitiateAvatarUploadRequestDto("stale.png", "image/png", 0)));
        }

        var pngBytes = CreateGradientPngBytes(300, 300);
        using (var scope = sp.CreateScope())
        {
            var avatarSvc = scope.ServiceProvider.GetRequiredService<IAvatarService>();
            var init = await avatarSvc.InitiateUploadAsync(
                new UserId(userAId),
                new InitiateAvatarUploadRequestDto("stale.png", "image/png", pngBytes.Length));
            uploadId = init.UploadId;

            // Write raw staged file directly to pending staging path on disk
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var uploadRow = await db.PendingAvatarUploads.AsNoTracking().SingleAsync(p => p.Id == uploadId);
            var stagedDiskPath = Path.Combine(_mediaTempRoot, uploadRow.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(stagedDiskPath)!);
            await File.WriteAllBytesAsync(stagedDiskPath, pngBytes);

            // Simulate user clicking Remove Photo at t+5s BEFORE stale CompleteUploadAsync commits
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            await avatarSvc.RemoveAvatarAsync(new UserId(userAId));

            timeProvider.Advance(TimeSpan.FromSeconds(2));
            var staleResult = await avatarSvc.CompleteUploadAsync(new UserId(userAId), uploadId);
            Assert.Null(staleResult.AvatarVersion);

            var userRow = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userAId);
            Assert.Null(userRow.AvatarObjectKey);
            Assert.Null(userRow.AvatarThumbnailObjectKey);
            Assert.Null(userRow.AvatarVersion);

            // 1b. Same-tick RemoveAvatar vs stale upload protection (>= guard on AvatarUpdatedAtUtc) and ConcurrencyStamp rotation ([B6])
            var initialStamp = userRow.ConcurrencyStamp;
            var sameTick = timeProvider.GetUtcNow().AddSeconds(10);
            timeProvider.Advance(TimeSpan.FromSeconds(10));

            var initSameTick = await avatarSvc.InitiateUploadAsync(
                new UserId(userAId),
                new InitiateAvatarUploadRequestDto("sametick.png", "image/png", pngBytes.Length));
            var uploadSameTickRow = await db.PendingAvatarUploads.AsNoTracking().SingleAsync(p => p.Id == initSameTick.UploadId);
            var stagedSameTickPath = Path.Combine(_mediaTempRoot, uploadSameTickRow.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(stagedSameTickPath)!);
            await File.WriteAllBytesAsync(stagedSameTickPath, pngBytes);

            // Remove happens in the exact same clock tick as Initiate (sameTick)
            await avatarSvc.RemoveAvatarAsync(new UserId(userAId));
            var afterRemoveRow = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userAId);
            Assert.NotEqual(initialStamp, afterRemoveRow.ConcurrencyStamp);
            Assert.Equal(sameTick, afterRemoveRow.AvatarUpdatedAtUtc);

            // Completing the same-tick upload must NOT resurrect the deleted photo
            var sameTickResult = await avatarSvc.CompleteUploadAsync(new UserId(userAId), initSameTick.UploadId);
            Assert.Null(sameTickResult.AvatarVersion);
            Assert.Null((await db.Users.AsNoTracking().SingleAsync(u => u.Id == userAId)).AvatarObjectKey);

            // Advancing clock and completing a new upload rotates ConcurrencyStamp again and detects stale concurrent updates
            timeProvider.Advance(TimeSpan.FromSeconds(5));
            var initCommit = await avatarSvc.InitiateUploadAsync(
                new UserId(userAId),
                new InitiateAvatarUploadRequestDto("fresh.png", "image/png", pngBytes.Length));
            var uploadCommitRow = await db.PendingAvatarUploads.AsNoTracking().SingleAsync(p => p.Id == initCommit.UploadId);
            var stagedCommitPath = Path.Combine(_mediaTempRoot, uploadCommitRow.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(stagedCommitPath)!);
            await File.WriteAllBytesAsync(stagedCommitPath, pngBytes);

            await using var staleDb = _fixture.CreateDbContext();
            var staleUser = await staleDb.Users.SingleAsync(u => u.Id == userAId);
            var preCommitStamp = staleUser.ConcurrencyStamp;

            var committed = await avatarSvc.CompleteUploadAsync(new UserId(userAId), initCommit.UploadId);
            Assert.False(string.IsNullOrWhiteSpace(committed.AvatarVersion));

            var postCommitUser = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userAId);
            Assert.NotEqual(preCommitStamp, postCommitUser.ConcurrencyStamp);

            staleUser.DisplayName = "Stale Concurrent Writer";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleDb.SaveChangesAsync());
        }

        // 2. MediaCleanupService sweeps orphans & stale pending uploads, PRESERVING soft-deleted user's avatars
        timeProvider.Advance(TimeSpan.FromHours(1));
        using (var scope = sp.CreateScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
            var res = await cleanup.RunCleanupPassAsync();
            Assert.True(res.OrphanObjectsDeletedCount >= 2);
        }

        Assert.True(File.Exists(Path.Combine(_mediaTempRoot, softDelFullKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.True(File.Exists(Path.Combine(_mediaTempRoot, softDelThumbKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, orphanAvatarKey.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(_mediaTempRoot, orphanThumbKey.Replace('/', Path.DirectorySeparatorChar))));

        // [S1] Uploaded PendingAvatarUploads metadata rows older than PurgedMetadataRetentionHours are swept while active user avatar files stay intact
        timeProvider.Advance(TimeSpan.FromHours(50));
        using (var scope = sp.CreateScope())
        {
            var cleanup = scope.ServiceProvider.GetRequiredService<MediaCleanupService>();
            var retentionRes = await cleanup.RunCleanupPassAsync();
            Assert.True(retentionRes.MetadataRowsDeletedCount >= 1);

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await db.PendingAvatarUploads.AsNoTracking().AnyAsync(p => p.CreatedAtUtc < timeProvider.GetUtcNow().AddHours(-49) && p.State == MediaState.Uploaded));

            var liveUser = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userAId);
            Assert.False(string.IsNullOrWhiteSpace(liveUser.AvatarObjectKey));
            Assert.True(File.Exists(Path.Combine(_mediaTempRoot, liveUser.AvatarObjectKey!.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    // =========================================================================
    // 5. Frontend Node.js ES Module Tests (avatar-loader, cropper, lightbox, renderer)
    // =========================================================================

    [Fact]
    public async Task FrontendModules_AvatarLoaderCropperLightboxAndThumbnails_BehaveAccuratelyInNode()
    {
        var root = FindSolutionRoot();
        string JsUrl(string name) => new Uri(Path.Combine(root, "src", "AOS.Messenger.Web", "wwwroot", "js", name)).AbsoluteUri;

        var script = $$"""
            import assert from 'node:assert/strict';

            const docListeners = new Map();
            let errorEventsFromEmptySrc = 0;
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
                    textContent: '',
                    get id() { return this._id; },
                    set id(v) { this._id = String(v || ''); attrs.id = this._id; },
                    get className() { return this._className; },
                    set className(v) { this._className = String(v || ''); attrs.class = this._className; },
                    get src() { return this._src; },
                    set src(v) {
                        this._src = String(v ?? '');
                        attrs.src = this._src;
                        // Browser-faithful regression guard: setting img.src = '' re-adds src="" and queues an error event.
                        if ((this.tagName === 'IMG' || this.tagName === 'VIDEO') && this._src === '') {
                            errorEventsFromEmptySrc++;
                            if (errorEventsFromEmptySrc > 10) {
                                throw new Error('Infinite error-event loop detected from empty src assignment');
                            }
                            this.dispatchEvent({ type: 'error', target: this });
                        }
                    },
                    appendChild(c) { c.parentNode = this; this.children.push(c); return c; },
                    insertBefore(c, ref) {
                        c.parentNode = this;
                        const idx = ref ? this.children.indexOf(ref) : -1;
                        if (idx >= 0) this.children.splice(idx, 0, c);
                        else this.children.push(c);
                        return c;
                    },
                    removeChild(c) { this.children = this.children.filter(x => x !== c); return c; },
                    remove() { if (this.parentNode) this.parentNode.removeChild(this); },
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
                    getAttributeNames() { return Object.keys(attrs); },
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
                querySelector: (s) => walk(body, n => n !== body && s.split(',').some(p => matchesSingle(n, p.trim())))[0] || null,
                querySelectorAll: (s) => walk(body, n => n !== body && s.split(',').some(p => matchesSingle(n, p.trim()))),
                addEventListener(t, fn) { if (!docListeners.has(t)) docListeners.set(t, new Set()); docListeners.get(t).add(fn); },
                removeEventListener(t, fn) { docListeners.get(t)?.delete(fn); },
                dispatchEvent(e) { for (const fn of docListeners.get(e.type) || []) fn(e); }
            };
            globalThis.window = { addEventListener() {}, removeEventListener() {} };

            const { isSafeMediaUrl } = await import('{{JsUrl("media-lightbox.js")}}');
            const avatarLoader = await import('{{JsUrl("avatar-loader.js")}}');
            const { validateAvatarSourceFile } = await import('{{JsUrl("avatar-cropper.js")}}');
            const { createMessageBubbleElement } = await import('{{JsUrl("message-renderer.js")}}');

            // 1. Safe URL validation ([W8]: static pack WebP paths /stickers/packs/*.webp are allowlisted)
            assert.equal(isSafeMediaUrl('/api/users/11111111-1111-1111-1111-111111111111/avatar?size=thumb'), true);
            assert.equal(isSafeMediaUrl('/api/stickers/11111111-1111-1111-1111-111111111111/image'), true);
            assert.equal(isSafeMediaUrl('/stickers/packs/aos-greetings/hi.webp'), true);
            assert.equal(isSafeMediaUrl('/stickers/packs/../../secret.webp'), false);
            assert.equal(isSafeMediaUrl('\\\\evil\\share'), false);

            // [S5] Bounded LRU Map helper caps signed URL cache maps cleanly
            const { setBoundedMapEntry, createCachedImageLoader } = await import('{{JsUrl("memory-image-cache.js")}}');
            const boundedMap = new Map();
            setBoundedMapEntry(boundedMap, 'k1', 1, 2);
            setBoundedMapEntry(boundedMap, 'k2', 2, 2);
            setBoundedMapEntry(boundedMap, 'k1', 10, 2);
            setBoundedMapEntry(boundedMap, 'k3', 3, 2);
            assert.deepEqual([...boundedMap.entries()], [['k1', 10], ['k3', 3]]);

            // 2. Cropper validation
            assert.equal(validateAvatarSourceFile({ name: 'cam.jpg', type: 'image/jpeg', size: 12 * 1024 * 1024 }).ok, true);
            assert.equal(validateAvatarSourceFile({ name: 'huge.jpg', type: 'image/jpeg', size: 16 * 1024 * 1024 }).ok, false);
            assert.equal(validateAvatarSourceFile({ name: 'bad.svg', type: 'image/svg+xml', size: 100 }).ok, false);
            assert.equal(validateAvatarSourceFile({ name: 'bad.gif', type: 'image/gif', size: 100 }).ok, false);

            // 3. Avatar loader coalesces 5 slots into 1 thumb call and 0 full calls until clicked
            const peerId = '22222222-2222-2222-2222-222222222222';
            const calls = [];
            const mockFetchAvatarUrl = async (uid, { size }) => {
                calls.push({ uid, size });
                return {
                    url: `https://r2.example.com/avatars/${uid}?size=${size}`,
                    expiresAtUtc: new Date(Date.now() + 300_000).toISOString(),
                    version: 'v1',
                    size
                };
            };

            for (let i = 0; i < 5; i++) {
                const slot = document.createElement('div');
                slot.setAttribute('data-avatar-slot', 'true');
                slot.setAttribute('data-avatar-user-id', peerId);
                slot.setAttribute('data-avatar-version', 'v1');
                slot.setAttribute('data-avatar-name', 'Peer Name');
                if (i === 0) slot.setAttribute('data-avatar-clickable', 'true');
                body.appendChild(slot);
            }

            await avatarLoader.hydrateAllAvatars(document, { getAvatarUrlFn: mockFetchAvatarUrl });
            assert.equal(calls.length, 1);
            assert.equal(calls[0].size, 'thumb');

            const imgs = document.querySelectorAll('[data-avatar-img]');
            assert.equal(imgs.length, 5);
            for (const im of imgs) {
                assert.equal(im.hasAttribute('onerror'), false);
                assert.equal(im.hasAttribute('crossorigin'), false);
                assert.ok(im.src.includes('size=thumb'));
            }

            // Clicking opens lightbox and loads ?size=full strictly on demand
            await avatarLoader.openAvatarLightbox(peerId, 'v1', 'Peer Name', { getAvatarUrlFn: mockFetchAvatarUrl });
            assert.equal(calls.length, 2);
            assert.equal(calls[1].size, 'full');
            assert.equal(avatarLoader.isAvatarLightboxOpen(), true);
            assert.ok(document.getElementById('avatar-lightbox-img').src.includes('size=full'));

            // updateUserAvatarAcrossDom(peerId, null) reverts DOM to initials & auto-closes lightbox
            // without setting src="" or triggering any error-event loop
            await avatarLoader.updateUserAvatarAcrossDom(peerId, null, 'Deleted Account', { getAvatarUrlFn: mockFetchAvatarUrl });
            assert.equal(avatarLoader.isAvatarLightboxOpen(), false);
            for (const im of imgs) {
                assert.equal(im.classList.contains('hidden'), true);
                assert.equal(im.getAttribute('src'), null);
            }
            assert.equal(document.getElementById('avatar-lightbox-img').getAttribute('src'), null);

            // Hydrating a slot with no avatar must not create an empty-src <img> or trigger error events
            const noPhotoSlot = document.createElement('div');
            noPhotoSlot.setAttribute('data-avatar-slot', 'true');
            noPhotoSlot.setAttribute('data-avatar-user-id', '55555555-5555-5555-5555-555555555555');
            noPhotoSlot.setAttribute('data-avatar-version', '');
            noPhotoSlot.setAttribute('data-avatar-name', 'No Photo User');
            body.appendChild(noPhotoSlot);
            await avatarLoader.hydrateAvatarSlot(noPhotoSlot, { getAvatarUrlFn: mockFetchAvatarUrl });
            assert.equal(noPhotoSlot.querySelector('[data-avatar-img]'), null);
            assert.equal(errorEventsFromEmptySrc, 0);

            // 4. Message renderer uses ?variant=thumb for inline photos
            const convId = '33333333-3333-3333-3333-333333333333';
            const mediaId = '44444444-4444-4444-4444-444444444444';
            const row = createMessageBubbleElement({
                id: 101,
                conversationId: convId,
                senderId: peerId,
                senderName: 'Peer',
                senderHandle: 'peer',
                senderAvatarVersion: '',
                createdAtUtc: new Date().toISOString(),
                isOutgoing: true,
                attachments: [{ id: mediaId, kind: 'image', fileName: 'pic.jpg', width: 800, height: 600, hasThumbnail: true }]
            }, peerId);
            const bubbleImg = row.querySelector('img[data-media-img="true"]');
            assert.ok(bubbleImg.src.endsWith(`/api/conversations/${convId}/messages/101/media/${mediaId}?variant=thumb`));

            // 5. Full-size avatar RAM Blob LRU cache: 0 repeat fetches + evicts on update/block/logout
            const avatarPeer3 = '66666666-6666-6666-6666-666666666666';
            let fullUrlFetchCalls = 0;
            let blobFetchCalls = 0;
            let blobCounter = 0;
            const revokedAvatarBlobs = [];
            const mockAvatarUrlFn = async (uid, { size }) => {
                fullUrlFetchCalls++;
                return {
                    url: `https://r2.example.com/avatars/${uid}?size=${size}&seq=${fullUrlFetchCalls}`,
                    expiresAtUtc: new Date(Date.now() + 300_000).toISOString(),
                    version: 'v1',
                    size
                };
            };
            const mockBlobFetchFn = async () => {
                blobFetchCalls++;
                return {
                    ok: true,
                    status: 200,
                    headers: { get: () => 'image/webp' },
                    blob: async () => ({ size: 4096, type: 'image/webp' })
                };
            };
            const mockCreateObjUrl = () => `blob:https://localhost/avatar-${++blobCounter}`;
            const mockRevokeObjUrl = (u) => revokedAvatarBlobs.push(u);

            const opened1 = await avatarLoader.openAvatarLightbox(avatarPeer3, 'v1', 'Avatar Peer', {
                getAvatarUrlFn: mockAvatarUrlFn,
                fetchFn: mockBlobFetchFn,
                createObjectUrlFn: mockCreateObjUrl,
                revokeObjectUrlFn: mockRevokeObjUrl
            });
            assert.equal(opened1, true);
            assert.equal(fullUrlFetchCalls, 1);
            assert.equal(blobFetchCalls, 1);
            assert.equal(document.getElementById('avatar-lightbox-img').src, 'blob:https://localhost/avatar-1');
            assert.equal(avatarLoader.getAvatarFullBlobCacheStats().count, 1);

            // Closing modal keeps Blob ObjectURL alive in tab RAM LRU cache
            avatarLoader.closeAvatarLightbox();
            assert.equal(revokedAvatarBlobs.length, 0);

            // Re-opening same (userId, version) does 0 /url calls and 0 Blob fetches
            const opened2 = await avatarLoader.openAvatarLightbox(avatarPeer3, 'v1', 'Avatar Peer', {
                getAvatarUrlFn: mockAvatarUrlFn,
                fetchFn: mockBlobFetchFn,
                createObjectUrlFn: mockCreateObjUrl,
                revokeObjectUrlFn: mockRevokeObjUrl
            });
            assert.equal(opened2, true);
            assert.equal(fullUrlFetchCalls, 1);
            assert.equal(blobFetchCalls, 1);
            assert.equal(document.getElementById('avatar-lightbox-img').src, 'blob:https://localhost/avatar-1');
            avatarLoader.closeAvatarLightbox();

            // evictUserAvatarCache (block/remove) immediately revokes the cached Blob ObjectURL
            avatarLoader.evictUserAvatarCache(avatarPeer3);
            assert.equal(avatarLoader.getAvatarFullBlobCacheStats().count, 0);
            assert.deepEqual(revokedAvatarBlobs, ['blob:https://localhost/avatar-1']);

            // Re-opening after eviction fetches cleanly again, and clearAvatarUrlCache (logout) wipes all Blobs
            await avatarLoader.openAvatarLightbox(avatarPeer3, 'v1', 'Avatar Peer', {
                getAvatarUrlFn: mockAvatarUrlFn,
                fetchFn: mockBlobFetchFn,
                createObjectUrlFn: mockCreateObjUrl,
                revokeObjectUrlFn: mockRevokeObjUrl
            });
            assert.equal(blobFetchCalls, 2);
            avatarLoader.clearAvatarUrlCache();
            assert.equal(avatarLoader.isAvatarLightboxOpen(), false);
            assert.equal(avatarLoader.getAvatarFullBlobCacheStats().count, 0);
            assert.deepEqual(revokedAvatarBlobs, ['blob:https://localhost/avatar-1', 'blob:https://localhost/avatar-2']);

            // Coalesced in-flight transient (oversized) avatar Blob stays alive on same-key reopen, revokes on permanent close
            let releaseHugeUrl;
            const hugeRevokes = [];
            let hugeBlobSeq = 0;
            const hugeGetUrl = () => new Promise(resolve => {
                releaseHugeUrl = () => resolve({
                    url: `https://r2.example.com/avatars/${avatarPeer3}-v-huge-full`,
                    expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
                    version: 'v-huge',
                    size: 'full'
                });
            });
            const hugeFetch = async () => ({
                ok: true,
                status: 200,
                headers: { get: () => 'image/webp' },
                blob: async () => ({ size: 35 * 1024 * 1024, type: 'image/webp' })
            });
            const hugeCreateUrl = () => `blob:https://localhost/huge-avatar-${++hugeBlobSeq}`;
            const hugeRevokeUrl = (u) => hugeRevokes.push(u);

            const firstHugePromise = avatarLoader.openAvatarLightbox(avatarPeer3, 'v-huge', 'Huge Peer', {
                getAvatarUrlFn: hugeGetUrl,
                fetchFn: hugeFetch,
                createObjectUrlFn: hugeCreateUrl,
                revokeObjectUrlFn: hugeRevokeUrl
            });
            await new Promise(r => setTimeout(r, 10));
            avatarLoader.closeAvatarLightbox();
            const secondHugePromise = avatarLoader.openAvatarLightbox(avatarPeer3, 'v-huge', 'Huge Peer', {
                getAvatarUrlFn: hugeGetUrl,
                fetchFn: hugeFetch,
                createObjectUrlFn: hugeCreateUrl,
                revokeObjectUrlFn: hugeRevokeUrl
            });
            await new Promise(r => setTimeout(r, 10));
            releaseHugeUrl();
            const [res1, res2] = await Promise.all([firstHugePromise, secondHugePromise]);
            assert.equal(res1, false);
            assert.equal(res2, true);
            assert.deepEqual(hugeRevokes, []);
            assert.equal(document.getElementById('avatar-lightbox-img').src, 'blob:https://localhost/huge-avatar-1');
            avatarLoader.closeAvatarLightbox();
            assert.deepEqual(hugeRevokes, ['blob:https://localhost/huge-avatar-1']);

            // Permanent close before resolution revokes stale transient Blob
            const thirdHugePromise = avatarLoader.openAvatarLightbox(avatarPeer3, 'v-huge2', 'Huge Peer', {
                getAvatarUrlFn: hugeGetUrl,
                fetchFn: hugeFetch,
                createObjectUrlFn: hugeCreateUrl,
                revokeObjectUrlFn: hugeRevokeUrl
            });
            await new Promise(r => setTimeout(r, 10));
            avatarLoader.closeAvatarLightbox();
            releaseHugeUrl();
            assert.equal(await thirdHugePromise, false);
            assert.deepEqual(hugeRevokes, ['blob:https://localhost/huge-avatar-1', 'blob:https://localhost/huge-avatar-2']);

            // Parked CORS fallback avatar <img> error retries once and displays Photo unavailable on failure
            let corsFallbackCalls = 0;
            let failFallbackRetry = false;
            const corsFallbackGetUrl = async () => {
                corsFallbackCalls++;
                if (failFallbackRetry) return null;
                return {
                    url: `https://r2.example.com/avatars/${avatarPeer3}-cors-full`,
                    expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
                    version: 'v-cors',
                    size: 'full'
                };
            };
            await avatarLoader.openAvatarLightbox(avatarPeer3, 'v-cors', 'CORS Peer', {
                getAvatarUrlFn: corsFallbackGetUrl,
                fetchFn: async () => { throw new TypeError('Failed to fetch'); }
            });
            const parkedAvatarFallbackEl = document.getElementById('avatar-lightbox-img');
            parkedAvatarFallbackEl.dispatchEvent({ type: 'load' });
            avatarLoader.closeAvatarLightbox();

            await avatarLoader.openAvatarLightbox(avatarPeer3, 'v-cors', 'CORS Peer', {
                getAvatarUrlFn: corsFallbackGetUrl,
                fetchFn: async () => { throw new TypeError('Failed to fetch'); }
            });
            assert.equal(corsFallbackCalls, 1);
            failFallbackRetry = true;
            parkedAvatarFallbackEl.dispatchEvent({ type: 'error' });
            await new Promise(r => setTimeout(r, 20));
            assert.equal(corsFallbackCalls, 2);
            assert.equal(parkedAvatarFallbackEl.classList.contains('hidden'), true);
            assert.equal(document.getElementById('avatar-lightbox-error').textContent, 'Photo unavailable');
            assert.equal(document.getElementById('avatar-lightbox-error').classList.contains('hidden'), false);
            avatarLoader.closeAvatarLightbox();

            // [W16] Name-only updateUserAvatarAcrossDom preserves open CORS-fallback lightbox (both active and parked); version change reloads it
            let nameUpdateCalls = 0;
            let currentCorsVer = 'v-cors-name';
            const nameUpdateGetUrl = async () => {
                nameUpdateCalls++;
                return {
                    url: `https://r2.example.com/avatars/${avatarPeer3}-${currentCorsVer}`,
                    expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
                    version: currentCorsVer,
                    size: 'full'
                };
            };
            await avatarLoader.openAvatarLightbox(avatarPeer3, 'v-cors-name', 'Old Display Name', {
                getAvatarUrlFn: nameUpdateGetUrl,
                fetchFn: async () => { throw new TypeError('Failed to fetch'); }
            });
            const activeCorsImgEl = document.getElementById('avatar-lightbox-img');
            activeCorsImgEl.dispatchEvent({ type: 'load' });
            assert.equal(nameUpdateCalls, 1);
            assert.equal(activeCorsImgEl.classList.contains('hidden'), false);

            await avatarLoader.updateUserAvatarAcrossDom(avatarPeer3, 'v-cors-name', 'New Display Name', {
                getAvatarUrlFn: nameUpdateGetUrl,
                fetchFn: async () => { throw new TypeError('Failed to fetch'); }
            });
            assert.equal(avatarLoader.isAvatarLightboxOpen(), true);
            assert.equal(nameUpdateCalls, 1, 'Name-only update should preserve unparked CORS-fallback lightbox without refetching');
            assert.equal(document.getElementById('avatar-lightbox-title').textContent, 'New Display Name');
            assert.equal(activeCorsImgEl.classList.contains('hidden'), false);
            assert.ok(activeCorsImgEl.getAttribute('src')?.includes('v-cors-name'));

            // Close and reopen so the fallback becomes a parked fallback element, then apply a second name-only update
            avatarLoader.closeAvatarLightbox();
            await avatarLoader.openAvatarLightbox(avatarPeer3, 'v-cors-name', 'New Display Name', {
                getAvatarUrlFn: nameUpdateGetUrl,
                fetchFn: async () => { throw new TypeError('Failed to fetch'); }
            });
            assert.equal(nameUpdateCalls, 1);
            await avatarLoader.updateUserAvatarAcrossDom(avatarPeer3, 'v-cors-name', 'Renamed Twice', {
                getAvatarUrlFn: nameUpdateGetUrl,
                fetchFn: async () => { throw new TypeError('Failed to fetch'); }
            });
            assert.equal(avatarLoader.isAvatarLightboxOpen(), true);
            assert.equal(nameUpdateCalls, 1, 'Name-only update must preserve parked CORS-fallback element too');
            assert.equal(document.getElementById('avatar-lightbox-title').textContent, 'Renamed Twice');
            assert.equal(activeCorsImgEl.classList.contains('hidden'), false);
            assert.ok(activeCorsImgEl.getAttribute('src')?.includes('v-cors-name'));

            currentCorsVer = 'v-cors-name-2';
            await avatarLoader.updateUserAvatarAcrossDom(avatarPeer3, 'v-cors-name-2', 'Renamed Twice', {
                getAvatarUrlFn: nameUpdateGetUrl,
                fetchFn: async () => { throw new TypeError('Failed to fetch'); }
            });
            await new Promise(r => setTimeout(r, 20));
            assert.equal(nameUpdateCalls, 2, 'Version change should reload open avatar lightbox');
            avatarLoader.closeAvatarLightbox();

            const staleUser = 'a0a0a0a0-b1b1-c2c2-d3d3-e4e4e4e4e4e4';
            let resolveStaleAvatar;
            const staleRequest = avatarLoader.resolveAvatarUrl(staleUser, 'v-stale', {
                getAvatarUrlFn: () => new Promise(resolve => { resolveStaleAvatar = resolve; })
            });
            avatarLoader.evictUserAvatarCache(staleUser);
            resolveStaleAvatar({ url: 'https://r2.example.com/stale-avatar' });
            assert.equal(await staleRequest, null);
            assert.equal(await avatarLoader.resolveAvatarUrl(staleUser, 'v-stale', {
                getAvatarUrlFn: async () => ({ url: 'https://r2.example.com/fresh-avatar' })
            }), 'https://r2.example.com/fresh-avatar');

            // Concurrent updateUserAvatarAcrossDom calls for the SAME new version (profile onUploaded + SignalR ReceiveUserProfileUpdated)
            // must coalesce onto one fetch and keep the hydrated <img> visible (not cleared back to fallback initials)
            const dualUpdateUser = 'b1b1b1b1-c2c2-d3d3-e4e4-f5f5f5f5f5f5';
            const dualSlot = createEl('div');
            dualSlot.setAttribute('data-avatar-slot', 'true');
            dualSlot.setAttribute('data-avatar-user-id', dualUpdateUser);
            dualSlot.setAttribute('data-avatar-version', '');
            dualSlot.setAttribute('data-avatar-name', 'Dual User');
            body.appendChild(dualSlot);
            let dualUrlCalls = 0;
            let resolveDualUrl;
            const dualGetUrlFn = () => {
                dualUrlCalls++;
                return new Promise(resolve => { resolveDualUrl = resolve; });
            };
            const updateCall1 = avatarLoader.updateUserAvatarAcrossDom(dualUpdateUser, 'v-dual-new', 'Dual User', { getAvatarUrlFn: dualGetUrlFn });
            const updateCall2 = avatarLoader.updateUserAvatarAcrossDom(dualUpdateUser, 'v-dual-new', 'Dual User', { getAvatarUrlFn: dualGetUrlFn });
            assert.equal(dualUrlCalls, 1, 'Simultaneous updateUserAvatarAcrossDom calls for the same version must coalesce without self-eviction');
            resolveDualUrl({ url: 'https://r2.example.com/dual-new-thumb', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
            await Promise.all([updateCall1, updateCall2]);
            const dualImg = dualSlot.querySelector('[data-avatar-img]');
            assert.ok(dualImg);
            assert.equal(dualImg.src, 'https://r2.example.com/dual-new-thumb');
            assert.equal(dualImg.classList.contains('hidden'), false, 'Same-version concurrent avatar update must leave <img> visible immediately');

            const imageLoader = createCachedImageLoader();
            let resolveImage;
            let imageLookups = 0;
            const imageOptions = {
                cacheKey: 'retry-after-missing',
                getSignedUrlFn: () => {
                    imageLookups++;
                    return new Promise(resolve => { resolveImage = resolve; });
                }
            };
            const missingImage = imageLoader.load(imageOptions);
            const joinedImage = imageLoader.load(imageOptions);
            assert.equal(imageLookups, 1);
            resolveImage(null);
            assert.equal((await missingImage).reason, 'missing-url');
            assert.equal((await joinedImage).reason, 'missing-url');
            const nextImage = imageLoader.load(imageOptions);
            assert.equal(imageLookups, 2);
            resolveImage(null);
            await nextImage;

            // [W15] avatar-cropper.js decode-vs-close, AbortController upload cancellation & listener cleanup
            const { createAvatarCropper } = await import('{{JsUrl("avatar-cropper.js")}}');
            const origCreateElement = document.createElement;
            document.createElement = (tag) => {
                const el = origCreateElement(tag);
                if (String(tag).toLowerCase() === 'canvas') {
                    el.getContext = () => ({
                        clearRect() {},
                        fillRect() {},
                        drawImage() {},
                        save() {},
                        restore() {},
                        beginPath() {},
                        rect() {},
                        arc() {},
                        fill() {},
                        stroke() {}
                    });
                    el.toBlob = (cb) => cb({ size: 64, type: 'image/webp' });
                }
                return el;
            };

            let resolveBitmap;
            let closedBitmaps = 0;
            const origCreateImageBitmap = globalThis.createImageBitmap;
            globalThis.createImageBitmap = () => new Promise((res) => { resolveBitmap = res; });

            let initSignals = [];
            let xhrCreatedCount = 0;
            let uploadedCallbacks = 0;
            let cropperErrors = [];
            let resolveInitiate;
            let rejectCompletion;
            let statusLookups = 0;
            const initialKeydownCount = docListeners.get('keydown')?.size || 0;

            const cropper = createAvatarCropper({
                onUploaded: () => { uploadedCallbacks++; },
                onError: (msg) => { cropperErrors.push(msg); },
                api: {
                    initiateAvatarUpload: (_payload, { signal } = {}) => {
                        initSignals.push(signal);
                        return new Promise((res) => { resolveInitiate = res; });
                    },
                    completeAvatarUpload: () => new Promise((_resolve, reject) => { rejectCompletion = reject; }),
                    getAvatarUploadStatus: async (id) => {
                        assert.equal(id, 'up-2');
                        statusLookups++;
                        return { avatarVersion: 'v-new' };
                    }
                },
                createXhr: () => {
                    xhrCreatedCount++;
                    return { upload: {}, status: 200, open() {}, setRequestHeader() {}, send() { this.onload(); }, abort() {} };
                }
            });

            const validFile = { name: 'photo.png', type: 'image/png', size: 1024 };
            const pendingOpen = cropper.open(validFile);
            await new Promise(r => setTimeout(r, 10));
            cropper.close();
            resolveBitmap({ width: 200, height: 200, close() { closedBitmaps++; } });
            assert.equal(await pendingOpen, false);
            assert.equal(closedBitmaps, 1, 'Closed modal mid-decode must dispose late ImageBitmap');
            assert.equal(cropper.isOpen(), false);
            assert.equal(document.getElementById('avatar-cropper-modal').classList.contains('hidden'), true);

            // Re-open cleanly and cancel mid-initiate
            globalThis.createImageBitmap = async () => ({ width: 200, height: 200, close() { closedBitmaps++; } });
            assert.equal(await cropper.open(validFile), true);
            assert.equal(docListeners.get('keydown')?.size || 0, initialKeydownCount + 1);
            document.getElementById('avatar-cropper-save-btn').dispatchEvent({ type: 'click', preventDefault() {} });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(initSignals.length, 1);
            assert.equal(initSignals[0].aborted, false);

            cropper.close();
            assert.equal(initSignals[0].aborted, true, 'Closing modal mid-save must abort upload request signal');
            resolveInitiate({ uploadId: 'up-1', uploadUrl: 'https://r2.example.com/up-1', requiredContentType: 'image/webp' });
            await new Promise(r => setTimeout(r, 20));
            assert.equal(xhrCreatedCount, 0, 'Cancelled cropper upload must not proceed to XHR PUT');
            assert.equal(uploadedCallbacks, 0);
            assert.deepEqual(cropperErrors, []);

            assert.equal(await cropper.open(validFile), true);
            document.getElementById('avatar-cropper-save-btn').dispatchEvent({ type: 'click' });
            await new Promise(r => setTimeout(r, 10));
            resolveInitiate({ uploadId: 'up-2', uploadUrl: 'https://r2.example.com/up-2' });
            await new Promise(r => setTimeout(r, 10));
            const cancelCropper = document.getElementById('avatar-cropper-cancel-btn');
            const closeCropper = document.getElementById('avatar-cropper-close-btn');
            assert.equal(cancelCropper.disabled, true);
            assert.equal(closeCropper.disabled, true);
            cancelCropper.dispatchEvent({ type: 'click' });
            closeCropper.dispatchEvent({ type: 'click' });
            document.dispatchEvent({ type: 'keydown', key: 'Escape', preventDefault() {} });
            assert.equal(cropper.isOpen(), true);
            assert.equal(initSignals[1].aborted, false);
            rejectCompletion(new TypeError('Connection lost after commit'));
            await new Promise(r => setTimeout(r, 10));
            assert.equal(statusLookups, 1);
            assert.equal(uploadedCallbacks, 1);
            assert.equal(cropper.isOpen(), false);

            cropper.destroy();
            assert.equal(docListeners.get('keydown')?.size || 0, initialKeydownCount, 'destroy() must remove document keydown listeners');

            // [W5] destroy() while completion backoff polling is sleeping must abort signal, hide modal, and cancel remaining retries
            let destroyUploaded = 0;
            const destroyErrors = [];
            const destroyCompleteSignals = [];
            const destroyStatusSignals = [];
            let resolveDestroyInit;
            let rejectDestroyCompletion;
            const origSetTimeout = globalThis.setTimeout;
            const origClearTimeout = globalThis.clearTimeout;
            const capturedRetryTimers = [];
            const clearedTimerIds = new Set();
            globalThis.setTimeout = (fn, delay, ...args) => {
                const id = origSetTimeout(fn, delay, ...args);
                if (delay >= 500) {
                    capturedRetryTimers.push({ id, delay });
                }
                return id;
            };
            globalThis.clearTimeout = (id) => {
                clearedTimerIds.add(id);
                return origClearTimeout(id);
            };
            try {
                const destroyCropper = createAvatarCropper({
                    onUploaded: () => { destroyUploaded++; },
                    onError: (msg) => { destroyErrors.push(msg); },
                    api: {
                        initiateAvatarUpload: () => new Promise((res) => { resolveDestroyInit = res; }),
                        completeAvatarUpload: (_id, { signal } = {}) => {
                            destroyCompleteSignals.push(signal);
                            return new Promise((_res, rej) => { rejectDestroyCompletion = rej; });
                        },
                        getAvatarUploadStatus: async (_id, { signal } = {}) => {
                            destroyStatusSignals.push(signal);
                            throw Object.assign(new Error('Service Unavailable'), { status: 503 });
                        }
                    },
                    createXhr: () => ({ upload: {}, status: 200, open() {}, setRequestHeader() {}, send() { this.onload(); }, abort() {} })
                });

                assert.equal(await destroyCropper.open(validFile), true);
                document.getElementById('avatar-cropper-save-btn').dispatchEvent({ type: 'click', preventDefault() {} });
                await new Promise(r => setTimeout(r, 10));
                resolveDestroyInit({ uploadId: 'up-destroy', uploadUrl: 'https://r2.example.com/up-destroy' });
                await new Promise(r => setTimeout(r, 10));
                assert.equal(destroyCompleteSignals.length, 1);
                assert.equal(destroyCompleteSignals[0].aborted, false);

                // Trigger 503 during first poll so completeAndReconcile enters 500ms backoff sleep
                rejectDestroyCompletion(new TypeError('Connection dropped'));
                await new Promise(r => setTimeout(r, 15));
                assert.equal(destroyStatusSignals.length, 1);
                assert.equal(capturedRetryTimers.length, 1, 'Expected 500ms backoff retry timer to be scheduled');
                assert.equal(clearedTimerIds.has(capturedRetryTimers[0].id), false);

                destroyCropper.destroy();
                assert.equal(clearedTimerIds.has(capturedRetryTimers[0].id), true, '[W5] destroy() must explicitly clear the scheduled 500ms retry timer');
                assert.equal(destroyCompleteSignals[0].aborted, true, '[W5] destroy() must abort completion signal');
                assert.equal(destroyStatusSignals[0].aborted, true, '[W5] destroy() must abort polling status signal');
                assert.equal(destroyStatusSignals.length, 1, '[W5] destroy() must stop queued polling timers cleanly');
                assert.equal(destroyCropper.isOpen(), false);
                assert.equal(document.getElementById('avatar-cropper-modal').classList.contains('hidden'), true);
                assert.equal(destroyUploaded, 0);
                assert.deepEqual(destroyErrors, []);
                assert.equal(docListeners.get('keydown')?.size || 0, initialKeydownCount);
            } finally {
                globalThis.setTimeout = origSetTimeout;
                globalThis.clearTimeout = origClearTimeout;
            }

            document.createElement = origCreateElement;
            globalThis.createImageBitmap = origCreateImageBitmap;

            // [W18] Thumb retry evicts only :thumb URL (keeps ?size=full Blob cache intact), coalesces concurrent retries, and skips detached slots
            avatarLoader.clearAvatarUrlCache();
            const w18Peer = '77777777-7777-7777-7777-777777777777';
            let w18FullRevokes = 0;
            let w18ThumbCalls = 0;
            let resolveThumbRetry;
            const w18ThumbUrlFn = async (_uid, { size }) => {
                if (size === 'full') {
                    return {
                        url: 'https://r2.example.com/avatars/w18-full',
                        expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
                        version: 'v-w18',
                        size: 'full'
                    };
                }
                w18ThumbCalls++;
                if (w18ThumbCalls === 1) {
                    return {
                        url: 'https://r2.example.com/avatars/w18-thumb-expired',
                        expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
                        version: 'v-w18',
                        size: 'thumb'
                    };
                }
                return new Promise((res) => { resolveThumbRetry = res; });
            };
            await avatarLoader.openAvatarLightbox(w18Peer, 'v-w18', 'W18 Peer', {
                getAvatarUrlFn: w18ThumbUrlFn,
                fetchFn: async () => ({
                    ok: true,
                    status: 200,
                    headers: { get: () => 'image/webp' },
                    blob: async () => ({ size: 1024, type: 'image/webp' })
                }),
                createObjectUrlFn: () => 'blob:https://localhost/w18-full-blob',
                revokeObjectUrlFn: () => { w18FullRevokes++; }
            });
            avatarLoader.closeAvatarLightbox();
            assert.equal(avatarLoader.getAvatarFullBlobCacheStats().count, 1);

            const thumbRetrySlots = [];
            for (let i = 0; i < 3; i++) {
                const s = document.createElement('div');
                s.setAttribute('data-avatar-slot', 'true');
                s.setAttribute('data-avatar-user-id', w18Peer);
                s.setAttribute('data-avatar-version', 'v-w18');
                s.setAttribute('data-avatar-name', 'W18 Peer');
                body.appendChild(s);
                thumbRetrySlots.push(s);
            }
            await avatarLoader.hydrateAllAvatars(body, { getAvatarUrlFn: w18ThumbUrlFn });
            assert.equal(w18ThumbCalls, 1);
            const retryImgs = thumbRetrySlots.map(s => s.querySelector('[data-avatar-img]'));
            for (const ri of retryImgs) ri.dispatchEvent({ type: 'error' });
            await new Promise(r => setTimeout(r, 10));
            assert.equal(w18ThumbCalls, 2, 'Concurrent thumb error retries must coalesce into a single fetch');
            resolveThumbRetry({
                url: 'https://r2.example.com/avatars/w18-thumb-fresh',
                expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
                version: 'v-w18',
                size: 'thumb'
            });
            await new Promise(r => setTimeout(r, 20));
            for (const ri of retryImgs) {
                assert.equal(ri.src, 'https://r2.example.com/avatars/w18-thumb-fresh');
            }
            assert.equal(avatarLoader.getAvatarFullBlobCacheStats().count, 1, 'Thumb retry must NOT evict cached ?size=full Blob');
            assert.equal(w18FullRevokes, 0);

            // hydrateAvatarSlot skips writing img.src if slot detaches mid-fetch
            const detachedSlot = document.createElement('div');
            detachedSlot.setAttribute('data-avatar-slot', 'true');
            detachedSlot.setAttribute('data-avatar-user-id', '88888888-8888-8888-8888-888888888888');
            detachedSlot.setAttribute('data-avatar-version', 'v-det');
            detachedSlot.isConnected = true;
            let resolveDetachUrl;
            const detachPromise = avatarLoader.hydrateAvatarSlot(detachedSlot, {
                getAvatarUrlFn: () => new Promise((res) => { resolveDetachUrl = res; })
            });
            detachedSlot.isConnected = false;
            resolveDetachUrl({
                url: 'https://r2.example.com/avatars/detached',
                expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
                version: 'v-det',
                size: 'thumb'
            });
            await detachPromise;
            assert.equal(detachedSlot.querySelector('[data-avatar-img]').getAttribute('src'), null, 'Detached slot must not receive img.src');

            // [W18] new-conversation-page.js uses apiFetch and surfaces 401/429 errors instead of false no-results
            const { configureApiClient } = await import('{{JsUrl("api-client.js")}}');
            const { initNewConversationPage } = await import('{{JsUrl("new-conversation-page.js")}}');
            const ncInput = document.createElement('input'); ncInput.id = 'user-search-input';
            const ncResults = document.createElement('div'); ncResults.id = 'state-results'; ncResults.setAttribute('data-search-state', 'results');
            const ncEmpty = document.createElement('div'); ncEmpty.id = 'state-empty'; ncEmpty.setAttribute('data-search-state', 'empty');
            const ncLoading = document.createElement('div'); ncLoading.id = 'state-loading'; ncLoading.setAttribute('data-search-state', 'loading');
            const ncNoRes = document.createElement('div'); ncNoRes.id = 'state-no-results'; ncNoRes.setAttribute('data-search-state', 'no-results'); ncNoRes.className = 'hidden';
            const ncQueryTerm = document.createElement('span'); ncQueryTerm.id = 'query-term'; ncQueryTerm.setAttribute('data-no-results-query', '');
            ncNoRes.appendChild(ncQueryTerm);
            const ncErr = document.createElement('div'); ncErr.id = 'state-search-error'; ncErr.setAttribute('data-search-state', 'error'); ncErr.className = 'hidden';
            const ncErrTitle = document.createElement('h3'); ncErrTitle.id = 'search-error-title';
            const ncErrMsg = document.createElement('p'); ncErrMsg.id = 'search-error-message';
            const ncLoginLink = document.createElement('a'); ncLoginLink.id = 'search-error-login-link'; ncLoginLink.className = 'hidden';
            ncErr.appendChild(ncErrTitle);
            ncErr.appendChild(ncErrMsg);
            ncErr.appendChild(ncLoginLink);
            for (const node of [ncInput, ncResults, ncEmpty, ncLoading, ncNoRes, ncErr]) body.appendChild(node);

            const ncClearBtn = document.createElement('button');
            ncClearBtn.id = 'clear-search-btn';
            body.appendChild(ncClearBtn);

            let ncMode = '429';
            let ncSessionExpired = 0;
            let ncLastFetchInit = null;
            let pendingSearchSignal = null;
            configureApiClient({
                onSessionExpired: () => { ncSessionExpired++; },
                fetchImpl: async (_url, init) => {
                    ncLastFetchInit = init;
                    if (ncMode === 'hang') {
                        pendingSearchSignal = init?.signal || null;
                        return await new Promise((_res, rej) => {
                            if (init?.signal) {
                                init.signal.addEventListener('abort', () => {
                                    const err = new Error('Aborted');
                                    err.name = 'AbortError';
                                    rej(err);
                                });
                            }
                        });
                    }
                    if (ncMode === '429') {
                        return {
                            ok: false,
                            status: 429,
                            headers: { get: () => 'application/problem+json' },
                            text: async () => JSON.stringify({ detail: 'Rate limit exceeded' })
                        };
                    }
                    if (ncMode === '401') {
                        return {
                            ok: false,
                            status: 401,
                            headers: { get: () => 'application/json' },
                            text: async () => ''
                        };
                    }
                    return {
                        ok: true,
                        status: 200,
                        headers: { get: () => 'application/json' },
                        text: async () => '[]'
                    };
                }
            });

            const nc = initNewConversationPage();
            await nc.performSearch('alice');
            assert.equal(ncLastFetchInit?.cache, 'no-store');
            assert.equal(ncErr.classList.contains('hidden'), false);
            assert.equal(ncNoRes.classList.contains('hidden'), true);
            assert.equal(ncErrTitle.textContent, 'Too many searches');
            assert.equal(ncLoginLink.classList.contains('hidden'), true);

            ncMode = '401';
            await nc.performSearch('alice');
            assert.equal(ncSessionExpired, 1);
            assert.equal(ncErr.classList.contains('hidden'), false);
            assert.equal(ncErrTitle.textContent, 'Session expired');
            assert.equal(ncLoginLink.classList.contains('hidden'), false);

            ncMode = 'empty';
            await nc.performSearch('nobody');
            assert.equal(ncNoRes.classList.contains('hidden'), false);
            assert.equal(ncErr.classList.contains('hidden'), true);

            // Clearing search aborts in-flight AbortController immediately
            ncMode = 'hang';
            const hangingSearch = nc.performSearch('slowpeer');
            assert.equal(pendingSearchSignal?.aborted, false);
            ncClearBtn.dispatchEvent({ type: 'click', preventDefault() {} });
            assert.equal(pendingSearchSignal?.aborted, true);
            await hangingSearch;

            nc.destroy();
            configureApiClient({ fetchImpl: null, onSessionExpired: null });

            // [Step 7.5] Avatar v1 -> v2 evicts stale v1 full blob ObjectURL, and concurrent bypassCache does not clobber normal resolveAvatarUrl
            const verPeer = '99999999-9999-9999-9999-999999999902';
            const verRevokes = [];
            let verSeq = 0;
            const verUrlFn = async (uid, { size }) => ({
                url: `https://r2.example.com/avatars/${uid}/${size}/${++verSeq}`,
                expiresAtUtc: new Date(Date.now() + 600_000).toISOString(),
                size
            });
            const verFetchFn = async () => ({
                ok: true,
                status: 200,
                headers: { get: () => 'image/webp' },
                blob: async () => ({ size: 1024, type: 'image/webp' })
            });
            await avatarLoader.openAvatarLightbox(verPeer, 'v1', 'Ver Peer', {
                getAvatarUrlFn: verUrlFn,
                fetchFn: verFetchFn,
                createObjectUrlFn: () => `blob:https://localhost/ver-${verSeq}`,
                revokeObjectUrlFn: (u) => verRevokes.push(u)
            });
            avatarLoader.closeAvatarLightbox();
            assert.equal(verRevokes.length, 0);
            avatarLoader.evictUserAvatarCache(verPeer, { preserveVersion: 'v2' });
            assert.equal(verRevokes.length, 1);

            let resNormUrl, resBypassUrl;
            const pNorm = avatarLoader.resolveAvatarUrl(verPeer, 'v2', {
                size: 'thumb',
                getAvatarUrlFn: () => new Promise(r => { resNormUrl = r; })
            });
            const pBypass = avatarLoader.resolveAvatarUrl(verPeer, 'v2', {
                size: 'thumb',
                bypassCache: true,
                getAvatarUrlFn: () => new Promise(r => { resBypassUrl = r; })
            });
            resBypassUrl({ url: 'https://r2.example.com/avatars/bypass', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
            resNormUrl({ url: 'https://r2.example.com/avatars/normal', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
            assert.equal(await pBypass, 'https://r2.example.com/avatars/bypass');
            assert.equal(await pNorm, 'https://r2.example.com/avatars/normal');

            let resClearedUrl;
            const pCleared = avatarLoader.resolveAvatarUrl(verPeer, 'v3', {
                size: 'thumb',
                getAvatarUrlFn: () => new Promise(r => { resClearedUrl = r; })
            });
            avatarLoader.clearAvatarUrlCache();
            resClearedUrl({ url: 'https://r2.example.com/avatars/late', expiresAtUtc: new Date(Date.now() + 600_000).toISOString() });
            assert.equal(await pCleared, null);

            console.log('OK_MILESTONE_B_FRONTEND');
            """;

        var (exitCode, stdout, stderr) = await NodeTestProcess.RunAsync(script);
        Assert.True(exitCode == 0, $"Node exited {exitCode}:\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}");
        Assert.Contains("OK_MILESTONE_B_FRONTEND", stdout);
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
    public async Task AvatarService_CompleteUploadAsync_WhenFinalCommitConflictsWithWinner_CallsHandleConcurrentConflictBeforeCleanup()
    {
        var services = _fixture.CreateServiceCollection();
        services.AddScoped<IAvatarPersistence, AvatarPersistence>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var realPersistence = scope.ServiceProvider.GetRequiredService<IAvatarPersistence>();
        var userReader = scope.ServiceProvider.GetRequiredService<IUserReader>();
        var blockPersistence = scope.ServiceProvider.GetRequiredService<IBlockPersistence>();
        var realtimeNotifier = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"avatar_race_{Guid.NewGuid():N}",
            Email = $"avatar_race_{Guid.NewGuid():N}@example.com",
            EmailConfirmed = true,
            DisplayName = "Avatar Race User",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        var createRes = await userManager.CreateAsync(user, "Password123!");
        Assert.True(createRes.Succeeded);
        var callerId = user.ToDomainUserId();

        var now = new DateTimeOffset(2026, 6, 1, 14, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        var events = new List<string>();
        var storage = new RecordingRaceMediaStorage(events, contentLength: 128);
        var spyPersistence = new ConflictRaceAvatarPersistence(realPersistence, events, async () =>
        {
            await using var winnerScope = provider.CreateAsyncScope();
            var winnerDb = winnerScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var winnerUser = await winnerDb.Users.FirstAsync(u => u.Id == user.Id);
            winnerUser.AvatarObjectKey = "avatars/winner-full";
            winnerUser.AvatarThumbnailObjectKey = "avatars/winner-thumb";
            winnerUser.AvatarContentType = AvatarConstraints.CanonicalAvatarContentType;
            winnerUser.AvatarVersion = "winner-ver";
            winnerUser.AvatarUpdatedAtUtc = now;
            var pending = await winnerDb.PendingAvatarUploads.FirstAsync(p => p.UserId == callerId);
            pending.MarkCompleted(128, now);
            await winnerDb.SaveChangesAsync();
        });

        var avatarService = new AvatarService(
            spyPersistence,
            storage,
            new StubRaceAvatarProcessor(),
            userReader,
            blockPersistence,
            realtimeNotifier,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AvatarService>.Instance,
            timeProvider: timeProvider);

        var initiated = await avatarService.InitiateUploadAsync(
            callerId,
            new InitiateAvatarUploadRequestDto("race.png", "image/png", 128));

        var result = await avatarService.CompleteUploadAsync(callerId, initiated.UploadId);

        Assert.Equal("winner-ver", result.AvatarVersion);

        var conflictReadIdx = events.IndexOf("GetPendingByIdNoTracking");
        var firstDeleteIdx = events.FindIndex(e => e.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.True(conflictReadIdx >= 0, "Expected HandleConcurrentConflictAsync to inspect pending upload state.");
        Assert.True(firstDeleteIdx > conflictReadIdx, "Orphan object deletion must occur after HandleConcurrentConflictAsync.");
        Assert.DoesNotContain(events, e => e is "Delete:avatars/winner-full" or "Delete:avatars/winner-thumb");
        Assert.Contains(events, e => e.StartsWith("Delete:avatars/", StringComparison.Ordinal) && e is not "Delete:avatars/winner-full" and not "Delete:avatars/winner-thumb");
    }

    [Fact]
    public async Task AvatarService_CompleteUploadAsync_PreservesLiveObjectsOnPostCommitConflict_AndPreservesStagingOnPreCommitConflict()
    {
        var services = _fixture.CreateServiceCollection();
        services.AddScoped<IAvatarPersistence, AvatarPersistence>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var realPersistence = scope.ServiceProvider.GetRequiredService<IAvatarPersistence>();
        var userReader = scope.ServiceProvider.GetRequiredService<IUserReader>();
        var blockPersistence = scope.ServiceProvider.GetRequiredService<IBlockPersistence>();
        var realtimeNotifier = scope.ServiceProvider.GetRequiredService<IRealtimeNotifier>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"avatar_postcommit_{Guid.NewGuid():N}",
            Email = $"avatar_postcommit_{Guid.NewGuid():N}@example.com",
            EmailConfirmed = true,
            DisplayName = "Post Commit User",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        Assert.True((await userManager.CreateAsync(user, "Password123!")).Succeeded);
        var callerId = user.ToDomainUserId();

        var now = new DateTimeOffset(2026, 6, 2, 10, 0, 0, TimeSpan.Zero);
        var timeProvider = new AdjustableTimeProvider(now);
        var events = new List<string>();
        var storage = new RecordingRaceMediaStorage(events, contentLength: 128);

        // 1. Simulated ConflictException AFTER real SQL CommitUserAvatarAsync succeeded:
        //    must return committed version, preserve live full+thumb keys, and clean up staging key.
        var postCommitPersistence = new InterceptingAvatarPersistence(
            realPersistence,
            events,
            async (upload, fullKey, thumbKey, contentType, fullSize, thumbSize, version, utc, ct) =>
            {
                await realPersistence.CommitUserAvatarAsync(
                    upload, fullKey, thumbKey, contentType, fullSize, thumbSize, version, utc, ct);
                throw new ConflictException("Simulated post-commit transient failure.");
            });

        var postCommitSvc = new AvatarService(
            postCommitPersistence,
            storage,
            new StubRaceAvatarProcessor(),
            userReader,
            blockPersistence,
            realtimeNotifier,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AvatarService>.Instance,
            timeProvider: timeProvider);

        var init1 = await postCommitSvc.InitiateUploadAsync(
            callerId,
            new InitiateAvatarUploadRequestDto("postcommit.png", "image/png", 128));
        var row1 = await db.PendingAvatarUploads.AsNoTracking().SingleAsync(p => p.Id == init1.UploadId);

        var result1 = await postCommitSvc.CompleteUploadAsync(callerId, init1.UploadId);
        var userAfterCommit = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.False(string.IsNullOrWhiteSpace(result1.AvatarVersion));
        Assert.Equal(userAfterCommit.AvatarVersion, result1.AvatarVersion);
        Assert.DoesNotContain($"Delete:{userAfterCommit.AvatarObjectKey}", events);
        Assert.DoesNotContain($"Delete:{userAfterCommit.AvatarThumbnailObjectKey}", events);
        Assert.Contains($"Delete:{row1.ObjectKey}", events);

        // Direct duplicate call to real SQL AvatarPersistence.CommitUserAvatarAsync must throw clean ConflictException
        var committedRow = await realPersistence.GetPendingByIdNoTrackingAsync(init1.UploadId);
        Assert.NotNull(committedRow);
        await Assert.ThrowsAsync<ConflictException>(() => realPersistence.CommitUserAvatarAsync(
            committedRow!,
            "avatars/dup-full",
            "avatars/dup-thumb",
            AvatarConstraints.CanonicalAvatarContentType,
            4,
            2,
            "dupver1234567890",
            now.AddSeconds(1)));

        // 2. Pre-commit ConflictException while upload remains Pending:
        //    must clean up promoted full+thumb objects while keeping staging intact for retry.
        events.Clear();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var preCommitPersistence = new InterceptingAvatarPersistence(
            realPersistence,
            events,
            (_, _, _, _, _, _, _, _, _) => throw new ConflictException("Simulated pre-commit conflict."));

        var preCommitSvc = new AvatarService(
            preCommitPersistence,
            storage,
            new StubRaceAvatarProcessor(),
            userReader,
            blockPersistence,
            realtimeNotifier,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AvatarService>.Instance,
            timeProvider: timeProvider);

        var init2 = await preCommitSvc.InitiateUploadAsync(
            callerId,
            new InitiateAvatarUploadRequestDto("precommit.png", "image/png", 128));
        var row2 = await db.PendingAvatarUploads.AsNoTracking().SingleAsync(p => p.Id == init2.UploadId);

        await Assert.ThrowsAsync<ConflictException>(() => preCommitSvc.CompleteUploadAsync(callerId, init2.UploadId));
        Assert.DoesNotContain($"Delete:{row2.ObjectKey}", events);
        Assert.Equal(2, events.Count(e => e.StartsWith("Delete:", StringComparison.Ordinal)));

        // 3. Post-commit conflict when GetUserAvatarMetadataAsync lookup fails:
        //    must retain promoted full+thumb objects (never delete potentially live objects on lookup failure).
        events.Clear();
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var failingLookupPersistence = new InterceptingAvatarPersistence(
            realPersistence,
            events,
            async (upload, fullKey, thumbKey, contentType, fullSize, thumbSize, version, utc, ct) =>
            {
                await realPersistence.CommitUserAvatarAsync(
                    upload, fullKey, thumbKey, contentType, fullSize, thumbSize, version, utc, ct);
                throw new ConflictException("Simulated post-commit transient failure.");
            },
            onGetMetadata: (_, _) => throw new InvalidOperationException("Simulated transient metadata lookup failure."));

        var lookupFailSvc = new AvatarService(
            failingLookupPersistence,
            storage,
            new StubRaceAvatarProcessor(),
            userReader,
            blockPersistence,
            realtimeNotifier,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AvatarService>.Instance,
            timeProvider: timeProvider);

        var init3 = await lookupFailSvc.InitiateUploadAsync(
            callerId,
            new InitiateAvatarUploadRequestDto("lookupfail.png", "image/png", 128));
        var row3 = await db.PendingAvatarUploads.AsNoTracking().SingleAsync(p => p.Id == init3.UploadId);

        var result3 = await lookupFailSvc.CompleteUploadAsync(callerId, init3.UploadId);
        var userAfterLookupFail = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.Equal(userAfterLookupFail.AvatarVersion, result3.AvatarVersion);
        Assert.DoesNotContain($"Delete:{userAfterLookupFail.AvatarObjectKey}", events);
        Assert.DoesNotContain($"Delete:{userAfterLookupFail.AvatarThumbnailObjectKey}", events);
        Assert.Contains($"Delete:{row3.ObjectKey}", events);
    }

    [Fact]
    public async Task AvatarService_SameTickRemoveThenInitiateThenComplete_AppliesNewAvatar_AndMissingStagingRaceReconciles()
    {
        var services = _fixture.CreateServiceCollection();
        var frozenClock = new AdjustableTimeProvider(new DateTimeOffset(2026, 11, 26, 14, 0, 0, TimeSpan.Zero));
        services.AddSingleton<TimeProvider>(frozenClock);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var avatarPersistence = new AvatarPersistence(db);
        var signing = new LocalMediaSigningService(
            DataProtectionProvider.Create("AvatarB5Test"));
        var storage = new LocalDiskMediaStorage(signing, _mediaTempRoot);
        var processor = new AvatarProcessor(storage, new ImageProcessingGate(Options.Create(new MediaOptions())));
        var userReader = scope.ServiceProvider.GetRequiredService<IUserReader>();
        var blockPersistence = scope.ServiceProvider.GetRequiredService<IBlockPersistence>();
        var avatarSvc = new AvatarService(
            avatarPersistence,
            storage,
            processor,
            userReader,
            blockPersistence,
            NoOpAvatarRealtimeNotifier.Instance,
            NullLogger<AvatarService>.Instance,
            Options.Create(new MediaOptions()),
            frozenClock);

        var user = await SeedConfirmedUserAsync(scope.ServiceProvider, "avatar_b5", "Avatar B5");
        var userId = user.ToDomainUserId();
        var pngBytes = CreateExifJpegBytes(64, 64);

        // First remove avatar, then initiate + complete ALL at the same clock tick
        await avatarSvc.RemoveAvatarAsync(userId);
        var initiated = await avatarSvc.InitiateUploadAsync(
            userId,
            new InitiateAvatarUploadRequestDto("same-tick.jpg", "image/jpeg", pngBytes.Length));
        var uploadRow = await db.PendingAvatarUploads.AsNoTracking().SingleAsync(p => p.Id == initiated.UploadId);
        var diskPath = Path.Combine(_mediaTempRoot, uploadRow.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(diskPath)!);
        await File.WriteAllBytesAsync(diskPath, pngBytes);

        var completed = await avatarSvc.CompleteUploadAsync(userId, initiated.UploadId);
        Assert.False(string.IsNullOrWhiteSpace(completed.AvatarVersion));
        var committedUser = await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id);
        Assert.NotNull(committedUser.AvatarObjectKey);

        var initRace = await avatarSvc.InitiateUploadAsync(
            userId,
            new InitiateAvatarUploadRequestDto("missing-race.png", "image/png", 128));
        var winnerVersion = AvatarConstraints.GenerateAvatarVersion();
        var nullMetaStorage = new MissingStagingAvatarStorage();
        var racePersistence = new MissingStagingConflictAvatarPersistence(avatarPersistence, async () =>
        {
            await using var winScope = provider.CreateAsyncScope();
            var winDb = winScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var winUpload = await winDb.PendingAvatarUploads.SingleAsync(p => p.Id == initRace.UploadId);
            winUpload.MarkCompleted(128, frozenClock.GetUtcNow().AddSeconds(1));
            var winUser = await winDb.Users.SingleAsync(u => u.Id == user.Id);
            winUser.AvatarVersion = winnerVersion;
            winUser.AvatarUpdatedAtUtc = frozenClock.GetUtcNow().AddSeconds(1);
            await winDb.SaveChangesAsync();
        });

        var raceSvc = new AvatarService(
            racePersistence,
            nullMetaStorage,
            new StubRaceAvatarProcessor(),
            userReader,
            blockPersistence,
            NoOpAvatarRealtimeNotifier.Instance,
            NullLogger<AvatarService>.Instance,
            Options.Create(new MediaOptions()),
            frozenClock);

        var reconciled = await raceSvc.CompleteUploadAsync(userId, initRace.UploadId);
        Assert.Equal(winnerVersion, reconciled.AvatarVersion);
    }

    private sealed class MissingStagingAvatarStorage : IMediaStorage
    {
        public string GeneratePresignedPutUrl(string objectKey, string contentType, long contentLength, TimeSpan expiresIn, DateTimeOffset nowUtc) => string.Empty;
        public string GeneratePresignedGetUrl(string objectKey, string responseContentType, string responseContentDisposition, TimeSpan expiresIn, DateTimeOffset nowUtc) => string.Empty;
        public Task<MediaObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct = default) => Task.FromResult<MediaObjectMetadata?>(null);
        public Task<byte[]> ReadByteRangeAsync(string objectKey, long offset, int length, string? ifMatchETag = null, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task DownloadToStreamAsync(string objectKey, Stream destination, long maxBytes, string? ifMatchETag = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PromoteByCopyAsync(string stagingKey, string permanentKey, string verifiedContentType, string? contentDisposition, string? ifMatchETag = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PromoteFromStreamAsync(string stagingKey, string permanentKey, Stream content, long contentLength, string verifiedContentType, string? contentDisposition, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteObjectAsync(string objectKey, CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<StoredObjectInfo> ListObjectsAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
    }

    private sealed class MissingStagingConflictAvatarPersistence : IAvatarPersistence
    {
        private readonly IAvatarPersistence _inner;
        private readonly Func<Task> _onUpdateAsync;

        public MissingStagingConflictAvatarPersistence(IAvatarPersistence inner, Func<Task> onUpdateAsync)
        {
            _inner = inner;
            _onUpdateAsync = onUpdateAsync;
        }

        public Task<PendingAvatarUpload> TryCreatePendingWithQuotaCheckAsync(PendingAvatarUpload upload, int maxConcurrentPending, long dailyQuotaBytes, DateTimeOffset dailyQuotaWindowStartUtc, CancellationToken ct = default) =>
            _inner.TryCreatePendingWithQuotaCheckAsync(upload, maxConcurrentPending, dailyQuotaBytes, dailyQuotaWindowStartUtc, ct);
        public Task<PendingAvatarUpload?> GetPendingByIdAsync(AvatarUploadId id, CancellationToken ct = default) =>
            _inner.GetPendingByIdAsync(id, ct);
        public Task<PendingAvatarUpload?> GetPendingByIdNoTrackingAsync(AvatarUploadId id, CancellationToken ct = default) =>
            _inner.GetPendingByIdNoTrackingAsync(id, ct);
        public async Task UpdatePendingAsync(PendingAvatarUpload upload, CancellationToken ct = default)
        {
            await _onUpdateAsync();
            throw new ConflictException("Simulated missing-staging race conflict.");
        }
        public Task<AvatarCommitResult> CommitUserAvatarAsync(PendingAvatarUpload upload, string fullObjectKey, string thumbObjectKey, string contentType, long fullSizeBytes, long thumbSizeBytes, string avatarVersion, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            _inner.CommitUserAvatarAsync(upload, fullObjectKey, thumbObjectKey, contentType, fullSizeBytes, thumbSizeBytes, avatarVersion, nowUtc, ct);
        public Task<AvatarRemoveResult> RemoveUserAvatarAsync(UserId userId, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            _inner.RemoveUserAvatarAsync(userId, nowUtc, ct);
        public Task<UserAvatarMetadata?> GetUserAvatarMetadataAsync(UserId targetUserId, CancellationToken ct = default) =>
            _inner.GetUserAvatarMetadataAsync(targetUserId, ct);
    }

    private sealed class NoOpAvatarRealtimeNotifier : IRealtimeNotifier
    {
        public static readonly NoOpAvatarRealtimeNotifier Instance = new();
        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyRecordingAsync(UserId recipientUserId, RecordingIndicatorDto recording, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyVoicePlayedAsync(ConversationId conversationId, UserId callerUserId, UserId uploaderUserId, VoicePlayedDto played, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubRaceAvatarProcessor : IAvatarProcessor
    {
        public Task<ProcessedAvatarResult> ProcessAvatarAsync(
            PendingAvatarUpload upload,
            MediaObjectMetadata stagingMetadata,
            CancellationToken ct = default) =>
            Task.FromResult(new ProcessedAvatarResult(
                AvatarConstraints.CanonicalAvatarContentType,
                128,
                new MemoryStream(new byte[] { 1, 2, 3, 4 }),
                4,
                new MemoryStream(new byte[] { 5, 6 }),
                2));
    }

    private sealed class RecordingRaceMediaStorage : IMediaStorage
    {
        private readonly List<string> _events;
        private readonly long _contentLength;

        public RecordingRaceMediaStorage(List<string> events, long contentLength)
        {
            _events = events;
            _contentLength = contentLength;
        }

        public string GeneratePresignedPutUrl(string objectKey, string contentType, long contentLength, TimeSpan expiresIn, DateTimeOffset nowUtc) =>
            $"https://r2.test/put/{objectKey}";
        public string GeneratePresignedGetUrl(string objectKey, string responseContentType, string responseContentDisposition, TimeSpan expiresIn, DateTimeOffset nowUtc) =>
            $"https://r2.test/get/{objectKey}";
        public Task<MediaObjectMetadata?> GetObjectMetadataAsync(string objectKey, CancellationToken ct = default) =>
            Task.FromResult<MediaObjectMetadata?>(new MediaObjectMetadata(_contentLength, "\"etag\"", "image/png"));
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

    private sealed class ConflictRaceAvatarPersistence : IAvatarPersistence
    {
        private readonly IAvatarPersistence _inner;
        private readonly List<string> _events;
        private readonly Func<Task> _onCommitRaceAsync;

        public ConflictRaceAvatarPersistence(
            IAvatarPersistence inner,
            List<string> events,
            Func<Task> onCommitRaceAsync)
        {
            _inner = inner;
            _events = events;
            _onCommitRaceAsync = onCommitRaceAsync;
        }

        public Task<PendingAvatarUpload> TryCreatePendingWithQuotaCheckAsync(
            PendingAvatarUpload upload, int maxConcurrentPending, long dailyQuotaBytes, DateTimeOffset dailyQuotaWindowStartUtc, CancellationToken ct = default) =>
            _inner.TryCreatePendingWithQuotaCheckAsync(upload, maxConcurrentPending, dailyQuotaBytes, dailyQuotaWindowStartUtc, ct);

        public Task<PendingAvatarUpload?> GetPendingByIdAsync(AvatarUploadId id, CancellationToken ct = default) =>
            _inner.GetPendingByIdAsync(id, ct);

        public async Task<PendingAvatarUpload?> GetPendingByIdNoTrackingAsync(AvatarUploadId id, CancellationToken ct = default)
        {
            _events.Add("GetPendingByIdNoTracking");
            return await _inner.GetPendingByIdNoTrackingAsync(id, ct);
        }

        public Task UpdatePendingAsync(PendingAvatarUpload upload, CancellationToken ct = default) =>
            _inner.UpdatePendingAsync(upload, ct);

        public async Task<AvatarCommitResult> CommitUserAvatarAsync(
            PendingAvatarUpload upload, string fullObjectKey, string thumbObjectKey, string contentType, long fullSizeBytes, long thumbSizeBytes, string avatarVersion, DateTimeOffset nowUtc, CancellationToken ct = default)
        {
            await _onCommitRaceAsync();
            throw new ConflictException("Simulated concurrent avatar commit conflict.");
        }

        public Task<AvatarRemoveResult> RemoveUserAvatarAsync(UserId userId, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            _inner.RemoveUserAvatarAsync(userId, nowUtc, ct);

        public Task<UserAvatarMetadata?> GetUserAvatarMetadataAsync(UserId targetUserId, CancellationToken ct = default) =>
            _inner.GetUserAvatarMetadataAsync(targetUserId, ct);
    }

    private sealed class InterceptingAvatarPersistence : IAvatarPersistence
    {
        private readonly IAvatarPersistence _inner;
        private readonly List<string> _events;
        private readonly Func<PendingAvatarUpload, string, string, string, long, long, string, DateTimeOffset, CancellationToken, Task<AvatarCommitResult>> _onCommit;
        private readonly Func<UserId, CancellationToken, Task<UserAvatarMetadata?>>? _onGetMetadata;

        public InterceptingAvatarPersistence(
            IAvatarPersistence inner,
            List<string> events,
            Func<PendingAvatarUpload, string, string, string, long, long, string, DateTimeOffset, CancellationToken, Task<AvatarCommitResult>> onCommit,
            Func<UserId, CancellationToken, Task<UserAvatarMetadata?>>? onGetMetadata = null)
        {
            _inner = inner;
            _events = events;
            _onCommit = onCommit;
            _onGetMetadata = onGetMetadata;
        }

        public Task<PendingAvatarUpload> TryCreatePendingWithQuotaCheckAsync(
            PendingAvatarUpload upload, int maxConcurrentPending, long dailyQuotaBytes, DateTimeOffset dailyQuotaWindowStartUtc, CancellationToken ct = default) =>
            _inner.TryCreatePendingWithQuotaCheckAsync(upload, maxConcurrentPending, dailyQuotaBytes, dailyQuotaWindowStartUtc, ct);

        public Task<PendingAvatarUpload?> GetPendingByIdAsync(AvatarUploadId id, CancellationToken ct = default) =>
            _inner.GetPendingByIdAsync(id, ct);

        public async Task<PendingAvatarUpload?> GetPendingByIdNoTrackingAsync(AvatarUploadId id, CancellationToken ct = default)
        {
            _events.Add("GetPendingByIdNoTracking");
            return await _inner.GetPendingByIdNoTrackingAsync(id, ct);
        }

        public Task UpdatePendingAsync(PendingAvatarUpload upload, CancellationToken ct = default) =>
            _inner.UpdatePendingAsync(upload, ct);

        public Task<AvatarCommitResult> CommitUserAvatarAsync(
            PendingAvatarUpload upload, string fullObjectKey, string thumbObjectKey, string contentType, long fullSizeBytes, long thumbSizeBytes, string avatarVersion, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            _onCommit(upload, fullObjectKey, thumbObjectKey, contentType, fullSizeBytes, thumbSizeBytes, avatarVersion, nowUtc, ct);

        public Task<AvatarRemoveResult> RemoveUserAvatarAsync(UserId userId, DateTimeOffset nowUtc, CancellationToken ct = default) =>
            _inner.RemoveUserAvatarAsync(userId, nowUtc, ct);

        public Task<UserAvatarMetadata?> GetUserAvatarMetadataAsync(UserId targetUserId, CancellationToken ct = default) =>
            _onGetMetadata is not null
                ? _onGetMetadata(targetUserId, ct)
                : _inner.GetUserAvatarMetadataAsync(targetUserId, ct);
    }
}
