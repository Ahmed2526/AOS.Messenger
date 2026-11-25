using System.Security.Claims;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Web.Filters;
using AOS.Messenger.Web.Hubs;
using AOS.Messenger.Web.Middleware;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AOS.Messenger.Tests;

/// <summary>
/// Comprehensive automated verification tests for Phase 6.5 Ticket 5:
/// 1. Newly blocked user does NOT receive subsequent broadcasts ([AUD-012]).
/// 2. Cross-origin /hubs/chat request rejected with 403 (CSWSH protection) ([AUD-013]).
/// 3. Connect-vs-revoke race & IHubFilter rejecting invocation after session revocation ([AUD-014]).
/// 4. 11th concurrent SignalR connection rejected cleanly ([AUD-032]).
/// 5. Revoking an individual SessionId disconnects only that session's SignalR connections ([AUD-008]).
/// 6. Replaying an idempotent message does NOT trigger a duplicate realtime broadcast ([AUD-048]).
/// </summary>
public sealed class Ticket5SignalRSecurityAndReliabilityTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public Ticket5SignalRSecurityAndReliabilityTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    // =========================================================================
    // 1. [AUD-012] Live Block-Aware Routing
    // Newly blocked user does NOT receive subsequent broadcasts.
    // =========================================================================

    [Fact]
    public async Task NewlyBlockedUser_DoesNotReceiveSubsequentBroadcasts_InRealtimeNotifier()
    {
        var hubContext = new TestHubContext();
        var logger = NullLogger<SignalRRealtimeNotifier>.Instance;

        var userA = UserId.New();
        var userB = UserId.New();
        var conv = Conversation.Create(userA, userB, DateTimeOffset.UtcNow, ConversationId.New());

        var convPersistence = new TestConversationPersistence(conv);
        var blockPersistence = new TestBlockPersistence();

        var notifier = new SignalRRealtimeNotifier(hubContext, convPersistence, blockPersistence, logger);

        var msgDto = new MessageDto(
            MessageId.New(),
            conv.Id,
            userA,
            "User A",
            "usera",
            "Message before block",
            DateTimeOffset.UtcNow,
            null,
            false,
            null,
            true);

        // Broadcast 1: Neither user has blocked the other -> broadcast succeeds to both
        await notifier.NotifyNewMessageAsync(conv.Id, msgDto);

        Assert.Single(hubContext.HubClients.TargetedUserIds);
        Assert.Contains(userA.Value.ToString(), hubContext.HubClients.TargetedUserIds[0]);
        Assert.Contains(userB.Value.ToString(), hubContext.HubClients.TargetedUserIds[0]);
        Assert.Single(hubContext.HubClients.UsersClient.ReceivedMessages);

        // Now User B blocks User A
        blockPersistence.AddBlock(userB, userA);

        hubContext.HubClients.TargetedUserIds.Clear();
        hubContext.HubClients.UsersClient.ReceivedMessages.Clear();

        var blockedMsgDto = new MessageDto(
            MessageId.New(),
            conv.Id,
            userA,
            "User A",
            "usera",
            "Message after block",
            DateTimeOffset.UtcNow,
            null,
            false,
            null,
            true);

        // Broadcast 2: Active block detected by live check -> broadcast suppressed!
        await notifier.NotifyNewMessageAsync(conv.Id, blockedMsgDto);
        await notifier.NotifyReceiptUpdatedAsync(userA, new ReceiptUpdateDto(conv.Id.Value, userB.Value, 2, DateTimeOffset.UtcNow, "read"));
        await notifier.NotifyTypingAsync(userA, new TypingIndicatorDto(conv.Id.Value, userB.Value, true));

        Assert.Empty(hubContext.HubClients.TargetedUserIds);
        Assert.Empty(hubContext.HubClients.UsersClient.ReceivedMessages);
        Assert.Empty(hubContext.HubClients.AllClient.ReceivedReceipts);
        Assert.Empty(hubContext.HubClients.AllClient.ReceivedTypingIndicators);

        // Broadcast 3 (different unblocked conversation): direct receipt & typing succeed
        var userC = UserId.New();
        var unblockedConv = Conversation.Create(userA, userC, DateTimeOffset.UtcNow, ConversationId.New());
        var unblockedNotifier = new SignalRRealtimeNotifier(hubContext, new TestConversationPersistence(unblockedConv), blockPersistence, logger);

        await unblockedNotifier.NotifyReceiptUpdatedAsync(userA, new ReceiptUpdateDto(unblockedConv.Id.Value, userC.Value, 5, DateTimeOffset.UtcNow, "delivered"));
        await unblockedNotifier.NotifyTypingAsync(userA, new TypingIndicatorDto(unblockedConv.Id.Value, userC.Value, true));

        Assert.Single(hubContext.HubClients.AllClient.ReceivedReceipts);
        Assert.Single(hubContext.HubClients.AllClient.ReceivedTypingIndicators);
    }

    [Fact]
    public async Task NewlyBlockedUser_PreventsMessageSendingAndBroadcast_InMessageService()
    {
        using var db = _fixture.CreateDbContext();
        var messagePersistence = new MessagePersistence(db);
        var conversationPersistence = new ConversationPersistence(db);
        var blockPersistence = new BlockPersistence(db);
        var userReader = new TestUserReader();
        var capturingNotifier = new CapturingRealtimeNotifier();
        var logger = NullLogger<MessageService>.Instance;

        var userA = UserId.New();
        var userB = UserId.New();
        userReader.AddProfile(userA, "Alice", "alice");
        userReader.AddProfile(userB, "Bob", "bob");

        var conv = Conversation.Create(userA, userB, DateTimeOffset.UtcNow);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var service = new MessageService(
            messagePersistence,
            conversationPersistence,
            blockPersistence,
            userReader,
            capturingNotifier,
            logger);

        // Message 1 succeeds and broadcasts
        var req1 = new SendMessageRequestDto("Hello before block", "block-test-key-1");
        var res1 = await service.SendMessageAsync(userA, conv.Id, req1);
        Assert.NotNull(res1);
        Assert.Single(capturingNotifier.Broadcasts);

        // User B blocks User A
        await blockPersistence.AddBlockAsync(UserBlock.Create(userB, userA, DateTimeOffset.UtcNow));

        // Message 2 from User A must throw ForbiddenException and NOT broadcast
        var req2 = new SendMessageRequestDto("Hello after block", "block-test-key-2");
        await Assert.ThrowsAsync<ForbiddenException>(() => service.SendMessageAsync(userA, conv.Id, req2));

        // Still only 1 broadcast total
        Assert.Single(capturingNotifier.Broadcasts);
    }

    // =========================================================================
    // 2. [AUD-013] Same-Origin Origin Validation (CSWSH Protection)
    // Cross-origin /hubs/chat request rejected with 403.
    // =========================================================================

    [Theory]
    [InlineData("/hubs/chat", "https://malicious-site.com", 403, false)]
    [InlineData("/hubs/chat/negotiate", "https://evil-attacker.io", 403, false)]
    [InlineData("/hubs/chat", "http://attacker.com:5000", 403, false)]
    [InlineData("/hubs/chat", "http://localhost:5000", 200, true)]
    [InlineData("/hubs/chat/negotiate", "http://localhost:5000", 200, true)]
    [InlineData("/hubs/chat", null, 200, true)]
    [InlineData("/api/messages", "https://malicious-site.com", 200, true)]
    public async Task CrossOrigin_SignalRRequest_OriginValidation(
        string requestPath,
        string? originHeader,
        int expectedStatusCode,
        bool expectNextCalled)
    {
        var nextCalled = false;
        RequestDelegate next = (HttpContext ctx) =>
        {
            nextCalled = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };

        var middleware = new SignalROriginValidationMiddleware(
            next,
            NullLogger<SignalROriginValidationMiddleware>.Instance);

        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5000);
        context.Request.Path = requestPath;

        if (originHeader is not null)
        {
            context.Request.Headers["Origin"] = originHeader;
        }

        await middleware.InvokeAsync(context);

        Assert.Equal(expectedStatusCode, context.Response.StatusCode);
        Assert.Equal(expectNextCalled, nextCalled);
    }

    [Fact]
    public async Task CrossOrigin_SignalRRequest_MatchesConfiguredAllowedOrigin()
    {
        var nextCalled = false;
        RequestDelegate next = (HttpContext ctx) =>
        {
            nextCalled = true;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        };

        var inMemorySettings = new Dictionary<string, string?>
        {
            ["SignalR:AllowedOrigin"] = "https://messenger.mycompany.com"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings)
            .Build();

        var middleware = new SignalROriginValidationMiddleware(
            next,
            NullLogger<SignalROriginValidationMiddleware>.Instance,
            configuration);

        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("internal-host", 443);
        context.Request.Path = "/hubs/chat";
        context.Request.Headers["Origin"] = "https://messenger.mycompany.com";

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.True(nextCalled);
    }

    // =========================================================================
    // 3. [AUD-014] Connect-vs-Revoke Race & IHubFilter Inactive Session Rejection
    // =========================================================================

    [Fact]
    public async Task ConnectVsRevokeRace_OnConnectedAsync_AbortsAndUnregistersWhenSessionIsRevoked()
    {
        var tracker = new InMemoryConnectionTracker();
        var currentUser = new TestCurrentUser();
        var convService = new TestConversationService();
        var sessionService = new TestSessionService();
        var logger = NullLogger<ChatHub>.Instance;

        var hub = new ChatHub(convService, tracker, currentUser, logger, sessionService);

        var user = UserId.New();
        var sessionId = Guid.NewGuid();
        currentUser.Id = user;

        var abortCalled = false;
        var userPrincipal = CreateClaimsPrincipal(user, sessionId);
        var callerContext = new TestHubCallerContext("race-conn-1", userPrincipal, () => abortCalled = true);
        hub.Context = callerContext;

        // Simulate session revoked in SQL Server prior to re-verification in OnConnectedAsync
        sessionService.SetSessionActive(sessionId, false);

        await hub.OnConnectedAsync();

        // Must abort connection
        Assert.True(abortCalled, "Context.Abort() must be called when session is revoked.");

        // Must NOT remain registered in connection tracker
        Assert.DoesNotContain("race-conn-1", tracker.GetConnections(user));
        Assert.False(tracker.IsUserOnline(user));
    }

    [Fact]
    public async Task SessionAndThrottleHubFilter_RejectsInvocation_WhenSessionRevoked()
    {
        var sessionService = new TestSessionService();
        var timeProvider = TimeProvider.System;
        var filter = new SessionAndThrottleHubFilter(
            sessionService,
            timeProvider,
            NullLogger<SessionAndThrottleHubFilter>.Instance);

        var user = UserId.New();
        var sessionId = Guid.NewGuid();

        var abortCalled = false;
        var userPrincipal = CreateClaimsPrincipal(user, sessionId);
        var callerContext = new TestHubCallerContext("session-revoke-conn", userPrincipal, () => abortCalled = true);

        var invocationContext = CreateInvocationContext(callerContext);

        // Session is revoked in database
        sessionService.SetSessionActive(sessionId, false);

        var nextCalled = false;
        Func<HubInvocationContext, ValueTask<object?>> next = (ctx) =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        };

        var ex = await Assert.ThrowsAsync<HubException>(async () =>
        {
            await filter.InvokeMethodAsync(invocationContext, next);
        });

        Assert.Contains("Session has been revoked or expired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(abortCalled, "Context.Abort() must be called when revoked session invokes hub method.");
        Assert.False(nextCalled, "Hub method invocation pipeline must NOT proceed when session is revoked.");
    }

    [Fact]
    public async Task SessionAndThrottleHubFilter_RejectsInvocation_WhenSessionClaimMissing()
    {
        var sessionService = new TestSessionService();
        var filter = new SessionAndThrottleHubFilter(
            sessionService,
            TimeProvider.System,
            NullLogger<SessionAndThrottleHubFilter>.Instance);

        var user = UserId.New();
        var abortCalled = false;
        var unauthenticatedPrincipal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Value.ToString())
        }, "TestAuth"));
        var callerContext = new TestHubCallerContext("missing-claim-conn", unauthenticatedPrincipal, () => abortCalled = true);

        var invocationContext = CreateInvocationContext(callerContext);

        var ex = await Assert.ThrowsAsync<HubException>(async () =>
        {
            await filter.InvokeMethodAsync(invocationContext, _ => ValueTask.FromResult<object?>(null));
        });

        Assert.Contains("Session has been revoked or expired", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(abortCalled);
    }

    [Fact]
    public async Task SessionAndThrottleHubFilter_ThrottlesFloodOfInvocations()
    {
        var sessionService = new TestSessionService();
        var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var filter = new SessionAndThrottleHubFilter(
            sessionService,
            timeProvider,
            NullLogger<SessionAndThrottleHubFilter>.Instance);

        var user = UserId.New();
        var sessionId = Guid.NewGuid();
        sessionService.SetSessionActive(sessionId, true);

        var userPrincipal = CreateClaimsPrincipal(user, sessionId);
        var callerContext = new TestHubCallerContext("flood-conn-1", userPrincipal);

        var invocationContext = CreateInvocationContext(callerContext);

        // First 60 invocations in 1 minute succeed
        for (int i = 0; i < 60; i++)
        {
            var res = await filter.InvokeMethodAsync(invocationContext, _ => ValueTask.FromResult<object?>("ok"));
            Assert.Equal("ok", res);
        }

        // 61st invocation within the same 1-minute window is throttled
        var ex = await Assert.ThrowsAsync<HubException>(async () =>
        {
            await filter.InvokeMethodAsync(invocationContext, _ => ValueTask.FromResult<object?>("ok"));
        });

        Assert.Contains("Rate limit exceeded", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Advance time by 61 seconds -> throttle resets
        timeProvider.Advance(TimeSpan.FromSeconds(61));
        var nextAttempt = await filter.InvokeMethodAsync(invocationContext, _ => ValueTask.FromResult<object?>("ok"));
        Assert.Equal("ok", nextAttempt);
    }

    // =========================================================================
    // 4. [AUD-032] 11th Concurrent SignalR Connection Rejected Cleanly
    // =========================================================================

    [Fact]
    public void ConnectionTracker_RejectsEleventhConcurrentConnectionCleanly()
    {
        var tracker = new InMemoryConnectionTracker();
        var user = UserId.New();
        var sessionId = Guid.NewGuid();

        // 1 to 10 connections succeed
        for (int i = 1; i <= 10; i++)
        {
            var added = tracker.TryAddConnection(user, sessionId, $"conn-{i}");
            Assert.True(added, $"Connection {i} should be accepted.");
        }

        Assert.Equal(10, tracker.GetConnections(user).Count);

        // 11th connection rejected cleanly
        var eleventhAccepted = tracker.TryAddConnection(user, sessionId, "conn-11");
        Assert.False(eleventhAccepted, "11th concurrent connection must be rejected.");
        Assert.Equal(10, tracker.GetConnections(user).Count);

        // Existing connection re-registration updates without consuming extra slot
        var readdAccepted = tracker.TryAddConnection(user, sessionId, "conn-1");
        Assert.True(readdAccepted, "Updating an existing connection must succeed.");
        Assert.Equal(10, tracker.GetConnections(user).Count);

        // Disconnecting one connection allows a new connection to succeed
        tracker.RemoveConnection(user, "conn-1");
        Assert.Equal(9, tracker.GetConnections(user).Count);

        var newAccepted = tracker.TryAddConnection(user, sessionId, "conn-11");
        Assert.True(newAccepted, "New connection should be accepted once a slot is freed.");
        Assert.Equal(10, tracker.GetConnections(user).Count);
    }

    [Fact]
    public async Task ChatHub_OnConnectedAsync_AbortsEleventhConnectionCleanly()
    {
        var tracker = new InMemoryConnectionTracker();
        var currentUser = new TestCurrentUser();
        var convService = new TestConversationService();
        var sessionService = new TestSessionService();
        var logger = NullLogger<ChatHub>.Instance;

        var user = UserId.New();
        var sessionId = Guid.NewGuid();
        currentUser.Id = user;
        sessionService.SetSessionActive(sessionId, true);

        // Pre-fill tracker with 10 active connections
        for (int i = 1; i <= 10; i++)
        {
            tracker.TryAddConnection(user, sessionId, $"conn-{i}");
        }

        var hub = new ChatHub(convService, tracker, currentUser, logger, sessionService);

        var abortCalled = false;
        var userPrincipal = CreateClaimsPrincipal(user, sessionId);
        var callerContext = new TestHubCallerContext("conn-11", userPrincipal, () => abortCalled = true);
        hub.Context = callerContext;

        await hub.OnConnectedAsync();

        Assert.True(abortCalled, "Context.Abort() must be called when connection cap (10) is reached.");
        Assert.DoesNotContain("conn-11", tracker.GetConnections(user));
        Assert.Equal(10, tracker.GetConnections(user).Count);
    }

    // =========================================================================
    // 5. [AUD-008] Revoking Individual SessionId Disconnects ONLY That Session
    // =========================================================================

    [Fact]
    public async Task RevokingIndividualSession_DisconnectsOnlyThatSessionsConnections()
    {
        var tracker = new InMemoryConnectionTracker();
        var terminator = new SignalRSessionConnectionTerminator(
            tracker,
            NullLogger<SignalRSessionConnectionTerminator>.Instance);

        var user = UserId.New();
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();

        var sessionAAbort1 = false;
        var sessionAAbort2 = false;
        var sessionBAbort1 = false;
        var sessionBAbort2 = false;

        // Register 2 connections for Session A
        tracker.TryAddConnection(user, sessionA, "conn-a1", () => sessionAAbort1 = true);
        tracker.TryAddConnection(user, sessionA, "conn-a2", () => sessionAAbort2 = true);

        // Register 2 connections for Session B
        tracker.TryAddConnection(user, sessionB, "conn-b1", () => sessionBAbort1 = true);
        tracker.TryAddConnection(user, sessionB, "conn-b2", () => sessionBAbort2 = true);

        Assert.Equal(4, tracker.GetConnections(user).Count);
        Assert.True(tracker.IsUserOnline(user));

        // Revoke Session A ONLY
        await terminator.DisconnectSessionAsync(sessionA);

        // Session A callbacks must be executed
        Assert.True(sessionAAbort1, "Session A connection 1 must be aborted.");
        Assert.True(sessionAAbort2, "Session A connection 2 must be aborted.");

        // Session B callbacks must NOT be executed!
        Assert.False(sessionBAbort1, "Session B connection 1 must NOT be aborted.");
        Assert.False(sessionBAbort2, "Session B connection 2 must NOT be aborted.");

        // Connections in tracker: only Session B connections remain
        var remainingConns = tracker.GetConnections(user);
        Assert.Equal(2, remainingConns.Count);
        Assert.Contains("conn-b1", remainingConns);
        Assert.Contains("conn-b2", remainingConns);
        Assert.DoesNotContain("conn-a1", remainingConns);
        Assert.DoesNotContain("conn-a2", remainingConns);

        // User is still online on Session B
        Assert.True(tracker.IsUserOnline(user));

        // Now revoke Session B
        await terminator.DisconnectSessionAsync(sessionB);

        Assert.True(sessionBAbort1);
        Assert.True(sessionBAbort2);
        Assert.Empty(tracker.GetConnections(user));
        Assert.False(tracker.IsUserOnline(user));
    }

    [Fact]
    public async Task DisconnectUserSessionsAsync_DisconnectsAllUserConnections()
    {
        var tracker = new InMemoryConnectionTracker();
        var terminator = new SignalRSessionConnectionTerminator(
            tracker,
            NullLogger<SignalRSessionConnectionTerminator>.Instance);

        var user = UserId.New();
        var session1 = Guid.NewGuid();
        var session2 = Guid.NewGuid();

        var abort1 = false;
        var abort2 = false;

        tracker.TryAddConnection(user, session1, "conn-1", () => abort1 = true);
        tracker.TryAddConnection(user, session2, "conn-2", () => abort2 = true);

        await terminator.DisconnectUserSessionsAsync(user);

        Assert.True(abort1);
        Assert.True(abort2);
        Assert.Empty(tracker.GetConnections(user));
        Assert.False(tracker.IsUserOnline(user));
    }

    // =========================================================================
    // 6. [AUD-048] Idempotent Message Replay Realtime Broadcast Suppression
    // =========================================================================

    [Fact]
    public async Task ReplayingIdempotentMessage_DoesNotTriggerDuplicateRealtimeBroadcast()
    {
        using var db = _fixture.CreateDbContext();
        var messagePersistence = new MessagePersistence(db);
        var conversationPersistence = new ConversationPersistence(db);
        var blockPersistence = new BlockPersistence(db);
        var userReader = new TestUserReader();
        var capturingNotifier = new CapturingRealtimeNotifier();
        var logger = NullLogger<MessageService>.Instance;

        var userA = UserId.New();
        var userB = UserId.New();
        userReader.AddProfile(userA, "Alice", "alice");
        userReader.AddProfile(userB, "Bob", "bob");

        var conv = Conversation.Create(userA, userB, DateTimeOffset.UtcNow);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var service = new MessageService(
            messagePersistence,
            conversationPersistence,
            blockPersistence,
            userReader,
            capturingNotifier,
            logger);

        const string idempotencyKey = "replay-key-ticket5-001";
        var sendRequest = new SendMessageRequestDto("Hello Idempotent World", idempotencyKey);

        // First send: newly created message
        var firstResult = await service.SendMessageAsync(userA, conv.Id, sendRequest);
        Assert.NotNull(firstResult);
        Assert.Equal("Hello Idempotent World", firstResult.Content);
        Assert.Single(capturingNotifier.Broadcasts);
        Assert.Equal(firstResult.Id, capturingNotifier.Broadcasts[0].Message.Id);

        // Second send: identical idempotency key replay
        var secondResult = await service.SendMessageAsync(userA, conv.Id, sendRequest);
        Assert.NotNull(secondResult);
        Assert.Equal(firstResult.Id, secondResult.Id);
        Assert.Equal("Hello Idempotent World", secondResult.Content);

        // Realtime broadcast count must REMAIN exactly 1 (duplicate suppressed!)
        Assert.Single(capturingNotifier.Broadcasts);
    }

    [Fact]
    public async Task MessagePersistence_AddAsync_ReturnsIsNewlyCreatedFlagCorrectly()
    {
        using var db = _fixture.CreateDbContext();
        var persistence = new MessagePersistence(db);

        var userA = UserId.New();
        var userB = UserId.New();
        var conv = Conversation.Create(userA, userB, DateTimeOffset.UtcNow, ConversationId.New());
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var msg = Message.Create(
            conv.Id,
            userA,
            "persistence-flag-key-1",
            "Initial Content",
            DateTimeOffset.UtcNow);

        var firstPersist = await persistence.AddAsync(msg);
        Assert.True(firstPersist.IsNewlyCreated);
        Assert.Equal(msg.Id, firstPersist.Message.Id);

        // Replay same idempotency key with identical content
        var replayMsg = Message.Create(
            conv.Id,
            userA,
            "persistence-flag-key-1",
            "Initial Content",
            DateTimeOffset.UtcNow);

        var secondPersist = await persistence.AddAsync(replayMsg);
        Assert.False(secondPersist.IsNewlyCreated);
        Assert.Equal(firstPersist.Message.Id, secondPersist.Message.Id);
    }

    // =========================================================================
    // Test Doubles & Helpers
    // =========================================================================

    private static HubInvocationContext CreateInvocationContext(
        HubCallerContext callerContext,
        IServiceProvider? serviceProvider = null)
    {
        var hubInstance = new ChatHub(
            new TestConversationService(),
            new InMemoryConnectionTracker(),
            new TestCurrentUser(),
            NullLogger<ChatHub>.Instance);

        var methodInfo = typeof(ChatHub).GetMethod(nameof(ChatHub.JoinConversation))!;
        return new HubInvocationContext(
            callerContext,
            serviceProvider ?? new EmptyServiceProvider(),
            hubInstance,
            methodInfo,
            new object?[] { Guid.NewGuid() });
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static ClaimsPrincipal CreateClaimsPrincipal(UserId userId, Guid sessionId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
            new Claim("SessionId", sessionId.ToString())
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly Action? _onAbort;

        public TestHubCallerContext(string connectionId, ClaimsPrincipal? user = null, Action? onAbort = null)
        {
            ConnectionId = connectionId;
            User = user;
            _onAbort = onAbort;
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;

        public override void Abort()
        {
            _onAbort?.Invoke();
        }
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated => Id is not null;
    }

    private sealed class TestSessionService : ISessionService
    {
        private readonly Dictionary<Guid, bool> _sessions = new();

        public void SetSessionActive(Guid sessionId, bool isActive) => _sessions[sessionId] = isActive;

        public Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken ct = default)
        {
            _sessions.TryGetValue(sessionId, out var active);
            return Task.FromResult(active);
        }

        public Task<IReadOnlyList<UserSessionDto>> GetActiveSessionsAsync(UserId userId, Guid? currentSessionId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserSessionDto>>(Array.Empty<UserSessionDto>());

        public Task<Guid> CreateSessionAsync(UserId userId, string deviceName, string? ipAddress, string? userAgent, CancellationToken ct = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task<bool> RevokeSessionAsync(UserId userId, Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task RevokeOtherSessionsAsync(UserId userId, Guid currentSessionId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task UpdateSessionActivityAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RevokeAllSessionsAsync(UserId userId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class TestConversationService : IConversationService
    {
        public Task<ConversationDetailDto> GetConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) =>
            Task.FromResult(new ConversationDetailDto(
                conversationId,
                UserId.New(),
                "Other User",
                "other_user",
                null,
                false,
                false,
                false,
                false,
                DateTimeOffset.UtcNow));

        public Task<ConversationDetailDto> GetOrCreateConversationAsync(UserId currentUserId, UserId targetUserId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ConversationSummaryDto>> GetConversationListAsync(UserId currentUserId, bool includeArchived = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> PinConversationAsync(UserId currentUserId, ConversationId conversationId, bool isPinned, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ArchiveConversationAsync(UserId currentUserId, ConversationId conversationId, bool isArchived, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> MuteConversationAsync(UserId currentUserId, ConversationId conversationId, bool isMuted, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ClearChatAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class TestConversationPersistence : IConversationPersistence
    {
        private readonly Conversation _conversation;

        public TestConversationPersistence(Conversation conversation)
        {
            _conversation = conversation;
        }

        public Task<Conversation?> GetByIdAsync(ConversationId id, CancellationToken ct = default) =>
            Task.FromResult<Conversation?>(_conversation.Id == id ? _conversation : null);

        public Task<Conversation?> GetByParticipantsAsync(UserId userA, UserId userB, CancellationToken ct = default) =>
            Task.FromResult<Conversation?>(_conversation);

        public Task<Conversation> AddAsync(Conversation conversation, CancellationToken ct = default) =>
            Task.FromResult(conversation);

        public Task<UserConversationState?> GetUserStateAsync(ConversationId conversationId, UserId userId, CancellationToken ct = default) =>
            Task.FromResult<UserConversationState?>(null);

        public Task UpsertUserStateAsync(UserConversationState state, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Conversation>> GetConversationsForUserAsync(UserId userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Conversation>>(new[] { _conversation });
        public Task<IReadOnlyList<UserConversationState>> GetUserStatesForUserAsync(UserId userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserConversationState>>(Array.Empty<UserConversationState>());
        public Task<IReadOnlyList<UserConversationState>> GetUserStatesForConversationsAsync(IReadOnlyList<ConversationId> conversationIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserConversationState>>(Array.Empty<UserConversationState>());
        public Task<IReadOnlyList<UserConversationState>> GetPeerStatesForConversationsAsync(IReadOnlyList<ConversationId> conversationIds, UserId callerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserConversationState>>(Array.Empty<UserConversationState>());
        public Task<Message?> GetLastMessageAsync(ConversationId conversationId, DateTimeOffset? minCreatedAtUtc = null, CancellationToken ct = default) =>
            Task.FromResult<Message?>(null);
        public Task<int> GetUnreadCountAsync(ConversationId conversationId, UserId userId, MessageId? lastReadMessageId = null, DateTimeOffset? minCreatedAtUtc = null, CancellationToken ct = default) =>
            Task.FromResult(0);
        public Task<bool> HasMessagesAfterAsync(ConversationId conversationId, DateTimeOffset afterUtc, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<Dictionary<ConversationId, Message>> GetLastMessagesForConversationsAsync(IReadOnlyList<ConversationId> conversationIds, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<ConversationId, Message>());
        public Task<Dictionary<ConversationId, Message>> GetLastMessagesForConversationsAsync(IReadOnlyList<ConversationId> conversationIds, UserId callerId, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<ConversationId, Message>());
        public Task<Dictionary<ConversationId, int>> GetUnreadCountsForConversationsAsync(UserId userId, IReadOnlyList<(ConversationId ConversationId, MessageId? LastReadMessageId, DateTimeOffset? ClearedAtUtc)> requests, CancellationToken ct = default) =>
            Task.FromResult(new Dictionary<ConversationId, int>());
        public Task<HashSet<ConversationId>> GetConversationsWithMessagesAfterAsync(IReadOnlyList<(ConversationId ConversationId, DateTimeOffset ThresholdUtc)> requests, CancellationToken ct = default) =>
            Task.FromResult(new HashSet<ConversationId>());
        public Task<HashSet<ConversationId>> GetConversationsWithMessagesAfterAsync(IReadOnlyList<(ConversationId ConversationId, DateTimeOffset ThresholdUtc)> requests, UserId callerId, CancellationToken ct = default) =>
            Task.FromResult(new HashSet<ConversationId>());
        public Task<bool> AdvanceDeliveredWatermarkAsync(ConversationId conversationId, UserId userId, MessageId messageId, DateTimeOffset msgCreatedAtUtc, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<bool> AdvanceReadWatermarkAsync(ConversationId conversationId, UserId userId, MessageId messageId, DateTimeOffset msgCreatedAtUtc, CancellationToken ct = default) =>
            Task.FromResult(false);
        public Task<IReadOnlyList<UserId>> GetPeerUserIdsWithExistingConversationAsync(UserId userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserId>>(Array.Empty<UserId>());
        public Task<UserId?> GetVerifiedTypingRecipientAsync(UserId senderId, ConversationId conversationId, CancellationToken ct = default) =>
            Task.FromResult<UserId?>(_conversation.Id == conversationId && _conversation.HasParticipant(senderId)
                ? _conversation.GetOtherParticipant(senderId)
                : null);
    }

    private sealed class TestBlockPersistence : IBlockPersistence
    {
        private readonly HashSet<(UserId, UserId)> _blocks = new();

        public void AddBlock(UserId blocker, UserId blocked) => _blocks.Add((blocker, blocked));

        public Task<bool> IsBlockedAsync(UserId userA, UserId userB, CancellationToken ct = default) =>
            Task.FromResult(_blocks.Contains((userA, userB)));

        public Task<bool> IsBlockedBidirectionalAsync(UserId userA, UserId userB, CancellationToken ct = default) =>
            Task.FromResult(_blocks.Contains((userA, userB)) || _blocks.Contains((userB, userA)));

        public Task<UserBlock?> GetBlockAsync(UserId blockerId, UserId blockedUserId, CancellationToken ct = default) =>
            Task.FromResult<UserBlock?>(null);

        public Task AddBlockAsync(UserBlock block, CancellationToken ct = default)
        {
            _blocks.Add((block.BlockerId, block.BlockedUserId));
            return Task.CompletedTask;
        }

        public Task RemoveBlockAsync(UserId blockerId, UserId blockedUserId, CancellationToken ct = default)
        {
            _blocks.Remove((blockerId, blockedUserId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UserId>> GetBlockedUserIdsAsync(UserId blockerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserId>>(Array.Empty<UserId>());

        public Task<IReadOnlyList<UserBlock>> GetBlocksByBlockerIdAsync(UserId blockerId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserBlock>>(Array.Empty<UserBlock>());

        public Task<IReadOnlySet<UserId>> GetBidirectionalBlockedUserIdsAsync(UserId userId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<UserId>>(new HashSet<UserId>());
    }

    private sealed class TestUserReader : IUserReader
    {
        private readonly Dictionary<UserId, UserProfileDto> _profiles = new();

        public void AddProfile(UserId id, string displayName, string userName)
        {
            _profiles[id] = new UserProfileDto(id, userName, displayName, null, false);
        }

        public Task<UserProfileDto?> GetByIdAsync(UserId id, CancellationToken ct = default)
        {
            _profiles.TryGetValue(id, out var p);
            return Task.FromResult(p);
        }

        public Task<IReadOnlyList<UserProfileDto>> GetByIdsAsync(IEnumerable<UserId> ids, CancellationToken ct = default)
        {
            var list = ids.Where(_profiles.ContainsKey).Select(id => _profiles[id]).ToList();
            return Task.FromResult<IReadOnlyList<UserProfileDto>>(list);
        }

        public Task<UserProfileDto?> GetByUserNameAsync(string userName, CancellationToken ct = default)
        {
            var p = _profiles.Values.FirstOrDefault(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(p);
        }

        public Task<UserProfileDto?> GetByEmailAsync(string email, CancellationToken ct = default) => Task.FromResult<UserProfileDto?>(null);
        public Task<bool> ExistsAsync(UserId id, CancellationToken ct = default) => Task.FromResult(_profiles.ContainsKey(id));
        public Task<IReadOnlySet<UserId>> GetActiveUserIdsAsync(IEnumerable<UserId> userIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<UserId>>(userIds.Where(_profiles.ContainsKey).ToHashSet());
        public Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(UserId currentUserId, string query, IReadOnlySet<UserId> excludedUserIds, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserSearchResultDto>>([]);
    }

    private sealed class CapturingRealtimeNotifier : IRealtimeNotifier
    {
        public List<(ConversationId ConversationId, MessageDto Message)> Broadcasts { get; } = new();

        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default)
        {
            Broadcasts.Add((conversationId, message));
            return Task.CompletedTask;
        }

        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestChatClient : IChatClient
    {
        public List<MessageDto> ReceivedMessages { get; } = new();
        public List<ReceiptUpdateDto> ReceivedReceipts { get; } = new();
        public List<TypingIndicatorDto> ReceivedTypingIndicators { get; } = new();

        public Task ReceiveMessage(MessageDto message)
        {
            ReceivedMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task ReceiveReceiptUpdated(ReceiptUpdateDto receipt)
        {
            ReceivedReceipts.Add(receipt);
            return Task.CompletedTask;
        }

        public Task ReceiveTypingIndicator(TypingIndicatorDto typing)
        {
            ReceivedTypingIndicators.Add(typing);
            return Task.CompletedTask;
        }
        public Task ReceivePresenceChanged(UserPresenceDto presence) => Task.CompletedTask;
        public Task ReceiveMessageEdited(MessageEditedDto message) => Task.CompletedTask;
        public Task ReceiveMessageDeletedForEveryone(MessageDeletedForEveryoneDto message) => Task.CompletedTask;
        public Task ReceiveMessageDeletedForMe(MessageDeletedForMeDto message) => Task.CompletedTask;
        public Task ReceiveReactionUpdated(ReactionUpdatedDto reaction) => Task.CompletedTask;
        public Task ReceiveUserProfileUpdated(UserProfileUpdatedDto profile) => Task.CompletedTask;
    }

    private sealed class TestHubContext : IHubContext<ChatHub, IChatClient>
    {
        public TestHubClients HubClients { get; } = new();
        public IHubClients<IChatClient> Clients => HubClients;
        public IGroupManager Groups => throw new NotImplementedException();
    }

    private sealed class TestHubClients : IHubClients<IChatClient>
    {
        public TestChatClient AllClient { get; } = new();
        public TestChatClient UsersClient { get; } = new();
        public List<IReadOnlyList<string>> TargetedUserIds { get; } = new();

        public IChatClient All => AllClient;
        public IChatClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => AllClient;
        public IChatClient Client(string connectionId) => AllClient;
        public IChatClient Clients(IReadOnlyList<string> connectionIds) => AllClient;
        public IChatClient Group(string groupName) => AllClient;
        public IChatClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => AllClient;
        public IChatClient Groups(IReadOnlyList<string> groupNames) => AllClient;
        public IChatClient User(string userId) => AllClient;

        public IChatClient Users(IReadOnlyList<string> userIds)
        {
            TargetedUserIds.Add(userIds);
            return UsersClient;
        }
    }
}
