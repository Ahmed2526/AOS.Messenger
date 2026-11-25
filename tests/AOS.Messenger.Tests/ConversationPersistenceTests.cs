using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class ConversationPersistenceTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public ConversationPersistenceTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private (IServiceProvider Provider, ApplicationDbContext Db) CreateTestEnvironment()
    {
        var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return (scope.ServiceProvider, db);
    }

    [Fact]
    public async Task AddAsync_And_GetByIdAsync_WorkCorrectly()
    {
        var (provider, _) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IConversationPersistence>();

        var userA = new UserId(Guid.NewGuid());
        var userB = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var conversation = Conversation.Create(userA, userB, now);
        var added = await repo.AddAsync(conversation);

        Assert.NotNull(added);
        Assert.Equal(conversation.Id, added.Id);

        var retrieved = await repo.GetByIdAsync(conversation.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(conversation.Id, retrieved.Id);
        Assert.True(retrieved.HasParticipant(userA));
        Assert.True(retrieved.HasParticipant(userB));
    }

    [Fact]
    public async Task GetByParticipantsAsync_ReturnsConversation_RegardlessOfArgumentOrder()
    {
        var (provider, _) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IConversationPersistence>();

        var userA = new UserId(Guid.NewGuid());
        var userB = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var conversation = Conversation.Create(userA, userB, now);
        await repo.AddAsync(conversation);

        var retrieved1 = await repo.GetByParticipantsAsync(userA, userB);
        Assert.NotNull(retrieved1);
        Assert.Equal(conversation.Id, retrieved1.Id);

        var retrieved2 = await repo.GetByParticipantsAsync(userB, userA);
        Assert.NotNull(retrieved2);
        Assert.Equal(conversation.Id, retrieved2.Id);

        var nonExistentUser = new UserId(Guid.NewGuid());
        var nonExistent = await repo.GetByParticipantsAsync(userA, nonExistentUser);
        Assert.Null(nonExistent);

        var self = await repo.GetByParticipantsAsync(userA, userA);
        Assert.Null(self);
    }

    [Fact]
    public async Task AddAsync_IsIdempotent_WhenConversationAlreadyExists()
    {
        var (provider, _) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IConversationPersistence>();

        var userA = new UserId(Guid.NewGuid());
        var userB = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var conversation1 = Conversation.Create(userA, userB, now);
        var added1 = await repo.AddAsync(conversation1);

        var conversation2 = Conversation.Create(userB, userA, now.AddMinutes(1));
        var added2 = await repo.AddAsync(conversation2);

        Assert.Equal(added1.Id, added2.Id);
    }

    [Fact]
    public async Task UpsertUserStateAsync_CreatesAndUpdates_UserStateCorrectly()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IConversationPersistence>();

        var userId = new UserId(Guid.NewGuid());
        var otherUser = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var conversation = Conversation.CreateDirect(userId, otherUser, now);
        await repo.AddAsync(conversation);
        var conversationId = conversation.Id;

        // Initially no state
        var initial = await repo.GetUserStateAsync(conversationId, userId);
        Assert.Null(initial);

        // Add state
        var state = UserConversationState.Create(conversationId, userId);
        state.Pin(now);
        state.Mute(now);
        await repo.UpsertUserStateAsync(state);

        var retrieved = await repo.GetUserStateAsync(conversationId, userId);
        Assert.NotNull(retrieved);
        Assert.True(retrieved.IsPinned);
        Assert.NotNull(retrieved.PinnedAtUtc);
        Assert.NotNull(retrieved.MutedAtUtc);
        Assert.False(retrieved.IsArchived);

        // Update state
        retrieved.Unpin();
        retrieved.Archive(now.AddMinutes(5));
        retrieved.ClearChat(now.AddMinutes(6));
        await repo.UpsertUserStateAsync(retrieved);

        var updated = await repo.GetUserStateAsync(conversationId, userId);
        Assert.NotNull(updated);
        Assert.False(updated.IsPinned);
        Assert.True(updated.IsArchived);
        Assert.NotNull(updated.ArchivedAtUtc);
        Assert.NotNull(updated.ClearedAtUtc);
    }

    [Fact]
    public async Task GetConversationsForUserAsync_ReturnsConversations_WhereUserIsEitherParticipant()
    {
        var (provider, _) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IConversationPersistence>();

        var targetUser = new UserId(Guid.NewGuid());
        var userB = new UserId(Guid.NewGuid());
        var userC = new UserId(Guid.NewGuid());
        var otherUser1 = new UserId(Guid.NewGuid());
        var otherUser2 = new UserId(Guid.NewGuid());

        var conv1 = Conversation.Create(targetUser, userB, DateTimeOffset.UtcNow);
        var conv2 = Conversation.Create(userC, targetUser, DateTimeOffset.UtcNow);
        var convOther = Conversation.Create(otherUser1, otherUser2, DateTimeOffset.UtcNow);

        await repo.AddAsync(conv1);
        await repo.AddAsync(conv2);
        await repo.AddAsync(convOther);

        var userConvs = await repo.GetConversationsForUserAsync(targetUser);
        Assert.Equal(2, userConvs.Count);
        Assert.Contains(userConvs, c => c.Id == conv1.Id);
        Assert.Contains(userConvs, c => c.Id == conv2.Id);
        Assert.DoesNotContain(userConvs, c => c.Id == convOther.Id);
    }

    [Fact]
    public async Task GetLastMessageAsync_And_GetUnreadCountAsync_WorkCorrectly()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IConversationPersistence>();

        var userA = new UserId(Guid.NewGuid());
        var userB = new UserId(Guid.NewGuid());
        var baseTime = DateTimeOffset.UtcNow;

        var conv = Conversation.CreateDirect(userA, userB, baseTime);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        var conversationId = conv.Id;

        var msg1 = Message.Create(conversationId, userB, "key-1", "Hello 1", baseTime);
        var msg2 = Message.Create(conversationId, userB, "key-2", "Hello 2", baseTime.AddMinutes(1));
        var msg3 = Message.Create(conversationId, userB, "key-3", "Hello 3", baseTime.AddMinutes(2));

        db.Messages.AddRange(msg1, msg2, msg3);
        await db.SaveChangesAsync();

        // Last message with no filter
        var last = await repo.GetLastMessageAsync(conversationId);
        Assert.NotNull(last);
        Assert.Equal(msg3.Id, last.Id);

        // Last message with cleared filter after msg2
        var lastAfterClear = await repo.GetLastMessageAsync(conversationId, baseTime.AddMinutes(1).AddSeconds(30));
        Assert.NotNull(lastAfterClear);
        Assert.Equal(msg3.Id, lastAfterClear.Id);

        // Last message after msg3
        var lastAfterAll = await repo.GetLastMessageAsync(conversationId, baseTime.AddMinutes(5));
        Assert.Null(lastAfterAll);

        // Unread count: userA has not read any
        var unreadAll = await repo.GetUnreadCountAsync(conversationId, userA);
        Assert.Equal(3, unreadAll);

        // Unread count: userA read up to msg2
        var unreadAfterRead = await repo.GetUnreadCountAsync(conversationId, userA, msg2.Id);
        Assert.Equal(1, unreadAfterRead);

        // HasMessagesAfterAsync
        Assert.True(await repo.HasMessagesAfterAsync(conversationId, baseTime.AddMinutes(1)));
        Assert.False(await repo.HasMessagesAfterAsync(conversationId, baseTime.AddMinutes(5)));
    }

    [Fact]
    public async Task GetUnreadCountAsync_WithIdenticalTimestamp_UsesDeterministicTieBreaker()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IConversationPersistence>();

        var userA = new UserId(Guid.NewGuid());
        var userB = new UserId(Guid.NewGuid());
        var exactSameTime = DateTimeOffset.UtcNow;

        var conv = Conversation.CreateDirect(userA, userB, exactSameTime);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        var conversationId = conv.Id;

        var msg1 = Message.Create(conversationId, userB, "tie-1", "Msg 1", exactSameTime);
        var msg2 = Message.Create(conversationId, userB, "tie-2", "Msg 2", exactSameTime);

        db.Messages.AddRange(msg1, msg2);
        await db.SaveChangesAsync();

        // Sort so we know which is smaller and which is larger after SQL Server generated their identity IDs
        var smaller = msg1.Id < msg2.Id ? msg1 : msg2;
        var larger = msg1.Id < msg2.Id ? msg2 : msg1;

        // When reading up to the smaller message ID, the larger message should still be counted as unread
        var unreadAfterSmaller = await repo.GetUnreadCountAsync(conversationId, userA, smaller.Id);
        Assert.Equal(1, unreadAfterSmaller);

        // When reading up to the larger message ID, 0 messages are unread
        var unreadAfterLarger = await repo.GetUnreadCountAsync(conversationId, userA, larger.Id);
        Assert.Equal(0, unreadAfterLarger);
    }

    [Fact]
    public async Task BatchedQueries_WorkCorrectlyAcrossMultipleConversations()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IConversationPersistence>();

        var userA = new UserId(Guid.NewGuid());
        var userB = new UserId(Guid.NewGuid());
        var userC = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var c1 = Conversation.CreateDirect(userA, userB, now);
        var c2 = Conversation.CreateDirect(userA, userC, now);
        var c3 = Conversation.CreateDirect(userB, userC, now);
        db.Conversations.AddRange(c1, c2, c3);
        await db.SaveChangesAsync();

        var conv1 = c1.Id;
        var conv2 = c2.Id;
        var conv3 = c3.Id; // Empty conversation

        // Conv 1 messages: 3 messages from userB
        var c1m1 = Message.Create(conv1, userB, "c1-1", "Conv1 Msg1", now);
        var c1m2 = Message.Create(conv1, userB, "c1-2", "Conv1 Msg2", now.AddMinutes(1));
        var c1m3 = Message.Create(conv1, userB, "c1-3", "Conv1 Msg3", now.AddMinutes(2));

        // Conv 2 messages: 2 messages from userC, 1 from userA
        var c2m1 = Message.Create(conv2, userC, "c2-1", "Conv2 Msg1", now);
        var c2m2 = Message.Create(conv2, userA, "c2-2", "Conv2 From A", now.AddMinutes(1));
        var c2m3 = Message.Create(conv2, userC, "c2-3", "Conv2 Msg3", now.AddMinutes(3));

        db.Messages.AddRange(c1m1, c1m2, c1m3, c2m1, c2m2, c2m3);
        await db.SaveChangesAsync();

        // 1. Batched GetLastMessagesForConversationsAsync
        var lastMessages = await repo.GetLastMessagesForConversationsAsync([conv1, conv2, conv3]);
        Assert.Equal(2, lastMessages.Count);
        Assert.Equal(c1m3.Id, lastMessages[conv1].Id);
        Assert.Equal(c2m3.Id, lastMessages[conv2].Id);
        Assert.False(lastMessages.ContainsKey(conv3));

        // 2. Batched GetConversationsWithMessagesAfterAsync
        var afterRequests = new List<(ConversationId, DateTimeOffset)>
        {
            (conv1, now.AddMinutes(1).AddSeconds(30)), // c1m3 is at +2m => True
            (conv2, now.AddMinutes(4)),               // latest is at +3m => False
            (conv3, now.AddMinutes(-5))               // no messages => False
        };
        var activeAfter = await repo.GetConversationsWithMessagesAfterAsync(afterRequests);
        Assert.Single(activeAfter);
        Assert.Contains(conv1, activeAfter);
        Assert.DoesNotContain(conv2, activeAfter);
        Assert.DoesNotContain(conv3, activeAfter);

        // 3. Batched GetUnreadCountsForConversationsAsync
        var unreadRequests = new List<(ConversationId ConversationId, MessageId? LastReadMessageId, DateTimeOffset? ClearedAtUtc)>
        {
            (conv1, c1m1.Id, null), // unread should be 2 (c1m2, c1m3)
            (conv2, null, null),    // unread should be 2 (c2m1, c2m3; c2m2 is from userA so excluded)
            (conv3, null, null)     // unread should be 0
        };
        var unreadCounts = await repo.GetUnreadCountsForConversationsAsync(userA, unreadRequests);
        Assert.Equal(3, unreadCounts.Count);
        Assert.Equal(2, unreadCounts[conv1]);
        Assert.Equal(2, unreadCounts[conv2]);
        Assert.Equal(0, unreadCounts[conv3]);
    }

    [Fact]
    public async Task GetConversationsForUserAsync_SortsByRecentMessageActivity_AndUnreadHandlesClearedAndStaleWatermarks()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IConversationPersistence>();

        var userA = new UserId(Guid.NewGuid());
        var userB = new UserId(Guid.NewGuid());
        var userC = new UserId(Guid.NewGuid());
        var baseTime = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.Zero);

        var olderConvWithRecentMsg = Conversation.CreateDirect(userA, userB, baseTime);
        var newerIdleConv = Conversation.CreateDirect(userA, userC, baseTime.AddHours(1));
        db.Conversations.AddRange(olderConvWithRecentMsg, newerIdleConv);
        await db.SaveChangesAsync();

        var staleOtherConvMsg = Message.Create(newerIdleConv.Id, userC, "sort-idle-1", "Early", baseTime.AddMinutes(5));
        db.Messages.Add(staleOtherConvMsg);
        await db.SaveChangesAsync();

        var recentMsg = Message.Create(olderConvWithRecentMsg.Id, userB, "sort-active-1", "Recent bump", baseTime.AddHours(5));
        var tieSameTsMsg = Message.Create(olderConvWithRecentMsg.Id, userB, "sort-active-2", "Same ts 2", baseTime.AddHours(5));
        db.Messages.AddRange(recentMsg, tieSameTsMsg);
        await db.SaveChangesAsync();

        var orderedConvs = await repo.GetConversationsForUserAsync(userA);
        Assert.Equal(2, orderedConvs.Count);
        Assert.Equal(olderConvWithRecentMsg.Id, orderedConvs[0].Id);
        Assert.Equal(newerIdleConv.Id, orderedConvs[1].Id);

        // Unread checks:
        // - Cross-conversation LastReadMessageId (staleOtherConvMsg passed for olderConv) must not suppress messages in olderConv
        // - Exact timestamp tie-breaker counts tieSameTsMsg after recentMsg
        // - ClearedAtUtc threshold >= filters out earlier message cleanly
        var counts = await repo.GetUnreadCountsForConversationsAsync(
            userA,
            [
                (olderConvWithRecentMsg.Id, recentMsg.Id, (DateTimeOffset?)null),
                (newerIdleConv.Id, recentMsg.Id, baseTime.AddMinutes(10))
            ]);
        Assert.Equal(1, counts[olderConvWithRecentMsg.Id]);
        Assert.Equal(0, counts[newerIdleConv.Id]);
    }
}
