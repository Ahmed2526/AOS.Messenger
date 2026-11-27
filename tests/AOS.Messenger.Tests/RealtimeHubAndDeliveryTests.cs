using System.Security.Claims;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Tests.TestDoubles;
using AOS.Messenger.Web.Hubs;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AOS.Messenger.Tests;

/// <summary>
/// Unit and integration tests for Phase 6 deliverables:
/// 1. Thread-safe in-memory connection tracker (multi-device, presence, conversation focus)
/// 2. Thin [Authorize] SignalR ChatHub (lifecycle, join with participant check, leave, exceptions)
/// 3. SignalR Realtime Notifier (group broadcast)
/// 4. Commit-First Invariant & Graceful Broadcast Failure in MessageService
/// </summary>
public sealed class RealtimeHubAndDeliveryTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public RealtimeHubAndDeliveryTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }
    // =========================================================================
    // 1. InMemoryConnectionTracker Tests
    // =========================================================================

    [Fact]
    public void ConnectionTracker_AddAndGetConnections_SupportsMultipleConnectionsPerUser()
    {
        var tracker = new InMemoryConnectionTracker();
        var user = UserId.New();

        tracker.AddConnection(user, "conn-1");
        tracker.AddConnection(user, "conn-2");

        var connections = tracker.GetConnections(user);
        Assert.Equal(2, connections.Count);
        Assert.Contains("conn-1", connections);
        Assert.Contains("conn-2", connections);
        Assert.True(tracker.IsUserOnline(user));
    }

    [Fact]
    public void ConnectionTracker_RemoveConnection_UpdatesState_AndCleansUpWhenEmpty()
    {
        var tracker = new InMemoryConnectionTracker();
        var user = UserId.New();

        tracker.AddConnection(user, "conn-1");
        tracker.AddConnection(user, "conn-2");
        tracker.SetActiveConversation("conn-1", Guid.NewGuid());

        tracker.RemoveConnection(user, "conn-1");

        var remaining = tracker.GetConnections(user);
        Assert.Single(remaining);
        Assert.Contains("conn-2", remaining);
        Assert.True(tracker.IsUserOnline(user));
        Assert.Null(tracker.GetActiveConversation("conn-1"));

        // Remove final connection
        tracker.RemoveConnection(user, "conn-2");
        Assert.Empty(tracker.GetConnections(user));
        Assert.False(tracker.IsUserOnline(user));
        Assert.DoesNotContain(user, tracker.GetOnlineUsers());
    }

    [Fact]
    public void ConnectionTracker_ActiveConversation_CanBeSetAndCleared()
    {
        var tracker = new InMemoryConnectionTracker();
        var user = UserId.New();
        var convId = Guid.NewGuid();

        // Unknown/disconnected connections must be ignored so stale writes cannot reinsert entries.
        tracker.SetActiveConversation("unregistered-conn", convId);
        Assert.Null(tracker.GetActiveConversation("unregistered-conn"));

        tracker.AddConnection(user, "conn-focus-1");
        tracker.SetActiveConversation("conn-focus-1", convId);
        Assert.Equal(convId, tracker.GetActiveConversation("conn-focus-1"));

        tracker.ClearActiveConversation("conn-focus-1");
        Assert.Null(tracker.GetActiveConversation("conn-focus-1"));
    }

    [Fact]
    public void ConnectionTracker_GetOnlineUsers_ReturnsDistinctOnlineUsers()
    {
        var tracker = new InMemoryConnectionTracker();
        var user1 = UserId.New();
        var user2 = UserId.New();
        var user3 = UserId.New();

        tracker.AddConnection(user1, "u1-c1");
        tracker.AddConnection(user1, "u1-c2");
        tracker.AddConnection(user2, "u2-c1");

        var onlineUsers = tracker.GetOnlineUsers();
        Assert.Equal(2, onlineUsers.Count);
        Assert.Contains(user1, onlineUsers);
        Assert.Contains(user2, onlineUsers);
        Assert.DoesNotContain(user3, onlineUsers);
    }

    [Fact]
    public async Task ConnectionTracker_IsThreadSafe_UnderConcurrentAccess()
    {
        var tracker = new InMemoryConnectionTracker();
        var user = UserId.New();
        const int iterations = 1000;

        var tasks = Enumerable.Range(0, iterations).Select(i => Task.Run(() =>
        {
            var connId = $"concurrent-conn-{i}";
            tracker.AddConnection(user, connId);
            tracker.SetActiveConversation(connId, Guid.NewGuid());
            _ = tracker.GetConnections(user);
            _ = tracker.IsUserOnline(user);
            tracker.RemoveConnection(user, connId);
        })).ToArray();

        await Task.WhenAll(tasks);

        Assert.Empty(tracker.GetConnections(user));
        Assert.False(tracker.IsUserOnline(user));
    }

    // =========================================================================
    // 2. ChatHub Tests
    // =========================================================================

    [Fact]
    public async Task ChatHub_OnConnectedAsync_RegistersUserConnection()
    {
        var (hub, tracker, currentUser, _, _) = CreateHubContext();
        var user = UserId.New();
        currentUser.Id = user;

        await hub.OnConnectedAsync();

        Assert.Contains("test-conn-id", tracker.GetConnections(user));
        Assert.True(tracker.IsUserOnline(user));
    }

    [Fact]
    public async Task ChatHub_OnDisconnectedAsync_UnregistersUserConnection()
    {
        var (hub, tracker, currentUser, _, _) = CreateHubContext();
        var user = UserId.New();
        currentUser.Id = user;

        tracker.AddConnection(user, "test-conn-id");
        Assert.True(tracker.IsUserOnline(user));

        await hub.OnDisconnectedAsync(null);

        Assert.Empty(tracker.GetConnections(user));
        Assert.False(tracker.IsUserOnline(user));
    }

    [Fact]
    public async Task ChatHub_JoinConversation_WhenParticipant_AddsToGroupAndSetsActive()
    {
        var (hub, tracker, currentUser, convService, groups) = CreateHubContext();
        var user = UserId.New();
        var convId = Guid.NewGuid();
        currentUser.Id = user;
        tracker.AddConnection(user, "test-conn-id");

        convService.AllowedConversations.Add(new ConversationId(convId));

        await hub.JoinConversation(convId);

        Assert.Equal(convId, tracker.GetActiveConversation("test-conn-id"));
    }

    [Fact]
    public async Task ChatHub_JoinConversation_WhenUnauthenticated_ThrowsHubException()
    {
        var (hub, _, currentUser, _, _) = CreateHubContext();
        currentUser.Id = null;

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.JoinConversation(Guid.NewGuid()));
        Assert.Contains("authenticated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChatHub_JoinConversation_WhenNonParticipant_ThrowsHubException()
    {
        var (hub, tracker, currentUser, _, groups) = CreateHubContext();
        var user = UserId.New();
        var convId = Guid.NewGuid();
        currentUser.Id = user;

        // convService will throw ForbiddenException for this conversation
        var ex = await Assert.ThrowsAsync<HubException>(() => hub.JoinConversation(convId));
        Assert.Contains("participant", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(groups.Added);
        Assert.Null(tracker.GetActiveConversation("test-conn-id"));
    }

    [Fact]
    public async Task ChatHub_JoinConversation_WhenConversationNotFound_ThrowsHubException()
    {
        var (hub, _, currentUser, convService, _) = CreateHubContext();
        var user = UserId.New();
        var convId = Guid.NewGuid();
        currentUser.Id = user;

        convService.NotFoundConversations.Add(new ConversationId(convId));

        var ex = await Assert.ThrowsAsync<HubException>(() => hub.JoinConversation(convId));
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChatHub_LeaveConversation_RemovesFromGroupAndClearsActive()
    {
        var (hub, tracker, currentUser, _, groups) = CreateHubContext();
        var user = UserId.New();
        var convId = Guid.NewGuid();
        currentUser.Id = user;

        tracker.SetActiveConversation("test-conn-id", convId);

        await hub.LeaveConversation(convId);

        Assert.Null(tracker.GetActiveConversation("test-conn-id"));
    }

    // =========================================================================
    // 3. SignalRRealtimeNotifier Tests
    // =========================================================================

    [Fact]
    public async Task SignalRRealtimeNotifier_BroadcastsMessageToParticipants()
    {
        var db = CreateDbContext();
        var convPersistence = new ConversationPersistence(db);
        var blockPersistence = new BlockPersistence(db);

        var userA = UserId.New();
        var userB = UserId.New();
        var conv = Conversation.Create(userA, userB, DateTimeOffset.UtcNow);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        var hubContext = new TestHubContext();
        var logger = new TestLogger<SignalRRealtimeNotifier>();
        var notifier = new SignalRRealtimeNotifier(hubContext, convPersistence, blockPersistence, logger);

        var msgDto = new MessageDto(
            MessageId.New(),
            conv.Id,
            userA,
            "Alice",
            "alice",
            "Hello Realtime!",
            DateTimeOffset.UtcNow,
            null,
            false,
            null,
            true);

        await notifier.NotifyNewMessageAsync(conv.Id, msgDto);

        Assert.Single(hubContext.HubClients.TargetedUserIds);
        var targeted = hubContext.HubClients.TargetedUserIds[0];
        Assert.Contains(userA.Value.ToString(), targeted);
        Assert.Contains(userB.Value.ToString(), targeted);
        Assert.Single(hubContext.HubClients.UsersClient.ReceivedMessages);
        Assert.Equal(msgDto.Id, hubContext.HubClients.UsersClient.ReceivedMessages[0].Id);
        Assert.Equal("Hello Realtime!", hubContext.HubClients.UsersClient.ReceivedMessages[0].Content);
    }

    // =========================================================================
    // 4. Commit-First Broadcast Invariant & Failure Resilience in MessageService
    // =========================================================================

    [Fact]
    public async Task MessageService_SendMessageAsync_BroadcastsStrictlyAfterPersistence()
    {
        var db = CreateDbContext();
        var messagePersistence = new MessagePersistence(db);
        var conversationPersistence = new ConversationPersistence(db);
        var blockPersistence = new BlockPersistence(db);
        var userReader = new FakeUserReader();
        var realtimeNotifier = new FakeRealtimeNotifier();
        var logger = new TestLogger<MessageService>();

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
            realtimeNotifier,
            logger,
            NullMediaPersistence.Instance);

        var req = new SendMessageRequestDto("Persistent and realtime", "broadcast-key-1");
        var result = await service.SendMessageAsync(userA, conv.Id, req);

        // Verify persisted to DB first
        var persistedInDb = await db.Messages.FirstOrDefaultAsync(m => m.Id == result.Id);
        Assert.NotNull(persistedInDb);
        Assert.Equal("Persistent and realtime", persistedInDb.Content);

        // Verify broadcast occurred
        Assert.Single(realtimeNotifier.Broadcasts);
        var broadcast = realtimeNotifier.Broadcasts[0];
        Assert.Equal(conv.Id, broadcast.ConversationId);
        Assert.Equal(result.Id, broadcast.Message.Id);
        Assert.Equal("Persistent and realtime", broadcast.Message.Content);
    }

    [Fact]
    public async Task MessageService_SendMessageAsync_WhenBroadcastFails_LogsWarningAndStillSucceeds()
    {
        var db = CreateDbContext();
        var messagePersistence = new MessagePersistence(db);
        var conversationPersistence = new ConversationPersistence(db);
        var blockPersistence = new BlockPersistence(db);
        var userReader = new FakeUserReader();
        var realtimeNotifier = new FakeRealtimeNotifier
        {
            ExceptionToThrow = new InvalidOperationException("SignalR connection transport dropped!")
        };
        var logger = new TestLogger<MessageService>();

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
            realtimeNotifier,
            logger,
            NullMediaPersistence.Instance);

        var req = new SendMessageRequestDto("Should survive broadcast failure", "resilient-key-1");
        var result = await service.SendMessageAsync(userA, conv.Id, req);

        // Send must NOT fail
        Assert.NotNull(result);
        Assert.Equal("Should survive broadcast failure", result.Content);

        // Database remains authoritative and persisted
        var inDb = await db.Messages.FirstOrDefaultAsync(m => m.Id == result.Id);
        Assert.NotNull(inDb);

        // Warning must be logged
        var warning = Assert.Single(logger.Logs, l => l.Level == LogLevel.Warning);
        Assert.Contains("Failed to broadcast realtime message notification", warning.Message);
        Assert.NotNull(warning.Exception);
    }

    // =========================================================================
    // Test Doubles & Helpers
    // =========================================================================

    private static (ChatHub Hub, InMemoryConnectionTracker Tracker, FakeCurrentUser CurrentUser, FakeConversationService ConvService, TestGroupManager Groups) CreateHubContext()
    {
        var tracker = new InMemoryConnectionTracker();
        var currentUser = new FakeCurrentUser();
        var convService = new FakeConversationService();
        var logger = new TestLogger<ChatHub>();
        var hub = new ChatHub(convService, tracker, currentUser, logger);

        var callerContext = new TestHubCallerContext("test-conn-id");
        var groups = new TestGroupManager();
        hub.Context = callerContext;
        hub.Groups = groups;

        return (hub, tracker, currentUser, convService, groups);
    }

    private ApplicationDbContext CreateDbContext()
    {
        return _fixture.CreateDbContext();
    }

    private sealed class FakeCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated => Id is not null;
    }

    private sealed class FakeConversationService : IConversationService
    {
        public HashSet<ConversationId> AllowedConversations { get; } = new();
        public HashSet<ConversationId> NotFoundConversations { get; } = new();

        public Task<ConversationDetailDto> GetConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default)
        {
            if (NotFoundConversations.Contains(conversationId))
            {
                throw new NotFoundException("Conversation", conversationId.Value);
            }

            if (!AllowedConversations.Contains(conversationId))
            {
                throw new ForbiddenException("User is not a participant in this conversation.");
            }

            return Task.FromResult(new ConversationDetailDto(
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
        }

        public Task<ConversationDetailDto> GetOrCreateConversationAsync(UserId currentUserId, UserId targetUserId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ConversationSummaryDto>> GetConversationListAsync(UserId currentUserId, bool includeArchived = false, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> PinConversationAsync(UserId currentUserId, ConversationId conversationId, bool isPinned, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ArchiveConversationAsync(UserId currentUserId, ConversationId conversationId, bool isArchived, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> MuteConversationAsync(UserId currentUserId, ConversationId conversationId, bool isMuted, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> ClearChatAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> DeleteConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeUserReader : IUserReader
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

        public Task<UserProfileDto?> GetByEmailAsync(string email, CancellationToken ct = default)
        {
            return Task.FromResult<UserProfileDto?>(null);
        }

        public Task<bool> ExistsAsync(UserId id, CancellationToken ct = default) =>
            Task.FromResult(_profiles.ContainsKey(id));

        public Task<IReadOnlySet<UserId>> GetActiveUserIdsAsync(IEnumerable<UserId> userIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<UserId>>(userIds.Where(_profiles.ContainsKey).ToHashSet());

        public Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(
            UserId currentUserId,
            string query,
            IReadOnlySet<UserId> excludedUserIds,
            int limit,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserSearchResultDto>>([]);
    }

    private sealed class FakeRealtimeNotifier : IRealtimeNotifier
    {
        public List<(ConversationId ConversationId, MessageDto Message)> Broadcasts { get; } = new();
        public Exception? ExceptionToThrow { get; set; }

        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

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

    private sealed class TestHubCallerContext : HubCallerContext
    {
        public TestHubCallerContext(string connectionId, ClaimsPrincipal? user = null)
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

    private sealed class TestGroupManager : IGroupManager
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

    private sealed class TestChatClient : IChatClient
    {
        public List<MessageDto> ReceivedMessages { get; } = new();

        public Task ReceiveMessage(MessageDto message)
        {
            ReceivedMessages.Add(message);
            return Task.CompletedTask;
        }

        public Task ReceiveReceiptUpdated(ReceiptUpdateDto receipt) => Task.CompletedTask;
        public Task ReceiveTypingIndicator(TypingIndicatorDto typing) => Task.CompletedTask;
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
        public IGroupManager Groups { get; } = new TestGroupManager();
    }

    private sealed class TestHubClients : IHubClients<IChatClient>
    {
        public Dictionary<string, TestChatClient> GroupClients { get; } = new();
        public TestChatClient AllClient { get; } = new();
        public TestChatClient UsersClient { get; } = new();
        public List<IReadOnlyList<string>> TargetedUserIds { get; } = new();

        public IChatClient All => AllClient;
        public IChatClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => AllClient;
        public IChatClient Client(string connectionId) => AllClient;
        public IChatClient Clients(IReadOnlyList<string> connectionIds) => AllClient;
        public IChatClient Group(string groupName)
        {
            if (!GroupClients.TryGetValue(groupName, out var client))
            {
                client = new TestChatClient();
                GroupClients[groupName] = client;
            }
            return client;
        }
        public IChatClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => Group(groupName);
        public IChatClient Groups(IReadOnlyList<string> groupNames) => AllClient;
        public IChatClient User(string userId) => AllClient;
        public IChatClient Users(IReadOnlyList<string> userIds)
        {
            TargetedUserIds.Add(userIds);
            return UsersClient;
        }
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
