using AOS.Messenger.Application.Common;
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

public sealed class MessagePersistenceTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public MessagePersistenceTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private (IServiceProvider Provider, ApplicationDbContext Db) CreateTestEnvironment()
    {
        var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return (scope.ServiceProvider, db);
    }

    private static async Task<ConversationId> SeedConversationAsync(ApplicationDbContext db)
    {
        var conv = Conversation.CreateDirect(UserId.New(), UserId.New(), DateTimeOffset.UtcNow);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        return conv.Id;
    }

    [Fact]
    public async Task AddAsync_PersistsMessage_Successfully()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        var message = Message.Create(conversationId, senderId, "key-1", "Hello World", now);

        var persisted = await repo.AddAsync(message);

        Assert.NotNull(persisted);
        Assert.Equal(message.Id, persisted.Id);
        Assert.Equal("Hello World", persisted.Content);
        Assert.Equal("key-1", persisted.IdempotencyKey);

        var retrieved = await repo.GetByIdAsync(conversationId, message.Id);
        Assert.NotNull(retrieved);
        Assert.Equal(message.Id, retrieved.Id);
        Assert.Equal("Hello World", retrieved.Content);
    }

    [Fact]
    public async Task AddAsync_DuplicateKey_SameContent_ReturnsExistingMessageIdempotently()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var message1 = Message.Create(conversationId, senderId, "idem-same", "Same Content", now);
        var added1 = await repo.AddAsync(message1);

        var message2 = Message.Create(conversationId, senderId, "idem-same", "Same Content", now.AddSeconds(1));
        var added2 = await repo.AddAsync(message2);

        Assert.True(added1.IsNewlyCreated);
        Assert.NotNull(added2);
        Assert.False(added2.IsNewlyCreated);
        Assert.Equal(added1.Id, added2.Id);
        Assert.Equal(added1.CreatedAtUtc, added2.CreatedAtUtc);
        Assert.Equal("Same Content", added2.Content);
    }

    [Fact]
    public async Task AddAsync_DuplicateKey_DifferentContent_ThrowsConflictException()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var message1 = Message.Create(conversationId, senderId, "idem-diff", "Original Content", now);
        await repo.AddAsync(message1);

        var message2 = Message.Create(conversationId, senderId, "idem-diff", "Conflicting Content", now.AddSeconds(1));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => repo.AddAsync(message2));

        Assert.Contains("existing message already exists with this idempotency key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddAsync_DuplicateKey_DifferentConversation_ThrowsConflictException()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversation1 = await SeedConversationAsync(db);
        var conversation2 = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var message1 = Message.Create(conversation1, senderId, "same-key-diff-conv", "Hello", now);
        await repo.AddAsync(message1);

        var message2 = Message.Create(conversation2, senderId, "same-key-diff-conv", "Hello", now.AddSeconds(1));
        var ex = await Assert.ThrowsAsync<ConflictException>(() => repo.AddAsync(message2));

        Assert.Contains("existing message already exists with this idempotency key", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddAsync_ConcurrentRace_WithSameContent_ReturnsExistingMessage()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        // Persist message first directly
        var existing = Message.Create(conversationId, senderId, "race-key-1", "Race Content", now);
        var created = await repo.AddAsync(existing);

        // Call AddAsync with another instance with identical content across fresh scope
        using var scope2 = _fixture.CreateScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IMessagePersistence>();
        var raceAttempt = Message.Create(conversationId, senderId, "race-key-1", "Race Content", now);
        var result = await repo2.AddAsync(raceAttempt);

        Assert.True(created.IsNewlyCreated);
        Assert.NotNull(result);
        Assert.False(result.IsNewlyCreated);
        Assert.Equal(created.Id, result.Id);
    }

    [Fact]
    public async Task AddAsync_ConcurrentRace_WithDifferentContent_ThrowsConflictException()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var existing = Message.Create(conversationId, senderId, "race-key-2", "Original", now);
        await repo.AddAsync(existing);

        using var scope2 = _fixture.CreateScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IMessagePersistence>();
        var conflictAttempt = Message.Create(conversationId, senderId, "race-key-2", "Different", now);
        await Assert.ThrowsAsync<ConflictException>(() => repo2.AddAsync(conflictAttempt));
    }

    [Fact]
    public async Task GetKeysetMessagesAsync_OrdersByCreatedAtUtcDesc_And_IdDesc()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var baseTime = DateTimeOffset.UtcNow;

        var msg1 = Message.Create(conversationId, senderId, "k1", "Msg 1 (oldest)", baseTime.AddMinutes(-10));
        var msg2 = Message.Create(conversationId, senderId, "k2", "Msg 2", baseTime.AddMinutes(-5));
        var msg3 = Message.Create(conversationId, senderId, "k3", "Msg 3 (newest)", baseTime);

        db.Messages.AddRange(msg1, msg2, msg3);
        await db.SaveChangesAsync();

        var result = await repo.GetKeysetMessagesAsync(conversationId, null, null, null, pageSize: 10);

        Assert.Equal(3, result.Count);
        Assert.Equal(msg3.Id, result[0].Id);
        Assert.Equal(msg2.Id, result[1].Id);
        Assert.Equal(msg1.Id, result[2].Id);
    }

    [Fact]
    public async Task GetKeysetMessagesAsync_WithIdenticalTimestamp_UsesIdAsDeterministicTieBreaker()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var exactSameTime = DateTimeOffset.UtcNow;

        var msgA = Message.Create(conversationId, senderId, "tie-a", "Msg A", exactSameTime);
        var msgB = Message.Create(conversationId, senderId, "tie-b", "Msg B", exactSameTime);

        db.Messages.AddRange(msgA, msgB);
        await db.SaveChangesAsync();

        // Check which ID is larger after SQL Server generated their identity keys
        var smallerIdMsg = msgA.Id < msgB.Id ? msgA : msgB;
        var largerIdMsg = msgA.Id < msgB.Id ? msgB : msgA;

        // Keyset ordering is DESC, so larger Id must appear first
        var result = await repo.GetKeysetMessagesAsync(conversationId, null, null, null, pageSize: 10);

        Assert.Equal(2, result.Count);
        Assert.Equal(largerIdMsg.Id, result[0].Id);
        Assert.Equal(smallerIdMsg.Id, result[1].Id);
    }

    [Fact]
    public async Task GetKeysetMessagesAsync_FiltersOutMessagesBeforeClearedAtUtc()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var baseTime = DateTimeOffset.UtcNow;

        var msgBeforeClear = Message.Create(conversationId, senderId, "c1", "Before Clear", baseTime.AddMinutes(-10));
        var msgAfterClear1 = Message.Create(conversationId, senderId, "c2", "After Clear 1", baseTime.AddMinutes(-2));
        var msgAfterClear2 = Message.Create(conversationId, senderId, "c3", "After Clear 2", baseTime);

        db.Messages.AddRange(msgBeforeClear, msgAfterClear1, msgAfterClear2);
        await db.SaveChangesAsync();

        var clearedAtUtc = baseTime.AddMinutes(-5);

        var result = await repo.GetKeysetMessagesAsync(conversationId, null, null, clearedAtUtc, pageSize: 10);

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, m => m.Id == msgBeforeClear.Id);
        Assert.Equal(msgAfterClear2.Id, result[0].Id);
        Assert.Equal(msgAfterClear1.Id, result[1].Id);
    }

    [Fact]
    public async Task GetKeysetMessagesAsync_KeysetCursorPagination_CorrectlyAdvancesPages()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var baseTime = DateTimeOffset.UtcNow;

        // Create 5 messages spaced by 1 minute
        var messages = new List<Message>();
        for (int i = 0; i < 5; i++)
        {
            var m = Message.Create(conversationId, senderId, $"page-key-{i}", $"Message {i}", baseTime.AddMinutes(i));
            messages.Add(m);
        }
        db.Messages.AddRange(messages);
        await db.SaveChangesAsync();

        // Page 1: pageSize = 2. Keyset repo takes pageSize + 1 = 3 to probe HasMore
        var page1 = await repo.GetKeysetMessagesAsync(conversationId, null, null, null, pageSize: 2);
        Assert.Equal(3, page1.Count); // Take(pageSize + 1)
        Assert.Equal(messages[4].Id, page1[0].Id);
        Assert.Equal(messages[3].Id, page1[1].Id);
        Assert.Equal(messages[2].Id, page1[2].Id);

        // Page 1 items used by service would be the first 2 (messages[4] and messages[3]).
        // The last item of the actual page is messages[3]. Cursor is (messages[3].CreatedAtUtc, messages[3].Id)
        var cursorTime = messages[3].CreatedAtUtc;
        var cursorId = messages[3].Id;

        // Page 2: fetch from cursor with pageSize = 2
        var page2 = await repo.GetKeysetMessagesAsync(conversationId, cursorTime, cursorId, null, pageSize: 2);
        Assert.Equal(3, page2.Count); // Has messages[2], messages[1], messages[0]
        Assert.Equal(messages[2].Id, page2[0].Id);
        Assert.Equal(messages[1].Id, page2[1].Id);
        Assert.Equal(messages[0].Id, page2[2].Id);

        // Last item of page 2 is messages[1].
        var cursorTime2 = messages[1].CreatedAtUtc;
        var cursorId2 = messages[1].Id;

        // Page 3: fetch from cursor with pageSize = 2
        var page3 = await repo.GetKeysetMessagesAsync(conversationId, cursorTime2, cursorId2, null, pageSize: 2);
        Assert.Single(page3);
        Assert.Equal(messages[0].Id, page3[0].Id);
    }

    [Fact]
    public async Task GetByIdsAsync_ReturnsDictionaryOfFoundMessages()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var m1 = Message.Create(conversationId, senderId, "b1", "M1", now);
        var m2 = Message.Create(conversationId, senderId, "b2", "M2", now);
        var m3 = Message.Create(conversationId, senderId, "b3", "M3", now);

        db.Messages.AddRange(m1, m2, m3);
        await db.SaveChangesAsync();

        var dict = await repo.GetByIdsAsync(conversationId, [m1.Id, m3.Id, MessageId.New()]);

        Assert.Equal(2, dict.Count);
        Assert.True(dict.ContainsKey(m1.Id));
        Assert.True(dict.ContainsKey(m3.Id));
        Assert.False(dict.ContainsKey(m2.Id));
    }

    [Fact]
    public async Task UpdateAsync_PersistsChangesToMessage()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conversationId = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var m = Message.Create(conversationId, senderId, "u1", "Original text", now);
        await repo.AddAsync(m);

        m.Edit("Updated text", now.AddMinutes(5));
        await repo.UpdateAsync(m);

        var retrieved = await repo.GetByIdAsync(conversationId, m.Id);
        Assert.NotNull(retrieved);
        Assert.Equal("Updated text", retrieved.Content);
        Assert.NotNull(retrieved.EditedAtUtc);
    }

    [Fact]
    public async Task GetByIdAsync_WithDifferentConversationId_ReturnsNull()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conv1 = await SeedConversationAsync(db);
        var conv2 = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var message = Message.Create(conv1, senderId, "scoped-key-1", "Scoped test", now);
        await repo.AddAsync(message);

        // Found in conv1
        var inConv1 = await repo.GetByIdAsync(conv1, message.Id);
        Assert.NotNull(inConv1);
        Assert.Equal(message.Id, inConv1.Id);

        // Not found in conv2
        var inConv2 = await repo.GetByIdAsync(conv2, message.Id);
        Assert.Null(inConv2);
    }

    [Fact]
    public async Task GetByIdsAsync_FiltersOutMessagesFromDifferentConversations()
    {
        var (provider, db) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var conv1 = await SeedConversationAsync(db);
        var conv2 = await SeedConversationAsync(db);
        var senderId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var m1 = Message.Create(conv1, senderId, "m1-c1", "Conv1 Msg", now);
        var m2 = Message.Create(conv2, senderId, "m2-c2", "Conv2 Msg", now);

        db.Messages.AddRange(m1, m2);
        await db.SaveChangesAsync();

        var dict = await repo.GetByIdsAsync(conv1, [m1.Id, m2.Id]);
        Assert.Single(dict);
        Assert.True(dict.ContainsKey(m1.Id));
        Assert.False(dict.ContainsKey(m2.Id));
    }
}
