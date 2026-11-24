using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Converters;
using ImageMagick;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase9MediaWebApiTests : IClassFixture<SqlTestDatabaseFixture>, IDisposable
{
    private readonly SqlTestDatabaseFixture _fixture;
    private readonly string _mediaTempRoot;

    public Phase9MediaWebApiTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
        _mediaTempRoot = Path.Combine(Path.GetTempPath(), $"aos_media_web_tests_{Guid.NewGuid():N}");
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
                        ["Media:LocalStoragePath"] = _mediaTempRoot
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
        var email = $"{username}@example.com";

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = username,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
        return user;
    }

    private static async Task<string> LoginAndGetCsrfTokenAsync(
        HttpClient client,
        string username,
        string password = "Password123!")
    {
        var loginPage = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        var loginHtml = await loginPage.Content.ReadAsStringAsync();

        var loginToken = ExtractAntiforgeryToken(loginHtml);
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserNameOrEmail"] = username,
            ["Password"] = password,
            ["RememberMe"] = "false",
            ["__RequestVerificationToken"] = loginToken
        });

        var loginResp = await client.PostAsync("/Account/Login", form);
        Assert.Equal(HttpStatusCode.Redirect, loginResp.StatusCode);

        // Fetch authenticated /Chat page to read CSRF token bound to authenticated identity
        var chatPage = await client.GetAsync("/Chat");
        Assert.Equal(HttpStatusCode.OK, chatPage.StatusCode);
        var chatHtml = await chatPage.Content.ReadAsStringAsync();
        return ExtractAntiforgeryToken(chatHtml);
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

    private static byte[] CreateRotatedExifJpegBytes()
    {
        using var img = new MagickImage(new MagickColor(120, 60, 200), 40, 20);
        img.Orientation = Orientation.RightTop; // 90 deg -> upright becomes 20x40
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.Make, "SensitivePhoneModel");
        img.SetProfile(exif);

        using var ms = new MemoryStream();
        img.Quality = 90;
        img.Write(ms, MagickFormat.Jpeg);
        return ms.ToArray();
    }

    private static byte[] CreateMinimalPdfBytes() =>
        Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<<>>\nendobj\ntrailer\n<<>>\n%%EOF\n");

    private static HttpRequestMessage CreateCsrfJsonPost(string url, string csrfToken, object? body = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("RequestVerificationToken", csrfToken);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        if (body is not null)
        {
            req.Content = JsonContent.Create(body);
        }

        return req;
    }

    private static void AssertSecurityAndNoStoreHeaders(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.Contains("no-store", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.True(response.Headers.TryGetValues("Cross-Origin-Resource-Policy", out var corp));
        Assert.Equal("same-origin", string.Join("", corp));
    }

    // =========================================================================
    // 1. MediaIdJsonConverter & MVC / SignalR Registration Tests
    // =========================================================================

    [Fact]
    public void MediaIdJsonConverter_RoundTripsValidGuid_AndRejectsInvalidOrEmptyStrings()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new MediaIdJsonConverter());

        var mediaId = MediaId.New();
        var json = JsonSerializer.Serialize(mediaId, options);
        Assert.Equal($"\"{mediaId.Value:D}\"", json);

        var parsed = JsonSerializer.Deserialize<MediaId>(json, options);
        Assert.Equal(mediaId, parsed);

        var nullResult = JsonSerializer.Deserialize<MediaId>("null", options);
        Assert.Null(nullResult);

        var objParsed = JsonSerializer.Deserialize<MediaId>($"{{\"value\":\"{mediaId.Value:D}\"}}", options);
        Assert.Equal(mediaId, objParsed);

        foreach (var badJson in new[]
        {
            "\"\"",
            "\"   \"",
            $"\"{Guid.Empty:D}\"",
            "\"not-a-guid\"",
            "12345",
            "{\"other\":\"value\"}"
        })
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MediaId>(badJson, options));
        }

        using var factory = CreateFactory();
        var mvcOptions = factory.Services.GetRequiredService<IOptions<JsonOptions>>().Value;
        Assert.Contains(mvcOptions.JsonSerializerOptions.Converters, c => c is MediaIdJsonConverter);

        var signalROptions = factory.Services.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;
        Assert.Contains(signalROptions.PayloadSerializerOptions.Converters, c => c is MediaIdJsonConverter);
    }

    // =========================================================================
    // 2. MediaController Attributes & Rate-Limit Policy Tests
    // =========================================================================

    [Fact]
    public void MediaController_IsSealed_WithExpectedSecurityAndRateLimitAttributes()
    {
        var type = typeof(MediaController);
        Assert.True(type.IsSealed);
        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.NotNull(type.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>());
        var cacheAttr = type.GetCustomAttribute<ResponseCacheAttribute>();
        Assert.NotNull(cacheAttr);
        Assert.True(cacheAttr.NoStore);
        var sizeLimitAttr = type.GetCustomAttribute<RequestSizeLimitAttribute>();
        Assert.NotNull(sizeLimitAttr);
        var sizeLimitMeta = Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata>(sizeLimitAttr);
        Assert.Equal(16 * 1024, sizeLimitMeta.MaxRequestBodySize);

        Assert.Equal("media-upload", type.GetMethod(nameof(MediaController.InitiateUpload))!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal("media-upload", type.GetMethod(nameof(MediaController.CompleteUpload))!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal("media-cancel", type.GetMethod(nameof(MediaController.CancelUpload))!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal("media-download", type.GetMethod(nameof(MediaController.DownloadRedirect))!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
        Assert.Equal("media-download", type.GetMethod(nameof(MediaController.GetDownloadUrl))!.GetCustomAttribute<EnableRateLimitingAttribute>()?.PolicyName);
    }

    // =========================================================================
    // 3. Full End-to-End Upload, Message, Download, Cancel & Delete-for-Everyone Flow
    // =========================================================================

    [Fact]
    public async Task MediaApi_EndToEndFlow_InitiatePutCompleteSendDownloadCancelAndDeleteForEveryone()
    {
        using var factory = CreateFactory();
        using var aliceClient = CreateHttpsClient(factory);
        using var bobClient = CreateHttpsClient(factory);

        Guid aliceId;
        Guid bobId;
        string aliceUsername;
        string bobUsername;
        Guid convId;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "m_e2e_a", "Alice Media");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "m_e2e_b", "Bob Media");
            aliceId = alice.Id;
            bobId = bob.Id;
            aliceUsername = alice.UserName!;
            bobUsername = bob.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(aliceId), new UserId(bobId), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        var aliceCsrf = await LoginAndGetCsrfTokenAsync(aliceClient, aliceUsername);
        var bobCsrf = await LoginAndGetCsrfTokenAsync(bobClient, bobUsername);

        var jpegBytes = CreateRotatedExifJpegBytes();
        var pdfBytes = CreateMinimalPdfBytes();

        // 1. Initiate & Complete JPEG upload
        using var initImgReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/media/initiate",
            aliceCsrf,
            new { fileName = "portrait.jpg", contentType = "image/jpeg", sizeBytes = jpegBytes.Length });
        var initImgResp = await aliceClient.SendAsync(initImgReq);
        Assert.Equal(HttpStatusCode.OK, initImgResp.StatusCode);
        AssertSecurityAndNoStoreHeaders(initImgResp);

        using var initImgJson = JsonDocument.Parse(await initImgResp.Content.ReadAsStringAsync());
        var imgIdStr = initImgJson.RootElement.GetProperty("mediaId").GetString()!;
        Assert.True(Guid.TryParse(imgIdStr, out var imgId));
        var imgPutUrl = initImgJson.RootElement.GetProperty("uploadUrl").GetString()!;
        var imgReqMime = initImgJson.RootElement.GetProperty("requiredContentType").GetString()!;

        using (var putReq = new HttpRequestMessage(HttpMethod.Put, imgPutUrl)
        {
            Content = new ByteArrayContent(jpegBytes)
        })
        {
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue(imgReqMime);
            var putResp = await aliceClient.SendAsync(putReq);
            Assert.Equal(HttpStatusCode.OK, putResp.StatusCode);
        }

        using var compImgReq = CreateCsrfJsonPost($"/api/conversations/{convId}/media/{imgId}/complete", aliceCsrf);
        var compImgResp = await aliceClient.SendAsync(compImgReq);
        Assert.Equal(HttpStatusCode.OK, compImgResp.StatusCode);
        AssertSecurityAndNoStoreHeaders(compImgResp);

        using (var compDoc = JsonDocument.Parse(await compImgResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal(20, compDoc.RootElement.GetProperty("width").GetInt32());
            Assert.Equal(40, compDoc.RootElement.GetProperty("height").GetInt32());
        }

        // 2. Initiate & Complete PDF upload
        using var initPdfReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/media/initiate",
            aliceCsrf,
            new { fileName = "spec.pdf", contentType = "application/pdf", sizeBytes = pdfBytes.Length });
        var initPdfResp = await aliceClient.SendAsync(initPdfReq);
        Assert.Equal(HttpStatusCode.OK, initPdfResp.StatusCode);
        using var initPdfJson = JsonDocument.Parse(await initPdfResp.Content.ReadAsStringAsync());
        var pdfId = Guid.Parse(initPdfJson.RootElement.GetProperty("mediaId").GetString()!);
        var pdfPutUrl = initPdfJson.RootElement.GetProperty("uploadUrl").GetString()!;

        using (var putPdfReq = new HttpRequestMessage(HttpMethod.Put, pdfPutUrl)
        {
            Content = new ByteArrayContent(pdfBytes)
        })
        {
            putPdfReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(putPdfReq)).StatusCode);
        }

        using var compPdfReq = CreateCsrfJsonPost($"/api/conversations/{convId}/media/{pdfId}/complete", aliceCsrf);
        Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(compPdfReq)).StatusCode);

        // 3. Initiate & Cancel an extra upload (DELETE -> 204 No Content)
        using var initCancelReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/media/initiate",
            aliceCsrf,
            new { fileName = "cancel.pdf", contentType = "application/pdf", sizeBytes = pdfBytes.Length });
        var initCancelResp = await aliceClient.SendAsync(initCancelReq);
        Assert.Equal(HttpStatusCode.OK, initCancelResp.StatusCode);
        using var cancelJson = JsonDocument.Parse(await initCancelResp.Content.ReadAsStringAsync());
        var cancelMediaId = Guid.Parse(cancelJson.RootElement.GetProperty("mediaId").GetString()!);

        using var deleteMediaReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/conversations/{convId}/media/{cancelMediaId}");
        deleteMediaReq.Headers.Add("RequestVerificationToken", aliceCsrf);
        var deleteMediaResp = await aliceClient.SendAsync(deleteMediaReq);
        Assert.Equal(HttpStatusCode.NoContent, deleteMediaResp.StatusCode);
        AssertSecurityAndNoStoreHeaders(deleteMediaResp);

        // 4. Send message attaching imgId + pdfId via MessagesController
        using var sendReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/messages",
            aliceCsrf,
            new { content = "Here are the files", idempotencyKey = Guid.NewGuid().ToString("N"), mediaIds = new[] { imgId, pdfId } });
        var sendResp = await aliceClient.SendAsync(sendReq);
        Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);
        using var sendDoc = JsonDocument.Parse(await sendResp.Content.ReadAsStringAsync());
        var messageId = sendDoc.RootElement.GetProperty("id").GetInt64();
        Assert.Equal(2, sendDoc.RootElement.GetProperty("attachments").GetArrayLength());

        // 5. Bob lists messages and sees both attachments
        var bobListResp = await bobClient.GetAsync($"/api/conversations/{convId}/messages");
        Assert.Equal(HttpStatusCode.OK, bobListResp.StatusCode);
        using (var listDoc = JsonDocument.Parse(await bobListResp.Content.ReadAsStringAsync()))
        {
            var attachments = listDoc.RootElement.GetProperty("messages")[0].GetProperty("attachments");
            Assert.Equal(2, attachments.GetArrayLength());
            Assert.Equal(imgId.ToString("D"), attachments[0].GetProperty("id").GetString());
        }

        // 6. Bob fetches inline image via 302 Found -> follow redirect to dev-media download -> verify EXIF stripped
        var imgRedirectResp = await bobClient.GetAsync($"/api/conversations/{convId}/messages/{messageId}/media/{imgId}");
        Assert.Equal(HttpStatusCode.Redirect, imgRedirectResp.StatusCode);
        AssertSecurityAndNoStoreHeaders(imgRedirectResp);
        var redirectLocation = imgRedirectResp.Headers.Location!.ToString();

        var downloadedImgResp = await bobClient.GetAsync(redirectLocation);
        Assert.Equal(HttpStatusCode.OK, downloadedImgResp.StatusCode);
        var downloadedImgBytes = await downloadedImgResp.Content.ReadAsByteArrayAsync();
        using (var downloadedImg = new MagickImage(downloadedImgBytes))
        {
            Assert.Null(downloadedImg.GetExifProfile());
            Assert.Equal(20u, downloadedImg.Width);
            Assert.Equal(40u, downloadedImg.Height);
        }

        // 7. Bob fetches PDF signed URL via /url -> 200 OK & follow to verify attachment disposition
        var pdfUrlResp = await bobClient.GetAsync($"/api/conversations/{convId}/messages/{messageId}/media/{pdfId}/url");
        Assert.Equal(HttpStatusCode.OK, pdfUrlResp.StatusCode);
        AssertSecurityAndNoStoreHeaders(pdfUrlResp);
        using var pdfUrlDoc = JsonDocument.Parse(await pdfUrlResp.Content.ReadAsStringAsync());
        var signedPdfUrl = pdfUrlDoc.RootElement.GetProperty("url").GetString()!;
        var downloadedPdfResp = await bobClient.GetAsync(signedPdfUrl);
        Assert.Equal(HttpStatusCode.OK, downloadedPdfResp.StatusCode);
        Assert.StartsWith("attachment;", downloadedPdfResp.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);

        // 8. Delete for everyone -> subsequent 302 and /url return 404 Not Found
        using var deleteForEveryoneReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/messages/{messageId}/delete-for-everyone",
            aliceCsrf);
        Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(deleteForEveryoneReq)).StatusCode);

        var deadRedirect = await bobClient.GetAsync($"/api/conversations/{convId}/messages/{messageId}/media/{imgId}");
        Assert.Equal(HttpStatusCode.NotFound, deadRedirect.StatusCode);
        Assert.Contains("no-store", deadRedirect.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var deadUrl = await bobClient.GetAsync($"/api/conversations/{convId}/messages/{messageId}/media/{pdfId}/url");
        Assert.Equal(HttpStatusCode.NotFound, deadUrl.StatusCode);
        Assert.Contains("no-store", deadUrl.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(bobCsrf);
    }

    // =========================================================================
    // 4. Auth, CSRF, Bad JSON, IDOR, Block & DeleteForMe Visibility Checks
    // =========================================================================

    [Fact]
    public async Task MediaApi_EnforcesAuthCsrfBadJsonIdorBlockAndDeleteForMeVisibility()
    {
        using var factory = CreateFactory();
        using var anonClient = CreateHttpsClient(factory);
        using var aliceClient = CreateHttpsClient(factory);
        using var bobClient = CreateHttpsClient(factory);
        using var outsiderClient = CreateHttpsClient(factory);

        Guid aliceId;
        Guid bobId;
        string aliceUsername;
        string bobUsername;
        string outsiderUsername;
        Guid convId;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "m_sec_a", "Alice Sec");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "m_sec_b", "Bob Sec");
            var outsider = await SeedConfirmedUserAsync(scope.ServiceProvider, "m_sec_o", "Outsider Sec");
            aliceId = alice.Id;
            bobId = bob.Id;
            aliceUsername = alice.UserName!;
            bobUsername = bob.UserName!;
            outsiderUsername = outsider.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(aliceId), new UserId(bobId), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        // Unauthenticated -> 401 with no-store
        foreach (var resp in new[]
        {
            await anonClient.PostAsJsonAsync($"/api/conversations/{convId}/media/initiate", new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 50 }),
            await anonClient.PostAsync($"/api/conversations/{convId}/media/{Guid.NewGuid()}/complete", null),
            await anonClient.DeleteAsync($"/api/conversations/{convId}/media/{Guid.NewGuid()}"),
            await anonClient.GetAsync($"/api/conversations/{convId}/messages/1/media/{Guid.NewGuid()}"),
            await anonClient.GetAsync($"/api/conversations/{convId}/messages/1/media/{Guid.NewGuid()}/url")
        })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            Assert.Contains("no-store", resp.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        var aliceCsrf = await LoginAndGetCsrfTokenAsync(aliceClient, aliceUsername);
        var bobCsrf = await LoginAndGetCsrfTokenAsync(bobClient, bobUsername);
        var outsiderCsrf = await LoginAndGetCsrfTokenAsync(outsiderClient, outsiderUsername);

        // Missing CSRF token -> 400 ProblemDetails on mutating endpoints
        var noCsrfInit = await aliceClient.PostAsJsonAsync(
            $"/api/conversations/{convId}/media/initiate",
            new { fileName = "a.pdf", contentType = "application/pdf", sizeBytes = 50 });
        Assert.Equal(HttpStatusCode.BadRequest, noCsrfInit.StatusCode);

        var noCsrfComplete = await aliceClient.PostAsync($"/api/conversations/{convId}/media/{Guid.NewGuid()}/complete", null);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrfComplete.StatusCode);

        var noCsrfDelete = await aliceClient.DeleteAsync($"/api/conversations/{convId}/media/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.BadRequest, noCsrfDelete.StatusCode);

        // Bad MediaIds JSON ("" or invalid string) -> 400 Bad Request
        using var emptyGuidMsg = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/messages",
            aliceCsrf,
            new { content = "hi", idempotencyKey = Guid.NewGuid().ToString("N"), mediaIds = new[] { "" } });
        Assert.Equal(HttpStatusCode.BadRequest, (await aliceClient.SendAsync(emptyGuidMsg)).StatusCode);

        using var badGuidMsg = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/messages",
            aliceCsrf,
            new { content = "hi", idempotencyKey = Guid.NewGuid().ToString("N"), mediaIds = new[] { "not-a-guid" } });
        Assert.Equal(HttpStatusCode.BadRequest, (await aliceClient.SendAsync(badGuidMsg)).StatusCode);

        // Alice uploads & sends a PDF message
        var pdfBytes = CreateMinimalPdfBytes();
        using var initReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/media/initiate",
            aliceCsrf,
            new { fileName = "sec.pdf", contentType = "application/pdf", sizeBytes = pdfBytes.Length });
        var initResp = await aliceClient.SendAsync(initReq);
        Assert.Equal(HttpStatusCode.OK, initResp.StatusCode);
        using var initDoc = JsonDocument.Parse(await initResp.Content.ReadAsStringAsync());
        var mediaId = Guid.Parse(initDoc.RootElement.GetProperty("mediaId").GetString()!);
        var putUrl = initDoc.RootElement.GetProperty("uploadUrl").GetString()!;

        using (var putReq = new HttpRequestMessage(HttpMethod.Put, putUrl) { Content = new ByteArrayContent(pdfBytes) })
        {
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
            Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(putReq)).StatusCode);
        }

        // Bob (non-uploader) cannot complete or cancel Alice's pending media -> 403 Forbidden
        using var bobCompReq = CreateCsrfJsonPost($"/api/conversations/{convId}/media/{mediaId}/complete", bobCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await bobClient.SendAsync(bobCompReq)).StatusCode);

        using var bobDelReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/conversations/{convId}/media/{mediaId}");
        bobDelReq.Headers.Add("RequestVerificationToken", bobCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await bobClient.SendAsync(bobDelReq)).StatusCode);

        // Outsider receives 403 on initiate, complete, cancel, 302, /url
        using var outInitReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/media/initiate",
            outsiderCsrf,
            new { fileName = "x.pdf", contentType = "application/pdf", sizeBytes = 10 });
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.SendAsync(outInitReq)).StatusCode);

        using var outCompReq = CreateCsrfJsonPost($"/api/conversations/{convId}/media/{mediaId}/complete", outsiderCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.SendAsync(outCompReq)).StatusCode);

        using var outDelReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/conversations/{convId}/media/{mediaId}");
        outDelReq.Headers.Add("RequestVerificationToken", outsiderCsrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.SendAsync(outDelReq)).StatusCode);

        // Complete & send message
        using var aliceCompReq = CreateCsrfJsonPost($"/api/conversations/{convId}/media/{mediaId}/complete", aliceCsrf);
        Assert.Equal(HttpStatusCode.OK, (await aliceClient.SendAsync(aliceCompReq)).StatusCode);

        using var sendReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/messages",
            aliceCsrf,
            new { content = "Attached", idempotencyKey = Guid.NewGuid().ToString("N"), mediaIds = new[] { mediaId } });
        var sendResp = await aliceClient.SendAsync(sendReq);
        Assert.Equal(HttpStatusCode.OK, sendResp.StatusCode);
        var messageId = JsonDocument.Parse(await sendResp.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetInt64();

        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync($"/api/conversations/{convId}/messages/{messageId}/media/{mediaId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsiderClient.GetAsync($"/api/conversations/{convId}/messages/{messageId}/media/{mediaId}/url")).StatusCode);

        // Block Alice <-> Bob: new initiate fails 403, historical download STILL returns 302 for Bob
        var blockForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["targetUserId"] = aliceId.ToString("D"),
            ["__RequestVerificationToken"] = bobCsrf
        });
        var blockResp = await bobClient.PostAsync("/Settings/BlockUser", blockForm);
        Assert.Equal(HttpStatusCode.Redirect, blockResp.StatusCode);

        using var blockedInitReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/media/initiate",
            aliceCsrf,
            new { fileName = "blk.pdf", contentType = "application/pdf", sizeBytes = 50 });
        Assert.Equal(HttpStatusCode.Forbidden, (await aliceClient.SendAsync(blockedInitReq)).StatusCode);

        var preservedDownload = await bobClient.GetAsync($"/api/conversations/{convId}/messages/{messageId}/media/{mediaId}");
        Assert.Equal(HttpStatusCode.Redirect, preservedDownload.StatusCode);

        // DeleteForMe by Alice hides media for Alice (404) while remaining accessible to Bob (302)
        using var deleteForMeReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/messages/{messageId}/delete-for-me",
            aliceCsrf);
        Assert.Equal(HttpStatusCode.NoContent, (await aliceClient.SendAsync(deleteForMeReq)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await aliceClient.GetAsync($"/api/conversations/{convId}/messages/{messageId}/media/{mediaId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await bobClient.GetAsync($"/api/conversations/{convId}/messages/{messageId}/media/{mediaId}")).StatusCode);
    }

    // =========================================================================
    // 5. Rate Limiting ("media-upload" 60/min -> 61st returns 429) & Unified R2 CSP
    // =========================================================================

    [Fact]
    public async Task MediaApi_RateLimit61stMediaUploadRequest_Returns429ProblemDetails()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        string username;
        Guid convId;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "m_rl_a", "Rate Alice");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "m_rl_b", "Rate Bob");
            username = alice.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        var csrf = await LoginAndGetCsrfTokenAsync(client, username);

        // Hit media-upload policy 60 times cleanly (nonexistent mediaId returns 404 after consuming 1 permit)
        for (var i = 0; i < 60; i++)
        {
            using var req = CreateCsrfJsonPost($"/api/conversations/{convId}/media/{Guid.NewGuid()}/complete", csrf);
            var resp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }

        using var req61 = CreateCsrfJsonPost($"/api/conversations/{convId}/media/{Guid.NewGuid()}/complete", csrf);
        var resp61 = await client.SendAsync(req61);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp61.StatusCode);
        Assert.Equal("application/problem+json", resp61.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", resp61.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecurityHeaders_WithConfiguredR2ServiceUrl_MatchesAcrossNormalAndErrorResponses()
    {
        const string r2ServiceUrl = "https://0123456789abcdef.r2.cloudflarestorage.com/";
        const string expectedOrigin = "https://0123456789abcdef.r2.cloudflarestorage.com";

        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["R2:ServiceUrl"] = r2ServiceUrl,
            ["R2:BucketName"] = "aos-test-bucket",
            ["R2:AccessKeyId"] = "test-access-key",
            ["R2:SecretAccessKey"] = "test-secret-key"
        });
        using var client = CreateHttpsClient(factory);

        string username;
        using (var scope = factory.Services.CreateScope())
        {
            var user = await SeedConfirmedUserAsync(scope.ServiceProvider, "m_csp_u", "CSP User");
            username = user.UserName!;
        }

        await LoginAndGetCsrfTokenAsync(client, username);

        // Normal 200 response
        var okResp = await client.GetAsync("/Chat");
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        Assert.True(okResp.Headers.TryGetValues("Content-Security-Policy", out var okCspValues));
        var okCsp = string.Join(" ", okCspValues);

        // Middleware-handled 404 error response
        var errResp = await client.GetAsync($"/api/conversations/{Guid.NewGuid()}/messages");
        Assert.Equal(HttpStatusCode.NotFound, errResp.StatusCode);
        Assert.True(errResp.Headers.TryGetValues("Content-Security-Policy", out var errCspValues));
        var errCsp = string.Join(" ", errCspValues);

        Assert.Equal(okCsp, errCsp);
        Assert.Contains($"img-src 'self' blob: {expectedOrigin}", okCsp, StringComparison.Ordinal);
        Assert.Contains($"media-src 'self' blob: {expectedOrigin}", okCsp, StringComparison.Ordinal);
        Assert.Contains($"connect-src 'self' {expectedOrigin}", okCsp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitiateUpload_RejectsOversizedBodyWith413_AndOversizedFileNameWith400()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        string username;
        Guid convId;
        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "m_lim_a", "Limit Alice");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "m_lim_b", "Limit Bob");
            username = alice.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        var csrf = await LoginAndGetCsrfTokenAsync(client, username);

        // 1. > 16 KB chunked JSON request body rejected with 413 Payload Too Large (application/problem+json)
        var hugeJson = new string('a', 20 * 1024);
        using var hugeReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/media/initiate",
            csrf,
            new { fileName = hugeJson + ".png", contentType = "image/png", sizeBytes = 100 });
        var hugeResp = await client.SendAsync(hugeReq);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, hugeResp.StatusCode);
        Assert.Equal("application/problem+json", hugeResp.Content.Headers.ContentType?.MediaType);

        // 2. > 16 KB explicit Content-Length JSON request body rejected cleanly before model binding
        var hugePayloadJson = JsonSerializer.Serialize(new { fileName = hugeJson + ".png", contentType = "image/png", sizeBytes = 100 });
        using var hugeLengthReq = new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{convId}/media/initiate")
        {
            Content = new StringContent(hugePayloadJson, Encoding.UTF8, "application/json")
        };
        hugeLengthReq.Headers.Add("RequestVerificationToken", csrf);
        hugeLengthReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var hugeLengthResp = await client.SendAsync(hugeLengthReq);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, hugeLengthResp.StatusCode);

        // 3. Small JSON body with > 1024-char fileName rejected cleanly with 400 Bad Request
        var overName = new string('b', 1025) + ".png";
        using var nameReq = CreateCsrfJsonPost(
            $"/api/conversations/{convId}/media/initiate",
            csrf,
            new { fileName = overName, contentType = "image/png", sizeBytes = 100 });
        var nameResp = await client.SendAsync(nameReq);
        Assert.Equal(HttpStatusCode.BadRequest, nameResp.StatusCode);
    }
}
