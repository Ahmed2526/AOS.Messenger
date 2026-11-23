using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Filters;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class ZeroRefreshChatShellBackendTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public ZeroRefreshChatShellBackendTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", _fixture.ConnectionString);
                builder.UseSetting("PublicBaseUrl", "https://localhost:7001");

                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                        ["PublicBaseUrl"] = "https://localhost:7001"
                    });
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

        var createResult = await userManager.CreateAsync(user, password);
        Assert.True(createResult.Succeeded, string.Join("; ", createResult.Errors.Select(e => e.Description)));
        return user;
    }

    private static async Task LoginWithCookieAsync(HttpClient client, string username, string password = "Password123!")
    {
        var loginPageResponse = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginPageResponse.StatusCode);
        var loginHtml = await loginPageResponse.Content.ReadAsStringAsync();

        var tokenMatch = Regex.Match(
            loginHtml,
            @"<input[^>]*name=""__RequestVerificationToken""[^>]*value=""([^""]+)""",
            RegexOptions.IgnoreCase);
        Assert.True(tokenMatch.Success, "Login page must render an antiforgery token.");

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserNameOrEmail"] = username,
            ["Password"] = password,
            ["RememberMe"] = "false",
            ["__RequestVerificationToken"] = tokenMatch.Groups[1].Value
        });

        var loginPostResponse = await client.PostAsync("/Account/Login", form);
        Assert.Equal(HttpStatusCode.Redirect, loginPostResponse.StatusCode);
    }

    private static void AssertNoStoreHeaders(HttpResponseMessage response)
    {
        var cacheControl = response.Headers.CacheControl?.ToString() ?? string.Empty;
        Assert.Contains("no-store", cacheControl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no-cache", cacheControl, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertSecurityHeaders(HttpResponseMessage response)
    {
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var cspValues));
        Assert.Contains("default-src 'self'", string.Join(" ", cspValues));
        Assert.True(response.Headers.TryGetValues("X-Content-Type-Options", out var nosniffValues));
        Assert.Equal("nosniff", string.Join("", nosniffValues));
    }

    [Fact]
    public async Task Unauthenticated_ApiAndSignalRNegotiate_Return401WithNoStore_NotRedirect()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);
        var convId = Guid.NewGuid();

        var bundleResp = await client.GetAsync($"/api/conversations/{convId}/bundle");
        Assert.Equal(HttpStatusCode.Unauthorized, bundleResp.StatusCode);
        AssertNoStoreHeaders(bundleResp);

        var messagesResp = await client.GetAsync($"/api/conversations/{convId}/messages");
        Assert.Equal(HttpStatusCode.Unauthorized, messagesResp.StatusCode);
        AssertNoStoreHeaders(messagesResp);

        using var negotiateReq = new HttpRequestMessage(HttpMethod.Post, "/hubs/chat/negotiate?negotiateVersion=1");
        negotiateReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var negotiateResp = await client.SendAsync(negotiateReq);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiateResp.StatusCode);
        AssertNoStoreHeaders(negotiateResp);
    }

    [Fact]
    public async Task RevokedSession_ApiAndSignalRNegotiate_Return401WithNoStore_NotRedirect()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        Guid userId;
        Guid convId;
        string username;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_rev_a", "Alice Revoked");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_rev_b", "Bob Peer");
            userId = alice.Id;
            username = alice.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        await LoginWithCookieAsync(client, username);

        // Confirm valid before revocation
        var okResp = await client.GetAsync($"/api/conversations/{convId}/bundle");
        Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);

        // Revoke active session in SQL Server and cache
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var sessionService = scope.ServiceProvider.GetRequiredService<ISessionService>();
            var sessions = await db.UserSessions
                .Where(s => s.UserId == userId && !s.IsRevoked)
                .ToListAsync();
            Assert.NotEmpty(sessions);

            foreach (var session in sessions)
            {
                await sessionService.RevokeSessionAsync(new UserId(userId), session.Id);
            }
        }

        var bundleAfterRevoke = await client.GetAsync($"/api/conversations/{convId}/bundle");
        Assert.Equal(HttpStatusCode.Unauthorized, bundleAfterRevoke.StatusCode);
        AssertNoStoreHeaders(bundleAfterRevoke);

        var messagesAfterRevoke = await client.GetAsync($"/api/conversations/{convId}/messages");
        Assert.Equal(HttpStatusCode.Unauthorized, messagesAfterRevoke.StatusCode);
        AssertNoStoreHeaders(messagesAfterRevoke);

        using var negotiateReq = new HttpRequestMessage(HttpMethod.Post, "/hubs/chat/negotiate?negotiateVersion=1");
        negotiateReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var negotiateAfterRevoke = await client.SendAsync(negotiateReq);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiateAfterRevoke.StatusCode);
        AssertNoStoreHeaders(negotiateAfterRevoke);
    }

    [Fact]
    public async Task NonParticipantAndMissingConversation_Return403And404_WithNoStoreAndSecurityHeaders()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        Guid otherConvId;
        string outsiderUsername;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_np_a", "Alice Participant");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_np_b", "Bob Participant");
            var outsider = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_np_out", "Outsider");
            outsiderUsername = outsider.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            otherConvId = conv.Id.Value;
        }

        await LoginWithCookieAsync(client, outsiderUsername);

        var forbiddenBundle = await client.GetAsync($"/api/conversations/{otherConvId}/bundle");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenBundle.StatusCode);
        AssertNoStoreHeaders(forbiddenBundle);
        AssertSecurityHeaders(forbiddenBundle);

        var forbiddenMessages = await client.GetAsync($"/api/conversations/{otherConvId}/messages");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenMessages.StatusCode);
        AssertNoStoreHeaders(forbiddenMessages);
        AssertSecurityHeaders(forbiddenMessages);

        var missingId = Guid.NewGuid();
        var notFoundBundle = await client.GetAsync($"/api/conversations/{missingId}/bundle");
        Assert.Equal(HttpStatusCode.NotFound, notFoundBundle.StatusCode);
        AssertNoStoreHeaders(notFoundBundle);
        AssertSecurityHeaders(notFoundBundle);
    }

    [Fact]
    public async Task GetConversationBundle_ReturnsOrderedFilteredMessages_CappedPageSize_LiveFlags_AndNoPeerEmail()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        Guid aliceId;
        string aliceUsername;
        string bobEmail;
        Guid convId;
        Guid blockedConvId;
        Guid deletedPeerConvId;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_bd_a", "Alice Bundle");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_bd_b", "Bob Bundle");
            var blockedPeer = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_bd_blk", "Blocked Peer");
            var deletedPeer = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_bd_del", "Deleted Peer");

            aliceId = alice.Id;
            aliceUsername = alice.UserName!;
            bobEmail = bob.Email!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var baseTime = DateTimeOffset.UtcNow.AddHours(-2);

            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), baseTime.AddMinutes(-10));
            var convBlocked = Conversation.CreateDirect(new UserId(alice.Id), new UserId(blockedPeer.Id), baseTime.AddMinutes(-10));
            var convDeleted = Conversation.CreateDirect(new UserId(alice.Id), new UserId(deletedPeer.Id), baseTime.AddMinutes(-10));
            db.Conversations.AddRange(conv, convBlocked, convDeleted);
            await db.SaveChangesAsync();

            convId = conv.Id.Value;
            blockedConvId = convBlocked.Id.Value;
            deletedPeerConvId = convDeleted.Id.Value;

            // Seed 1 message before ClearedAtUtc, 1 hidden via DeleteForMe, and 105 visible messages
            var oldBeforeClear = Message.Create(conv.Id, new UserId(bob.Id), "zr-old-1", "Hidden before clear", baseTime);
            var hiddenForMe = Message.Create(conv.Id, new UserId(bob.Id), "zr-delme-1", "Hidden delete for me", baseTime.AddMinutes(5));
            db.Messages.AddRange(oldBeforeClear, hiddenForMe);

            var visibleMessages = new List<Message>(105);
            for (int i = 1; i <= 105; i++)
            {
                visibleMessages.Add(Message.Create(
                    conv.Id,
                    new UserId(i % 2 == 0 ? alice.Id : bob.Id),
                    $"zr-vis-{i}",
                    $"Message #{i:D3}",
                    baseTime.AddMinutes(10).AddSeconds(i)));
            }
            db.Messages.AddRange(visibleMessages);
            await db.SaveChangesAsync();

            var aliceState = UserConversationState.Create(conv.Id, new UserId(alice.Id));
            aliceState.ClearChat(baseTime.AddMinutes(2));
            db.UserConversationStates.Add(aliceState);
            db.UserMessageDeletions.Add(UserMessageDeletion.Create(conv.Id, hiddenForMe.Id, new UserId(alice.Id), baseTime.AddMinutes(6)));

            db.UserBlocks.Add(UserBlock.Create(new UserId(blockedPeer.Id), new UserId(alice.Id), DateTimeOffset.UtcNow));

            deletedPeer.IsDeleted = true;
            deletedPeer.DeletedAtUtc = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
        }

        await LoginWithCookieAsync(client, aliceUsername);

        // Request excessive pageSize=500 -> server caps at 100
        var resp = await client.GetAsync($"/api/conversations/{convId}/bundle?pageSize=500");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        AssertNoStoreHeaders(resp);

        var rawJson = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain(bobEmail, rawJson, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        Assert.Equal(aliceId.ToString(), root.GetProperty("viewerUserId").GetString(), ignoreCase: true);
        Assert.True(root.GetProperty("hasMore").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("nextCursor").GetString()));

        var convDto = root.GetProperty("conversation");
        Assert.False(convDto.GetProperty("isBlocked").GetBoolean());
        Assert.False(convDto.GetProperty("isPeerDeleted").GetBoolean());
        Assert.True(convDto.GetProperty("canSend").GetBoolean());

        var messages = root.GetProperty("messages");
        Assert.Equal(100, messages.GetArrayLength());

        // Ascending order (CreatedAtUtc ASC, Id ASC): latest 100 visible messages (#006..#105)
        Assert.Equal("Message #006", messages[0].GetProperty("content").GetString());
        Assert.Equal("Message #105", messages[99].GetProperty("content").GetString());

        DateTimeOffset prevTime = DateTimeOffset.MinValue;
        long prevId = 0;
        foreach (var m in messages.EnumerateArray())
        {
            var content = m.GetProperty("content").GetString();
            Assert.NotEqual("Hidden before clear", content);
            Assert.NotEqual("Hidden delete for me", content);

            var created = m.GetProperty("createdAtUtc").GetDateTimeOffset();
            var id = m.GetProperty("id").GetInt64();
            Assert.True(created > prevTime || (created == prevTime && id > prevId));
            prevTime = created;
            prevId = id;
        }

        // Blocked conversation -> isBlocked=true, canSend=false
        var blockedResp = await client.GetAsync($"/api/conversations/{blockedConvId}/bundle");
        Assert.Equal(HttpStatusCode.OK, blockedResp.StatusCode);
        using (var blockedDoc = JsonDocument.Parse(await blockedResp.Content.ReadAsStringAsync()))
        {
            var c = blockedDoc.RootElement.GetProperty("conversation");
            Assert.True(c.GetProperty("isBlocked").GetBoolean());
            Assert.False(c.GetProperty("canSend").GetBoolean());
        }

        // Soft-deleted peer -> isPeerDeleted=true, canSend=true (per AGENTS.md)
        var deletedPeerResp = await client.GetAsync($"/api/conversations/{deletedPeerConvId}/bundle");
        Assert.Equal(HttpStatusCode.OK, deletedPeerResp.StatusCode);
        using (var deletedDoc = JsonDocument.Parse(await deletedPeerResp.Content.ReadAsStringAsync()))
        {
            var c = deletedDoc.RootElement.GetProperty("conversation");
            Assert.True(c.GetProperty("isPeerDeleted").GetBoolean());
            Assert.True(c.GetProperty("canSend").GetBoolean());
            Assert.Equal("Deleted Account", c.GetProperty("otherParticipantName").GetString());
        }
    }

    [Fact]
    public async Task PostMessage_WithoutAntiforgeryHeader_Returns400ProblemDetailsWithAntiforgeryType()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        Guid convId;
        string aliceUsername;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_csrf_a", "Alice Csrf");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_csrf_b", "Bob Csrf");
            aliceUsername = alice.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        await LoginWithCookieAsync(client, aliceUsername);

        using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/conversations/{convId}/messages");
        req.Headers.Add("Accept", "application/json");
        req.Content = JsonContent.Create(new
        {
            content = "Blocked by CSRF check",
            idempotencyKey = Guid.NewGuid().ToString("N")
        });

        var response = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoStoreHeaders(response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(AntiforgeryProblemDetailsFilter.ProblemType, doc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task UnifiedShellRoutes_RenderValidBootstrap_ZeroDuplicateIds_AndEnforceAuthorization()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        Guid aliceId;
        string aliceUsername;
        Guid convId;
        Guid unauthorizedConvId;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_sh_a", "Alice Shell");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_sh_b", "Bob Shell");
            var charlie = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_sh_c", "Charlie Shell");
            aliceId = alice.Id;
            aliceUsername = alice.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), DateTimeOffset.UtcNow);
            var otherConv = Conversation.CreateDirect(new UserId(bob.Id), new UserId(charlie.Id), DateTimeOffset.UtcNow);
            db.Conversations.AddRange(conv, otherConv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
            unauthorizedConvId = otherConv.Id.Value;
        }

        await LoginWithCookieAsync(client, aliceUsername);

        var shellRoutes = new[]
        {
            ("/chat", (string?)null),
            ($"/chat/{convId}", convId.ToString()),
            ($"/Chat/Conversation/{convId}", convId.ToString())
        };

        foreach (var (route, expectedConvId) in shellRoutes)
        {
            var resp = await client.GetAsync(route);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            AssertNoStoreHeaders(resp);

            var html = await resp.Content.ReadAsStringAsync();
            Assert.Contains("id=\"chat-shell-root\"", html);
            Assert.Contains("id=\"app-sidebar-pane\"", html);
            Assert.Contains("id=\"app-main-pane\"", html);
            Assert.Contains("data-conversation-link=\"true\"", html);
            Assert.Contains("__RequestVerificationToken", html);

            var bootstrapMatch = Regex.Match(
                html,
                @"<script\s+id=""chat-bootstrap""\s+type=""application/json"">([\s\S]*?)</script>",
                RegexOptions.IgnoreCase);
            Assert.True(bootstrapMatch.Success, $"Missing #chat-bootstrap JSON on {route}");

            using var doc = JsonDocument.Parse(bootstrapMatch.Groups[1].Value);
            var root = doc.RootElement;
            Assert.Equal(aliceId.ToString(), root.GetProperty("currentUserId").GetString(), ignoreCase: true);
            Assert.Equal("/Account/Login", root.GetProperty("loginUrl").GetString());
            if (expectedConvId is null)
            {
                Assert.Equal(JsonValueKind.Null, root.GetProperty("initialConversationId").ValueKind);
            }
            else
            {
                Assert.Equal(expectedConvId, root.GetProperty("initialConversationId").GetString(), ignoreCase: true);
            }

            // Assert zero duplicate DOM IDs
            var idMatches = Regex.Matches(html, @"(?<![\w-])id=""([^""]+)""");
            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in idMatches)
            {
                var idVal = m.Groups[1].Value;
                Assert.True(seenIds.Add(idVal), $"Duplicate DOM id '{idVal}' found on route '{route}'.");
            }
        }

        var forbiddenShellResp = await client.GetAsync($"/chat/{unauthorizedConvId}");
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenShellResp.StatusCode);
        AssertNoStoreHeaders(forbiddenShellResp);
        AssertSecurityHeaders(forbiddenShellResp);
    }

    [Fact]
    public async Task PresenceController_WhenCallerIdIsNull_ReturnsUnauthorized401()
    {
        var controller = new PresenceController(
            new StubPresenceService(),
            new StubAnonymousCurrentUser());

        var presencesResult = await controller.GetPresences(CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(presencesResult);

        var peerResult = await controller.GetPeerPresence(Guid.NewGuid(), CancellationToken.None);
        Assert.IsType<UnauthorizedResult>(peerResult);
    }

    [Fact]
    public async Task GetConversationBundle_WithEmptyGuid_Returns400WithNoStore()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        string aliceUsername;
        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_eg_a", "Alice EmptyGuid");
            aliceUsername = alice.UserName!;
        }

        await LoginWithCookieAsync(client, aliceUsername);

        var resp = await client.GetAsync($"/api/conversations/{Guid.Empty}/bundle");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        AssertNoStoreHeaders(resp);
        AssertSecurityHeaders(resp);
    }

    [Fact]
    public async Task RateLimiter_OnRejected_ForApiAndXhr_Returns429ProblemDetailsWithNoStore()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        Guid convId;
        string isolatedUsername;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_rl_a", "Alice RateLimit");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_rl_b", "Bob RateLimit");
            isolatedUsername = alice.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        await LoginWithCookieAsync(client, isolatedUsername);

        // Exhaust the 60 req/min "messages" rate-limiter partition for this dedicated test user
        for (int i = 0; i < 60; i++)
        {
            var okResp = await client.GetAsync($"/api/conversations/{convId}/bundle");
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
        }

        // 61st request on /api route -> 429 ProblemDetails with no-store
        var rejectedApiResp = await client.GetAsync($"/api/conversations/{convId}/bundle");
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedApiResp.StatusCode);
        AssertNoStoreHeaders(rejectedApiResp);
        Assert.Equal("application/problem+json", rejectedApiResp.Content.Headers.ContentType?.MediaType);

        using (var doc = JsonDocument.Parse(await rejectedApiResp.Content.ReadAsStringAsync()))
        {
            Assert.Equal(429, doc.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("Too Many Requests", doc.RootElement.GetProperty("title").GetString());
            Assert.Contains("Rate limit exceeded", doc.RootElement.GetProperty("detail").GetString());
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("traceId").GetString()));
        }

        // 62nd request using X-Requested-With: XMLHttpRequest without Accept: application/json -> also 429 ProblemDetails with no-store
        using var xhrReq = new HttpRequestMessage(HttpMethod.Get, "/api/presence");
        xhrReq.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var rejectedXhrResp = await client.SendAsync(xhrReq);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejectedXhrResp.StatusCode);
        AssertNoStoreHeaders(rejectedXhrResp);
        Assert.Equal("application/problem+json", rejectedXhrResp.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ChatBootstrapJson_WithMaliciousScriptClosingTagInPeerProfile_EscapesSafelyAndParsesInFull()
    {
        using var factory = CreateFactory();
        using var client = CreateHttpsClient(factory);

        const string maliciousPayload = "</script><script>alert('xss')</script>";
        string aliceUsername;
        Guid convId;

        using (var scope = factory.Services.CreateScope())
        {
            var alice = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_xss_a", "Alice Safe");
            var bob = await SeedConfirmedUserAsync(scope.ServiceProvider, "zr_xss_b", maliciousPayload);
            aliceUsername = alice.UserName!;

            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            bob.Bio = maliciousPayload;
            var conv = Conversation.CreateDirect(new UserId(alice.Id), new UserId(bob.Id), DateTimeOffset.UtcNow);
            db.Conversations.Add(conv);
            await db.SaveChangesAsync();
            convId = conv.Id.Value;
        }

        await LoginWithCookieAsync(client, aliceUsername);

        var resp = await client.GetAsync($"/chat/{convId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();

        var bootstrapMatch = Regex.Match(
            html,
            @"<script\s+id=""chat-bootstrap""\s+type=""application/json"">([\s\S]*?)</script>",
            RegexOptions.IgnoreCase);
        Assert.True(bootstrapMatch.Success, "Expected #chat-bootstrap block to remain intact.");

        var bootstrapRaw = bootstrapMatch.Groups[1].Value;
        Assert.DoesNotContain("</script>", bootstrapRaw, StringComparison.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(bootstrapRaw);
        var meta = doc.RootElement.GetProperty("meta");
        Assert.Equal(maliciousPayload, meta.GetProperty("otherParticipantName").GetString());
        Assert.Equal(maliciousPayload, meta.GetProperty("otherParticipantBio").GetString());
    }

    private sealed class StubPresenceService : IPresenceService
    {
        public Task<IReadOnlyDictionary<Guid, UserPresenceDto>> GetPresenceForConversationsAsync(UserId callerId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<Guid, UserPresenceDto>>(new Dictionary<Guid, UserPresenceDto>());

        public Task<UserPresenceDto?> GetPeerPresenceAsync(UserId callerId, UserId peerId, CancellationToken ct = default)
            => Task.FromResult<UserPresenceDto?>(null);

        public Task RecordLastSeenAsync(UserId userId, DateTimeOffset lastSeenAtUtc, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<UserId>> GetEligiblePresencePeerIdsAsync(UserId userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserId>>([]);
    }

    private sealed class StubAnonymousCurrentUser : ICurrentUser
    {
        public UserId? Id => null;
        public bool IsAuthenticated => false;
    }
}
