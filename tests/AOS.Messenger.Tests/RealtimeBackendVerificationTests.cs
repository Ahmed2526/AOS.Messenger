using System.Reflection;
using System.Security.Claims;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Web.Hubs;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AOS.Messenger.Tests;

/// <summary>
/// Rigorous, independent QA verification tests for Phase 6 Backend & Hub deliverables:
/// 1. Hub Architecture & Security (sealed, Hub[IChatClient], [Authorize], zero Infrastructure/EF Core coupling, participant authorization)
/// 2. Commit-First Broadcast Invariant (persistence commits first, zero broadcast on failure, failure isolation on notifier exception)
/// 3. Multi-Connection Tracking (multi-device, active conversation tracking, cleanup on disconnect, thread safety)
/// 4. Application Layer Purity (zero references to SignalR types or packages in Application)
/// 5. Repository Directives Invariant (zero #region directives across all C# files)
/// </summary>
public sealed class RealtimeBackendVerificationTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public RealtimeBackendVerificationTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }
    // =========================================================================
    // 1. Hub Architecture & Security
    // =========================================================================

    [Fact]
    public void ChatHub_IsSealed_InheritsFromHubWithIChatClient_AndDecoratedWithAuthorizeAttribute()
    {
        var hubType = typeof(ChatHub);

        // Sealed check
        Assert.True(hubType.IsSealed, "ChatHub must be sealed to prevent unintended inheritance.");

        // Type hierarchy check
        Assert.True(
            typeof(Hub<IChatClient>).IsAssignableFrom(hubType),
            "ChatHub must inherit from strongly-typed Hub<IChatClient>.");

        // [Authorize] attribute check
        var authorizeAttribute = hubType.GetCustomAttribute<AuthorizeAttribute>(inherit: true);
        Assert.NotNull(authorizeAttribute);
    }

    [Fact]
    public void ChatHub_HasZeroDirectReferencesToInfrastructure_OrEntityFrameworkCore()
    {
        var hubType = typeof(ChatHub);
        const string infraNamespace = "AOS.Messenger.Infrastructure";
        const string efNamespace = "Microsoft.EntityFrameworkCore";

        // Check constructors
        foreach (var constructor in hubType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                AssertTypeNotCoupled(parameter.ParameterType, $"ChatHub constructor parameter '{parameter.Name}'");
            }
        }

        // Check instance fields
        foreach (var field in hubType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            AssertTypeNotCoupled(field.FieldType, $"ChatHub field '{field.Name}'");
        }

        // Check properties
        foreach (var prop in hubType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            AssertTypeNotCoupled(prop.PropertyType, $"ChatHub property '{prop.Name}'");
        }

        // Check declared methods
        foreach (var method in hubType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            AssertTypeNotCoupled(method.ReturnType, $"ChatHub method '{method.Name}' return type");
            foreach (var parameter in method.GetParameters())
            {
                AssertTypeNotCoupled(parameter.ParameterType, $"ChatHub method '{method.Name}' parameter '{parameter.Name}'");
            }
        }

        static void AssertTypeNotCoupled(Type type, string location)
        {
            var fullName = type.FullName ?? type.Name;
            Assert.False(
                fullName.StartsWith(infraNamespace, StringComparison.Ordinal),
                $"Forbidden dependency: {location} references Infrastructure type '{fullName}'.");
            Assert.False(
                fullName.StartsWith(efNamespace, StringComparison.Ordinal),
                $"Forbidden dependency: {location} references EF Core type '{fullName}'.");
        }
    }

    [Fact]
    public async Task ChatHub_JoinConversation_WhenParticipantIsAuthorized_AddsToGroupAndTracksActiveConversation()
    {
        var (hub, tracker, currentUser, convService, groups) = CreateHubFixture();
        var callerUserId = UserId.New();
        currentUser.Id = callerUserId;

        var conversationGuid = Guid.NewGuid();
        convService.OnGetConversationAsync = (uid, cid, _) =>
        {
            Assert.Equal(callerUserId, uid);
            Assert.Equal(conversationGuid, cid.Value);
            return Task.FromResult(CreateConversationDetail(cid, uid));
        };

        await hub.JoinConversation(conversationGuid);

        Assert.Equal(conversationGuid, tracker.GetActiveConversation("test-connection-1"));
    }

    [Fact]
    public async Task ChatHub_JoinConversation_WhenUserIsNotParticipant_ThrowsHubException()
    {
        var (hub, tracker, currentUser, convService, groups) = CreateHubFixture();
        var callerUserId = UserId.New();
        currentUser.Id = callerUserId;

        var conversationGuid = Guid.NewGuid();
        convService.OnGetConversationAsync = (_, _, _) =>
            throw new ForbiddenException("User is not a participant in this conversation.");

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.JoinConversation(conversationGuid));
        Assert.Contains("participant", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Verify socket was not joined to group and active conversation was not tracked
        Assert.Empty(groups.Added);
        Assert.Null(tracker.GetActiveConversation("test-connection-1"));
    }

    [Fact]
    public async Task ChatHub_JoinConversation_WhenUserIsNotAuthenticated_ThrowsHubException()
    {
        var (hub, tracker, currentUser, _, groups) = CreateHubFixture();
        currentUser.Id = null; // Unauthenticated

        var conversationGuid = Guid.NewGuid();

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.JoinConversation(conversationGuid));
        Assert.Contains("authenticated", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(groups.Added);
        Assert.Null(tracker.GetActiveConversation("test-connection-1"));
    }

    [Fact]
    public async Task ChatHub_JoinConversation_WhenConversationNotFound_ThrowsHubException()
    {
        var (hub, tracker, currentUser, convService, groups) = CreateHubFixture();
        currentUser.Id = UserId.New();

        var conversationGuid = Guid.NewGuid();
        convService.OnGetConversationAsync = (_, _, _) =>
            throw new NotFoundException("Conversation", conversationGuid);

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.JoinConversation(conversationGuid));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(groups.Added);
        Assert.Null(tracker.GetActiveConversation("test-connection-1"));
    }

    [Fact]
    public async Task ChatHub_JoinConversation_WhenConversationIdIsInvalid_ThrowsHubException()
    {
        var (hub, tracker, currentUser, _, groups) = CreateHubFixture();
        currentUser.Id = UserId.New();

        // Empty Guid triggers DomainException from ConversationId constructor
        var ex = await Assert.ThrowsAsync<HubException>(() => hub.JoinConversation(Guid.Empty));
        Assert.NotNull(ex.Message);

        Assert.Empty(groups.Added);
        Assert.Null(tracker.GetActiveConversation("test-connection-1"));
    }

    [Fact]
    public async Task ChatHub_LeaveConversation_RemovesFromGroupAndClearsActiveConversation()
    {
        var (hub, tracker, currentUser, _, groups) = CreateHubFixture();
        currentUser.Id = UserId.New();

        var conversationGuid = Guid.NewGuid();
        tracker.SetActiveConversation("test-connection-1", conversationGuid);

        await hub.LeaveConversation(conversationGuid);

        Assert.Null(tracker.GetActiveConversation("test-connection-1"));
    }

    [Fact]
    public async Task ChatHub_OnConnectedAndDisconnected_TracksAndCleansUpConnection()
    {
        var (hub, tracker, currentUser, _, _) = CreateHubFixture();
        var user = UserId.New();
        currentUser.Id = user;

        await hub.OnConnectedAsync();
        Assert.True(tracker.IsUserOnline(user));
        Assert.Contains("test-connection-1", tracker.GetConnections(user));

        await hub.OnDisconnectedAsync(null);
        Assert.False(tracker.IsUserOnline(user));
        Assert.Empty(tracker.GetConnections(user));
    }

    // =========================================================================
    // 2. Commit-First Broadcast Invariant
    // =========================================================================

    [Fact]
    public async Task SendMessageAsync_PersistsMessageToPersistence_StrictlyBeforeCallingRealtimeNotifier()
    {
        var executionLog = new List<string>();
        var userA = UserId.New();
        var userB = UserId.New();
        var conversation = Conversation.Create(userA, userB, DateTimeOffset.UtcNow, ConversationId.New());

        var mockMessagePersistence = new TrackingMessagePersistence(executionLog);
        var mockConversationPersistence = new StubConversationPersistence(conversation);
        var mockBlockPersistence = new StubBlockPersistence();
        var mockUserReader = new StubUserReader();
        var mockRealtimeNotifier = new TrackingRealtimeNotifier(executionLog);
        var logger = new VerificationTestLogger<MessageService>();

        var messageService = new MessageService(
            mockMessagePersistence,
            mockConversationPersistence,
            mockBlockPersistence,
            mockUserReader,
            mockRealtimeNotifier,
            logger);

        var request = new SendMessageRequestDto("Hello realtime world!", "idempotency-key-001", null);
        var result = await messageService.SendMessageAsync(userA, conversation.Id, request);

        Assert.NotNull(result);
        Assert.Equal("Hello realtime world!", result.Content);

        // Verification of invariant: Persistence MUST occur strictly BEFORE Notifier
        Assert.Equal(2, executionLog.Count);
        Assert.Equal("IMessagePersistence.AddAsync", executionLog[0]);
        Assert.Equal("IRealtimeNotifier.NotifyNewMessageAsync", executionLog[1]);
    }

    [Fact]
    public async Task SendMessageAsync_WhenPersistenceThrows_RealtimeNotifierIsNeverCalled()
    {
        var executionLog = new List<string>();
        var userA = UserId.New();
        var userB = UserId.New();
        var conversation = Conversation.Create(userA, userB, DateTimeOffset.UtcNow, ConversationId.New());

        var mockMessagePersistence = new ThrowingMessagePersistence(new InvalidOperationException("SQL Server deadlocked."));
        var mockConversationPersistence = new StubConversationPersistence(conversation);
        var mockBlockPersistence = new StubBlockPersistence();
        var mockUserReader = new StubUserReader();
        var mockRealtimeNotifier = new TrackingRealtimeNotifier(executionLog);
        var logger = new VerificationTestLogger<MessageService>();

        var messageService = new MessageService(
            mockMessagePersistence,
            mockConversationPersistence,
            mockBlockPersistence,
            mockUserReader,
            mockRealtimeNotifier,
            logger);

        var request = new SendMessageRequestDto("Message that fails db persistence", "idempotency-key-002", null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            messageService.SendMessageAsync(userA, conversation.Id, request));

        // Realtime notifier must NEVER be called when persistence fails
        Assert.Empty(executionLog);
    }

    [Fact]
    public async Task SendMessageAsync_WhenMessageIsIdempotentDuplicate_RealtimeNotifierIsNeverCalled()
    {
        var executionLog = new List<string>();
        var userA = UserId.New();
        var userB = UserId.New();
        var conversation = Conversation.Create(userA, userB, DateTimeOffset.UtcNow, ConversationId.New());

        var mockMessagePersistence = new IdempotentDuplicateMessagePersistence(executionLog);
        var mockConversationPersistence = new StubConversationPersistence(conversation);
        var mockBlockPersistence = new StubBlockPersistence();
        var mockUserReader = new StubUserReader();
        var mockRealtimeNotifier = new TrackingRealtimeNotifier(executionLog);
        var logger = new VerificationTestLogger<MessageService>();

        var messageService = new MessageService(
            mockMessagePersistence,
            mockConversationPersistence,
            mockBlockPersistence,
            mockUserReader,
            mockRealtimeNotifier,
            logger);

        var request = new SendMessageRequestDto("Duplicate message", "idempotency-key-003", null);
        var result = await messageService.SendMessageAsync(userA, conversation.Id, request);

        Assert.NotNull(result);
        Assert.Equal("Duplicate message", result.Content);

        // Verification of invariant: AddAsync is called, but RealtimeNotifier is NEVER called for idempotent duplicates
        Assert.Single(executionLog);
        Assert.Equal("IMessagePersistence.AddAsync", executionLog[0]);
    }

    [Fact]
    public async Task SendMessageAsync_WhenRealtimeNotifierThrows_ReturnsPersistedMessageSuccessfully_AndLogsWarning()
    {
        var userA = UserId.New();
        var userB = UserId.New();
        var conversation = Conversation.Create(userA, userB, DateTimeOffset.UtcNow, ConversationId.New());

        var executionLog = new List<string>();
        var mockMessagePersistence = new TrackingMessagePersistence(executionLog);
        var mockConversationPersistence = new StubConversationPersistence(conversation);
        var mockBlockPersistence = new StubBlockPersistence();
        var mockUserReader = new StubUserReader();
        var throwingNotifier = new ThrowingRealtimeNotifier(new TimeoutException("SignalR socket broadcast timed out."));
        var logger = new VerificationTestLogger<MessageService>();

        var messageService = new MessageService(
            mockMessagePersistence,
            mockConversationPersistence,
            mockBlockPersistence,
            mockUserReader,
            throwingNotifier,
            logger);

        var request = new SendMessageRequestDto("Safe delivery even if broadcast fails", "idempotency-key-003", null);

        // Invariant: Failure in realtime delivery MUST NOT roll back or throw to caller
        var result = await messageService.SendMessageAsync(userA, conversation.Id, request);

        Assert.NotNull(result);
        Assert.Equal("Safe delivery even if broadcast fails", result.Content);
        Assert.Single(executionLog); // Message was persisted!

        // Warning must be logged
        var warning = Assert.Single(logger.Logs, l => l.Level == LogLevel.Warning);
        Assert.Contains("Failed to broadcast realtime message notification", warning.Message);
        Assert.IsType<TimeoutException>(warning.Exception);
    }

    [Fact]
    public async Task SendMessageAsync_PassesExactPersistedMessageDtoToRealtimeNotifier()
    {
        var userA = UserId.New();
        var userB = UserId.New();
        var conversation = Conversation.Create(userA, userB, DateTimeOffset.UtcNow, ConversationId.New());

        var capturedDispatches = new List<(ConversationId ConvId, MessageDto Dto)>();
        var mockMessagePersistence = new TrackingMessagePersistence(new List<string>());
        var mockConversationPersistence = new StubConversationPersistence(conversation);
        var mockBlockPersistence = new StubBlockPersistence();
        var mockUserReader = new StubUserReader();
        var capturingNotifier = new CapturingRealtimeNotifier(capturedDispatches);
        var logger = new VerificationTestLogger<MessageService>();

        var messageService = new MessageService(
            mockMessagePersistence,
            mockConversationPersistence,
            mockBlockPersistence,
            mockUserReader,
            capturingNotifier,
            logger);

        var request = new SendMessageRequestDto("Testing DTO exact matching", "idempotency-key-004", null);
        var result = await messageService.SendMessageAsync(userA, conversation.Id, request);

        var dispatch = Assert.Single(capturedDispatches);
        Assert.Equal(conversation.Id, dispatch.ConvId);
        Assert.Equal(result.Id, dispatch.Dto.Id);
        Assert.Equal(result.ConversationId, dispatch.Dto.ConversationId);
        Assert.Equal(result.SenderId, dispatch.Dto.SenderId);
        Assert.Equal(result.Content, dispatch.Dto.Content);
        Assert.Equal(result.CreatedAtUtc, dispatch.Dto.CreatedAtUtc);
    }

    [Fact]
    public async Task SendMessageAsync_WithRealDatabase_CommitsMessage_AndBroadcastsRealtimeEvent()
    {
        await using var db = _fixture.CreateDbContext();
        var userA = UserId.New();
        var userB = UserId.New();
        var conversation = Conversation.Create(userA, userB, DateTimeOffset.UtcNow, ConversationId.New());
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();

        var messagePersistence = new MessagePersistence(db);
        var convPersistence = new ConversationPersistence(db);
        var blockPersistence = new StubBlockPersistence();
        var userReader = new StubUserReader();

        var dispatches = new List<(ConversationId ConvId, MessageDto Dto)>();
        var notifier = new CapturingRealtimeNotifier(dispatches);
        var logger = new VerificationTestLogger<MessageService>();

        var messageService = new MessageService(
            messagePersistence,
            convPersistence,
            blockPersistence,
            userReader,
            notifier,
            logger);

        var request = new SendMessageRequestDto("Persisted in SQL Server and notified", "idempotency-key-005", null);
        var result = await messageService.SendMessageAsync(userA, conversation.Id, request);

        // Verify message is actually in database
        var dbMessage = await db.Messages.FirstOrDefaultAsync(m => m.Id == result.Id);
        Assert.NotNull(dbMessage);
        Assert.Equal("Persisted in SQL Server and notified", dbMessage.Content);

        // Verify broadcast occurred
        var dispatch = Assert.Single(dispatches);
        Assert.Equal(conversation.Id, dispatch.ConvId);
        Assert.Equal(result.Id, dispatch.Dto.Id);
    }

    // =========================================================================
    // 3. Multi-Connection Tracking (InMemoryConnectionTracker)
    // =========================================================================

    [Fact]
    public void ConnectionTracker_TracksMultipleConcurrentConnections_ForSameUser()
    {
        var tracker = new InMemoryConnectionTracker();
        var user = UserId.New();

        tracker.AddConnection(user, "conn-tab-1");
        tracker.AddConnection(user, "conn-tab-2");
        tracker.AddConnection(user, "conn-mobile-3");

        var connections = tracker.GetConnections(user);
        Assert.Equal(3, connections.Count);
        Assert.Contains("conn-tab-1", connections);
        Assert.Contains("conn-tab-2", connections);
        Assert.Contains("conn-mobile-3", connections);
        Assert.True(tracker.IsUserOnline(user));
    }

    [Fact]
    public void ConnectionTracker_TracksActiveConversationPerConnection_Independently()
    {
        var tracker = new InMemoryConnectionTracker();
        var user = UserId.New();
        var conv1 = Guid.NewGuid();
        var conv2 = Guid.NewGuid();

        tracker.AddConnection(user, "conn-window-1");
        tracker.AddConnection(user, "conn-window-2");

        tracker.SetActiveConversation("conn-window-1", conv1);
        tracker.SetActiveConversation("conn-window-2", conv2);

        Assert.Equal(conv1, tracker.GetActiveConversation("conn-window-1"));
        Assert.Equal(conv2, tracker.GetActiveConversation("conn-window-2"));

        tracker.ClearActiveConversation("conn-window-1");
        Assert.Null(tracker.GetActiveConversation("conn-window-1"));
        Assert.Equal(conv2, tracker.GetActiveConversation("conn-window-2"));
    }

    [Fact]
    public void ConnectionTracker_RemoveConnection_DecrementsCount_AndCleansUpWhenAllConnectionsClosed()
    {
        var tracker = new InMemoryConnectionTracker();
        var user = UserId.New();
        var convId = Guid.NewGuid();

        tracker.AddConnection(user, "conn-a");
        tracker.AddConnection(user, "conn-b");
        tracker.SetActiveConversation("conn-a", convId);

        // Remove first connection
        tracker.RemoveConnection(user, "conn-a");
        Assert.True(tracker.IsUserOnline(user));
        Assert.Single(tracker.GetConnections(user));
        Assert.Equal("conn-b", tracker.GetConnections(user).First());
        Assert.Null(tracker.GetActiveConversation("conn-a")); // active conversation cleaned up on connection removal

        // Remove second connection
        tracker.RemoveConnection(user, "conn-b");
        Assert.False(tracker.IsUserOnline(user));
        Assert.Empty(tracker.GetConnections(user));
        Assert.DoesNotContain(user, tracker.GetOnlineUsers());
    }

    [Fact]
    public void ConnectionTracker_TracksMultipleDistinctUsers_Accurately()
    {
        var tracker = new InMemoryConnectionTracker();
        var user1 = UserId.New();
        var user2 = UserId.New();
        var user3 = UserId.New();

        tracker.AddConnection(user1, "u1-conn");
        tracker.AddConnection(user2, "u2-conn");

        var onlineUsers = tracker.GetOnlineUsers();
        Assert.Equal(2, onlineUsers.Count);
        Assert.Contains(user1, onlineUsers);
        Assert.Contains(user2, onlineUsers);
        Assert.DoesNotContain(user3, onlineUsers);

        Assert.True(tracker.IsUserOnline(user1));
        Assert.True(tracker.IsUserOnline(user2));
        Assert.False(tracker.IsUserOnline(user3));
    }

    [Fact]
    public async Task ConnectionTracker_ConcurrentOperations_AreThreadSafe()
    {
        var tracker = new InMemoryConnectionTracker();
        var users = Enumerable.Range(0, 20).Select(_ => UserId.New()).ToList();

        // Concurrently add 10 connections per user across multiple threads
        await Task.WhenAll(users.Select(user => Task.Run(() =>
        {
            for (var i = 0; i < 10; i++)
            {
                var connId = $"{user.Value}_{i}";
                tracker.AddConnection(user, connId);
                tracker.SetActiveConversation(connId, Guid.NewGuid());
            }
        })));

        foreach (var user in users)
        {
            Assert.True(tracker.IsUserOnline(user));
            Assert.Equal(10, tracker.GetConnections(user).Count);
        }

        Assert.Equal(20, tracker.GetOnlineUsers().Count);

        // Concurrently remove half the connections per user
        await Task.WhenAll(users.Select(user => Task.Run(() =>
        {
            for (var i = 0; i < 5; i++)
            {
                var connId = $"{user.Value}_{i}";
                tracker.RemoveConnection(user, connId);
            }
        })));

        foreach (var user in users)
        {
            Assert.True(tracker.IsUserOnline(user));
            Assert.Equal(5, tracker.GetConnections(user).Count);
        }

        // Concurrently remove remaining connections
        await Task.WhenAll(users.Select(user => Task.Run(() =>
        {
            for (var i = 5; i < 10; i++)
            {
                var connId = $"{user.Value}_{i}";
                tracker.RemoveConnection(user, connId);
            }
        })));

        foreach (var user in users)
        {
            Assert.False(tracker.IsUserOnline(user));
            Assert.Empty(tracker.GetConnections(user));
        }

        Assert.Empty(tracker.GetOnlineUsers());
    }

    [Fact]
    public void ConnectionTracker_ArgumentValidation_ThrowsExpectedExceptions()
    {
        var tracker = new InMemoryConnectionTracker();
        var validUser = UserId.New();

        Assert.Throws<ArgumentNullException>(() => tracker.AddConnection(null!, "conn-1"));
        Assert.Throws<ArgumentException>(() => tracker.AddConnection(validUser, ""));
        Assert.Throws<ArgumentException>(() => tracker.AddConnection(validUser, "   "));

        Assert.Throws<ArgumentNullException>(() => tracker.RemoveConnection(null!, "conn-1"));
        Assert.Throws<ArgumentException>(() => tracker.RemoveConnection(validUser, ""));
        Assert.Throws<ArgumentException>(() => tracker.RemoveConnection(validUser, "   "));

        Assert.Throws<ArgumentException>(() => tracker.SetActiveConversation("", Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => tracker.ClearActiveConversation(""));
        Assert.Throws<ArgumentException>(() => tracker.GetActiveConversation(""));
        Assert.Throws<ArgumentNullException>(() => tracker.GetConnections(null!));
        Assert.Throws<ArgumentNullException>(() => tracker.IsUserOnline(null!));
    }

    // =========================================================================
    // 4. Application Layer Purity
    // =========================================================================

    [Fact]
    public void ApplicationAssembly_HasZeroReferencesToSignalRTypes_OrPackages()
    {
        var appAssembly = typeof(IRealtimeNotifier).Assembly;
        var referencedAssemblies = appAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("SignalR", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Contains("AspNetCore.SignalR", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IRealtimeNotifier_ResidesInApplication_WithZeroSignalRDependencies()
    {
        var notifierType = typeof(IRealtimeNotifier);

        Assert.Equal("AOS.Messenger.Application.Common.Interfaces", notifierType.Namespace);

        var method = notifierType.GetMethod(nameof(IRealtimeNotifier.NotifyNewMessageAsync));
        Assert.NotNull(method);

        // Parameters must be ConversationId, MessageDto, CancellationToken
        var parameters = method.GetParameters();
        Assert.Equal(3, parameters.Length);
        Assert.Equal(typeof(ConversationId), parameters[0].ParameterType);
        Assert.Equal(typeof(MessageDto), parameters[1].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[2].ParameterType);

        // Return type must be Task
        Assert.Equal(typeof(Task), method.ReturnType);
    }

    [Fact]
    public void ApplicationAssembly_Types_HaveZeroSignalRDependenciesInMethodsOrProperties()
    {
        var appAssembly = typeof(IRealtimeNotifier).Assembly;

        foreach (var type in appAssembly.GetTypes())
        {
            var typeFullName = type.FullName ?? type.Name;
            Assert.DoesNotContain("SignalR", typeFullName, StringComparison.OrdinalIgnoreCase);

            foreach (var iface in type.GetInterfaces())
            {
                Assert.DoesNotContain("SignalR", iface.FullName ?? iface.Name, StringComparison.OrdinalIgnoreCase);
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            {
                Assert.DoesNotContain("SignalR", method.ReturnType.FullName ?? method.ReturnType.Name, StringComparison.OrdinalIgnoreCase);
                foreach (var param in method.GetParameters())
                {
                    Assert.DoesNotContain("SignalR", param.ParameterType.FullName ?? param.ParameterType.Name, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }

    // =========================================================================
    // 5. Repository Directives Invariant (No #region anywhere)
    // =========================================================================

    [Fact]
    public void EntireRepository_ContainsZeroRegionDirectives_InAnyCSharpFile()
    {
        var currentDir = Directory.GetCurrentDirectory();
        while (currentDir != null && !File.Exists(Path.Combine(currentDir, "AGENTS.md")))
        {
            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        Assert.NotNull(currentDir);

        var csharpFiles = Directory.GetFiles(currentDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) &&
                        !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .ToList();

        Assert.NotEmpty(csharpFiles);

        var filesWithRegions = new List<string>();

        foreach (var file in csharpFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed.StartsWith("#region", StringComparison.Ordinal) ||
                    trimmed.StartsWith("#endregion", StringComparison.Ordinal))
                {
                    filesWithRegions.Add($"{Path.GetFileName(file)}: line {i + 1} ({trimmed})");
                }
            }
        }

        Assert.True(
            filesWithRegions.Count == 0,
            $"Found forbidden #region / #endregion directives in:{Environment.NewLine}{string.Join(Environment.NewLine, filesWithRegions)}");
    }

    // =========================================================================
    // Test Helpers and Test Doubles
    // =========================================================================

    private static (ChatHub Hub, InMemoryConnectionTracker Tracker, FakeCurrentUser CurrentUser, FakeConversationService ConvService, VerificationGroupManager Groups) CreateHubFixture()
    {
        var tracker = new InMemoryConnectionTracker();
        var currentUser = new FakeCurrentUser();
        var convService = new FakeConversationService();
        var logger = new VerificationTestLogger<ChatHub>();
        var hub = new ChatHub(convService, tracker, currentUser, logger);

        var callerContext = new VerificationHubCallerContext("test-connection-1");
        var groups = new VerificationGroupManager();
        hub.Context = callerContext;
        hub.Groups = groups;

        return (hub, tracker, currentUser, convService, groups);
    }

    private static ConversationDetailDto CreateConversationDetail(ConversationId id, UserId currentUserId)
    {
        return new ConversationDetailDto(
            id,
            UserId.New(),
            "Partner",
            "partner_handle",
            null,
            false,
            false,
            false,
            false,
            DateTimeOffset.UtcNow);
    }

    private sealed class VerificationHubCallerContext : HubCallerContext
    {
        public VerificationHubCallerContext(string connectionId, ClaimsPrincipal? user = null)
        {
            ConnectionId = connectionId;
            User = user;
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() { }
    }

    private sealed class VerificationGroupManager : IGroupManager
    {
        public List<(string ConnectionId, string GroupName)> Added { get; } = new();
        public List<(string ConnectionId, string GroupName)> Removed { get; } = new();

        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Added.Add((connectionId, groupName));
            return Task.CompletedTask;
        }

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
        {
            Removed.Add((connectionId, groupName));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public string? UserName { get; set; }
        public bool IsAuthenticated => Id is not null;
    }

    private sealed class FakeConversationService : IConversationService
    {
        public Func<UserId, ConversationId, CancellationToken, Task<ConversationDetailDto>>? OnGetConversationAsync { get; set; }

        public Task<ConversationDetailDto> GetConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default)
        {
            if (OnGetConversationAsync is not null)
            {
                return OnGetConversationAsync(currentUserId, conversationId, ct);
            }

            return Task.FromResult(new ConversationDetailDto(
                conversationId,
                UserId.New(),
                "Other",
                "other_user",
                null,
                false,
                false,
                false,
                false,
                DateTimeOffset.UtcNow));
        }

        public Task<ConversationDetailDto> GetOrCreateConversationAsync(UserId currentUserId, UserId targetUserId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<ConversationSummaryDto>> GetConversationListAsync(UserId currentUserId, bool includeArchived = false, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> PinConversationAsync(UserId currentUserId, ConversationId conversationId, bool isPinned, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> ArchiveConversationAsync(UserId currentUserId, ConversationId conversationId, bool isArchived, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> MuteConversationAsync(UserId currentUserId, ConversationId conversationId, bool isMuted, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> ClearChatAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class TrackingMessagePersistence : IMessagePersistence
    {
        private readonly List<string> _executionLog;

        public TrackingMessagePersistence(List<string> executionLog)
        {
            _executionLog = executionLog;
        }

        public Task<PersistMessageResult> AddAsync(Message message, CancellationToken ct = default)
        {
            _executionLog.Add("IMessagePersistence.AddAsync");
            return Task.FromResult(new PersistMessageResult(message, IsNewlyCreated: true));
        }

        public Task<Message?> GetByIdAsync(ConversationId conversationId, MessageId id, CancellationToken ct = default) => Task.FromResult<Message?>(null);
        public Task<IReadOnlyDictionary<MessageId, Message>> GetByIdsAsync(ConversationId conversationId, IEnumerable<MessageId> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<MessageId, Message>>(new Dictionary<MessageId, Message>());
        public Task<Message?> GetByIdempotencyKeyAsync(UserId senderId, string idempotencyKey, CancellationToken ct = default) => Task.FromResult<Message?>(null);
        public Task UpdateAsync(Message message, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Message>> GetKeysetMessagesAsync(ConversationId conversationId, DateTimeOffset? cursorCreatedAtUtc, MessageId? cursorId, DateTimeOffset? clearedAtUtc, int pageSize = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
        public Task<IReadOnlyList<Message>> GetKeysetMessagesAsync(ConversationId conversationId, UserId callerId, DateTimeOffset? cursorCreatedAtUtc, MessageId? cursorId, DateTimeOffset? clearedAtUtc, int pageSize = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
        public Task DeleteForMeAsync(ConversationId conversationId, MessageId messageId, UserId callerId, DateTimeOffset deletedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsMessageDeletedForUserAsync(ConversationId conversationId, MessageId messageId, UserId callerId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<HashSet<MessageId>> GetDeletedMessageIdsForUserAsync(ConversationId conversationId, UserId callerId, IEnumerable<MessageId> messageIds, CancellationToken ct = default) => Task.FromResult<HashSet<MessageId>>(new HashSet<MessageId>());
        public Task DeleteForEveryoneTransactionalAsync(ConversationId conversationId, MessageId messageId, DateTimeOffset deletedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingMessagePersistence : IMessagePersistence
    {
        private readonly Exception _exception;

        public ThrowingMessagePersistence(Exception exception)
        {
            _exception = exception;
        }

        public Task<PersistMessageResult> AddAsync(Message message, CancellationToken ct = default) => throw _exception;
        public Task<Message?> GetByIdAsync(ConversationId conversationId, MessageId id, CancellationToken ct = default) => Task.FromResult<Message?>(null);
        public Task<IReadOnlyDictionary<MessageId, Message>> GetByIdsAsync(ConversationId conversationId, IEnumerable<MessageId> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<MessageId, Message>>(new Dictionary<MessageId, Message>());
        public Task<Message?> GetByIdempotencyKeyAsync(UserId senderId, string idempotencyKey, CancellationToken ct = default) => Task.FromResult<Message?>(null);
        public Task UpdateAsync(Message message, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Message>> GetKeysetMessagesAsync(ConversationId conversationId, DateTimeOffset? cursorCreatedAtUtc, MessageId? cursorId, DateTimeOffset? clearedAtUtc, int pageSize = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
        public Task<IReadOnlyList<Message>> GetKeysetMessagesAsync(ConversationId conversationId, UserId callerId, DateTimeOffset? cursorCreatedAtUtc, MessageId? cursorId, DateTimeOffset? clearedAtUtc, int pageSize = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
        public Task DeleteForMeAsync(ConversationId conversationId, MessageId messageId, UserId callerId, DateTimeOffset deletedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsMessageDeletedForUserAsync(ConversationId conversationId, MessageId messageId, UserId callerId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<HashSet<MessageId>> GetDeletedMessageIdsForUserAsync(ConversationId conversationId, UserId callerId, IEnumerable<MessageId> messageIds, CancellationToken ct = default) => Task.FromResult<HashSet<MessageId>>(new HashSet<MessageId>());
        public Task DeleteForEveryoneTransactionalAsync(ConversationId conversationId, MessageId messageId, DateTimeOffset deletedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class IdempotentDuplicateMessagePersistence : IMessagePersistence
    {
        private readonly List<string> _executionLog;

        public IdempotentDuplicateMessagePersistence(List<string> executionLog)
        {
            _executionLog = executionLog;
        }

        public Task<PersistMessageResult> AddAsync(Message message, CancellationToken ct = default)
        {
            _executionLog.Add("IMessagePersistence.AddAsync");
            return Task.FromResult(new PersistMessageResult(message, IsNewlyCreated: false));
        }

        public Task<Message?> GetByIdAsync(ConversationId conversationId, MessageId id, CancellationToken ct = default) => Task.FromResult<Message?>(null);
        public Task<IReadOnlyDictionary<MessageId, Message>> GetByIdsAsync(ConversationId conversationId, IEnumerable<MessageId> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<MessageId, Message>>(new Dictionary<MessageId, Message>());
        public Task<Message?> GetByIdempotencyKeyAsync(UserId senderId, string idempotencyKey, CancellationToken ct = default) => Task.FromResult<Message?>(null);
        public Task UpdateAsync(Message message, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Message>> GetKeysetMessagesAsync(ConversationId conversationId, DateTimeOffset? cursorCreatedAtUtc, MessageId? cursorId, DateTimeOffset? clearedAtUtc, int pageSize = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
        public Task<IReadOnlyList<Message>> GetKeysetMessagesAsync(ConversationId conversationId, UserId callerId, DateTimeOffset? cursorCreatedAtUtc, MessageId? cursorId, DateTimeOffset? clearedAtUtc, int pageSize = 50, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>());
        public Task DeleteForMeAsync(ConversationId conversationId, MessageId messageId, UserId callerId, DateTimeOffset deletedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsMessageDeletedForUserAsync(ConversationId conversationId, MessageId messageId, UserId callerId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<HashSet<MessageId>> GetDeletedMessageIdsForUserAsync(ConversationId conversationId, UserId callerId, IEnumerable<MessageId> messageIds, CancellationToken ct = default) => Task.FromResult<HashSet<MessageId>>(new HashSet<MessageId>());
        public Task DeleteForEveryoneTransactionalAsync(ConversationId conversationId, MessageId messageId, DateTimeOffset deletedAtUtc, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TrackingRealtimeNotifier : IRealtimeNotifier
    {
        private readonly List<string> _executionLog;

        public TrackingRealtimeNotifier(List<string> executionLog)
        {
            _executionLog = executionLog;
        }

        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default)
        {
            _executionLog.Add("IRealtimeNotifier.NotifyNewMessageAsync");
            return Task.CompletedTask;
        }

        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingRealtimeNotifier : IRealtimeNotifier
    {
        private readonly Exception _exception;

        public ThrowingRealtimeNotifier(Exception exception)
        {
            _exception = exception;
        }

        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) =>
            throw _exception;

        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) =>
            throw _exception;

        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) =>
            throw _exception;

        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) =>
            throw _exception;

        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) =>
            throw _exception;

        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) =>
            throw _exception;

        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) =>
            throw _exception;

        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) =>
            throw _exception;
    }

    private sealed class CapturingRealtimeNotifier : IRealtimeNotifier
    {
        private readonly List<(ConversationId, MessageDto)> _captured;

        public CapturingRealtimeNotifier(List<(ConversationId, MessageDto)> captured)
        {
            _captured = captured;
        }

        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default)
        {
            _captured.Add((conversationId, message));
            return Task.CompletedTask;
        }

        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubConversationPersistence : IConversationPersistence
    {
        private readonly Conversation _conversation;

        public StubConversationPersistence(Conversation conversation)
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
    }

    private sealed class StubBlockPersistence : IBlockPersistence
    {
        public Task<bool> IsBlockedAsync(UserId userA, UserId userB, CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> IsBlockedBidirectionalAsync(UserId userA, UserId userB, CancellationToken ct = default) => Task.FromResult(false);
        public Task<UserBlock?> GetBlockAsync(UserId blockerId, UserId blockedUserId, CancellationToken ct = default) => Task.FromResult<UserBlock?>(null);
        public Task AddBlockAsync(UserBlock block, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveBlockAsync(UserId blockerId, UserId blockedUserId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<UserId>> GetBlockedUserIdsAsync(UserId blockerId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UserId>>(Array.Empty<UserId>());
        public Task<IReadOnlyList<UserBlock>> GetBlocksByBlockerIdAsync(UserId blockerId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<UserBlock>>(Array.Empty<UserBlock>());
        public Task<IReadOnlySet<UserId>> GetBidirectionalBlockedUserIdsAsync(UserId userId, CancellationToken ct = default) => Task.FromResult<IReadOnlySet<UserId>>(new HashSet<UserId>());
    }

    private sealed class StubUserReader : IUserReader
    {
        public Task<UserProfileDto?> GetByIdAsync(UserId userId, CancellationToken ct = default) =>
            Task.FromResult<UserProfileDto?>(new UserProfileDto(userId, "testuser", "Test User", "Bio", false));

        public Task<IReadOnlyList<UserProfileDto>> GetByIdsAsync(IEnumerable<UserId> userIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserProfileDto>>(Array.Empty<UserProfileDto>());

        public Task<UserProfileDto?> GetByUserNameAsync(string userName, CancellationToken ct = default) =>
            Task.FromResult<UserProfileDto?>(null);

        public Task<UserProfileDto?> GetByEmailAsync(string email, CancellationToken ct = default) =>
            Task.FromResult<UserProfileDto?>(null);

        public Task<bool> ExistsAsync(UserId userId, CancellationToken ct = default) => Task.FromResult(true);

        public Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(
            UserId currentUserId,
            string query,
            IReadOnlySet<UserId> excludedUserIds,
            int limit,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserSearchResultDto>>(Array.Empty<UserSearchResultDto>());
    }

    private sealed class VerificationTestLogger<T> : ILogger<T>
    {
        public sealed record LogRecord(LogLevel Level, string Message, Exception? Exception);

        public List<LogRecord> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add(new LogRecord(logLevel, formatter(state, exception), exception));
        }
    }
}
