using System.Reflection;
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
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AOS.Messenger.Tests;

/// <summary>
/// Rigorous, independent QA verification tests for Phase 5 Backend deliverables:
/// 1. Idempotency Invariant (first submission, duplicate replay, conflicting payload, concurrent Task.WhenAll)
/// 2. Deterministic Keyset Ordering Invariant (CreatedAtUtc DESC, Id DESC, tie-breaking, cursor traversal)
/// 3. Per-User Cleared Boundary (ClearedAtUtc omission, multi-user isolation, post-clear visibility)
/// 4. Authorization & Privacy (non-participants, non-existent conversations, bidirectional blocks, content length limits)
/// 5. Layer Purity (Application assembly zero references to EF Core or Infrastructure)
/// </summary>
public sealed class MessageBackendVerificationTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public MessageBackendVerificationTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private (IServiceProvider Provider, ApplicationDbContext Db, UserManager<ApplicationUser> UserManager) CreateTestEnvironment(string? dbName = null)
    {
        var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        return (scope.ServiceProvider, db, userManager);
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        UserManager<ApplicationUser> userManager,
        string userName,
        string displayName)
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = userName,
            DisplayName = displayName,
            Email = $"{userName}@example.com",
            EmailConfirmed = true,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IsDeleted = false
        };

        var result = await userManager.CreateAsync(user, "StrongPassw0rd!");
        Assert.True(result.Succeeded, $"Failed to create user {userName}: {string.Join(", ", result.Errors.Select(e => e.Description))}");
        return user;
    }

    private static async Task<Conversation> CreateConversationAsync(
        ApplicationDbContext db,
        UserId userA,
        UserId userB)
    {
        var conversation = Conversation.Create(userA, userB, DateTimeOffset.UtcNow);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }


    [Fact]
    public async Task Idempotency_FirstMessageSubmission_PersistsAndReturnsMessage()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_idem_1", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_idem_1", "Bob");
        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var req = new SendMessageRequestDto("Hello Bob!", "first-key-1");
        var result = await messageService.SendMessageAsync(new UserId(userA.Id), conv.Id, req);

        Assert.NotNull(result);
        Assert.Equal("Hello Bob!", result.Content);
        Assert.Equal(new UserId(userA.Id), result.SenderId);
        Assert.Equal(conv.Id, result.ConversationId);
        Assert.True(result.IsOutgoing);
        Assert.False(result.IsDeletedForEveryone);
        Assert.Null(result.EditedAtUtc);

        // Verify persisted directly in database
        var persisted = await db.Messages.FirstOrDefaultAsync(m => m.Id == result.Id);
        Assert.NotNull(persisted);
        Assert.Equal("first-key-1", persisted.IdempotencyKey);
        Assert.Equal("Hello Bob!", persisted.Content);
    }

    [Fact]
    public async Task Idempotency_DuplicateReplay_SameSenderAndKeyAndContent_ReturnsExistingMessageWithoutDuplicate()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_idem_2", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_idem_2", "Bob");
        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var req1 = new SendMessageRequestDto("Replay test content", "replay-key-1");
        var firstResult = await messageService.SendMessageAsync(new UserId(userA.Id), conv.Id, req1);

        // Second submission with exact same key and content
        var req2 = new SendMessageRequestDto("Replay test content", "replay-key-1");
        var secondResult = await messageService.SendMessageAsync(new UserId(userA.Id), conv.Id, req2);

        Assert.NotNull(secondResult);
        Assert.Equal(firstResult.Id, secondResult.Id);
        Assert.Equal(firstResult.CreatedAtUtc, secondResult.CreatedAtUtc);
        Assert.Equal(firstResult.Content, secondResult.Content);

        // Assert strictly only 1 message exists in DB
        var totalMessages = await db.Messages.CountAsync(m => m.ConversationId == conv.Id);
        Assert.Equal(1, totalMessages);
    }

    [Fact]
    public async Task Idempotency_ConflictingKey_DifferentContent_ThrowsConflictException()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_idem_3", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_idem_3", "Bob");
        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var req1 = new SendMessageRequestDto("Original Content", "conflict-key-1");
        await messageService.SendMessageAsync(new UserId(userA.Id), conv.Id, req1);

        // Submitting with SAME idempotency key but DIFFERENT content must throw ConflictException
        var req2 = new SendMessageRequestDto("Conflicting Content", "conflict-key-1");
        var ex = await Assert.ThrowsAsync<ConflictException>(() =>
            messageService.SendMessageAsync(new UserId(userA.Id), conv.Id, req2));

        Assert.Contains("idempotency key", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Ensure database content was not mutated
        var persisted = await db.Messages.FirstAsync(m => m.IdempotencyKey == "conflict-key-1");
        Assert.Equal("Original Content", persisted.Content);
    }

    [Fact]
    public async Task Idempotency_ConcurrentSubmissions_TaskWhenAll_SucceedsAndReturnsSameMessage()
    {
        var rootProvider = _fixture.CreateServiceProvider();

        // Seed users and conversation
        Guid userAGuid, userBGuid;
        ConversationId convId;
        using (var setupScope = rootProvider.CreateScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userManager = setupScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var userA = await CreateUserAsync(userManager, "alice_concurrent", "Alice");
            var userB = await CreateUserAsync(userManager, "bob_concurrent", "Bob");
            userAGuid = userA.Id;
            userBGuid = userB.Id;

            var conv = await CreateConversationAsync(db, new UserId(userAGuid), new UserId(userBGuid));
            convId = conv.Id;
        }

        const int concurrency = 10;
        var results = new MessageDto[concurrency];

        // Concurrently invoke SendMessageAsync across isolated DI scopes (simulating parallel HTTP requests)
        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            using var scope = rootProvider.CreateScope();
            var service = scope.ServiceProvider.GetRequiredService<IMessageService>();
            var req = new SendMessageRequestDto("Concurrent identical message", "concurrent-idem-key");
            results[i] = await service.SendMessageAsync(new UserId(userAGuid), convId, req);
        });

        await Task.WhenAll(tasks);

        // All concurrent executions must return the exact same message
        var firstResult = results[0];
        Assert.NotNull(firstResult);
        for (var i = 1; i < concurrency; i++)
        {
            Assert.Equal(firstResult.Id, results[i].Id);
            Assert.Equal(firstResult.CreatedAtUtc, results[i].CreatedAtUtc);
            Assert.Equal(firstResult.Content, results[i].Content);
        }

        // Authoritative persistent state must contain exactly one message entity
        using (var verifyScope = rootProvider.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var totalMessages = await db.Messages.CountAsync(m => m.ConversationId == convId);
            Assert.Equal(1, totalMessages);
        }
    }

    [Fact]
    public async Task Idempotency_UniqueIndexEnforcement_MessagePersistenceHandlesDbUpdateExceptionGracefully()
    {
        var (provider, db, _) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IMessagePersistence>();

        var senderId = new UserId(Guid.NewGuid());
        var otherId = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        var conv = Conversation.CreateDirect(senderId, otherId, now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();
        var convId = conv.Id;

        // Persist initial message
        var initial = Message.Create(convId, senderId, "race-key-xyz", "Identical Content", now);
        var created = await repo.AddAsync(initial);

        // Another request creates message with identical content; SQL Server unique index on (SenderId, IdempotencyKey)
        // throws DbUpdateException during SaveChangesAsync, and AddAsync catches it, re-queries existing, and returns it cleanly.
        using var scope2 = _fixture.CreateScope();
        var repo2 = scope2.ServiceProvider.GetRequiredService<IMessagePersistence>();
        var duplicateAttempt = Message.Create(convId, senderId, "race-key-xyz", "Identical Content", now);
        var resolved = await repo2.AddAsync(duplicateAttempt);

        Assert.NotNull(resolved);
        Assert.Equal(created.Id, resolved.Id);
        Assert.Equal("Identical Content", resolved.Content);
    }



    [Fact]
    public async Task KeysetOrdering_MessagesReturnedStrictlyOrderedBy_CreatedAtUtcDesc_Then_IdDesc()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_order_1", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_order_1", "Bob");
        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var baseTime = DateTimeOffset.UtcNow;
        var messages = new List<Message>();
        for (var i = 0; i < 6; i++)
        {
            var msg = Message.Create(
                conv.Id,
                new UserId(userA.Id),
                $"order-key-{i}",
                $"Message {i}",
                baseTime.AddMinutes(i * 2));
            messages.Add(msg);
        }

        db.Messages.AddRange(messages);
        await db.SaveChangesAsync();

        var page = await messageService.GetMessagesAsync(new UserId(userA.Id), conv.Id, cursor: null, pageSize: 20);

        Assert.Equal(6, page.Messages.Count);
        // Strict descending total order
        for (var i = 0; i < page.Messages.Count - 1; i++)
        {
            var current = page.Messages[i];
            var next = page.Messages[i + 1];

            var isStrictlyNewer = current.CreatedAtUtc > next.CreatedAtUtc ||
                (current.CreatedAtUtc == next.CreatedAtUtc && current.Id > next.Id);
            Assert.True(isStrictlyNewer, $"Message at index {i} was not strictly greater than index {i + 1}.");
        }

        // Specifically: index 0 must be the newest message (i=5)
        Assert.Equal(messages[5].Id, page.Messages[0].Id);
        Assert.Equal(messages[0].Id, page.Messages[5].Id);
    }

    [Fact]
    public async Task KeysetOrdering_TieBreaking_IdenticalTimestamp_UsesDeterministicIdDesc()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_tie_1", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_tie_1", "Bob");
        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var exactSameTime = new DateTimeOffset(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

        var msg1 = Message.Create(conv.Id, new UserId(userA.Id), "tie-k1", "Tie Message 1", exactSameTime);
        var msg2 = Message.Create(conv.Id, new UserId(userA.Id), "tie-k2", "Tie Message 2", exactSameTime);

        var smallerId = msg1.Id < msg2.Id ? msg1 : msg2;
        var largerId = msg1.Id < msg2.Id ? msg2 : msg1;

        db.Messages.AddRange(smallerId, largerId);
        await db.SaveChangesAsync();

        // In descending order: larger ID must appear first
        var page = await messageService.GetMessagesAsync(new UserId(userA.Id), conv.Id, cursor: null, pageSize: 10);

        Assert.Equal(2, page.Messages.Count);
        Assert.Equal(largerId.Id, page.Messages[0].Id);
        Assert.Equal(smallerId.Id, page.Messages[1].Id);

        // Verify keyset pagination splits exactly on this tie
        var page1 = await messageService.GetMessagesAsync(new UserId(userA.Id), conv.Id, cursor: null, pageSize: 1);
        Assert.Single(page1.Messages);
        Assert.Equal(largerId.Id, page1.Messages[0].Id);
        Assert.True(page1.HasMore);
        Assert.NotNull(page1.NextCursor);

        var page2 = await messageService.GetMessagesAsync(new UserId(userA.Id), conv.Id, cursor: page1.NextCursor, pageSize: 1);
        Assert.Single(page2.Messages);
        Assert.Equal(smallerId.Id, page2.Messages[0].Id);
        Assert.False(page2.HasMore);
    }

    [Fact]
    public async Task KeysetOrdering_CursorAdvancesPagesDeterministically_WithoutSkippingOrDuplicating()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_cursor_1", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_cursor_1", "Bob");
        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        const int totalCount = 11;
        var baseTime = DateTimeOffset.UtcNow;
        var expectedMessages = new List<Message>();
        for (var i = 0; i < totalCount; i++)
        {
            var msg = Message.Create(
                conv.Id,
                new UserId(userA.Id),
                $"cursor-key-{i}",
                $"Cursor Item {i}",
                baseTime.AddMinutes(i));
            expectedMessages.Add(msg);
        }

        db.Messages.AddRange(expectedMessages);
        await db.SaveChangesAsync();

        // Paginate using pageSize = 3
        const int pageSize = 3;
        var collected = new List<MessageDto>();
        string? cursor = null;
        var pageCount = 0;

        do
        {
            pageCount++;
            var page = await messageService.GetMessagesAsync(new UserId(userA.Id), conv.Id, cursor, pageSize);
            collected.AddRange(page.Messages);
            cursor = page.NextCursor;

            if (page.HasMore)
            {
                Assert.NotNull(cursor);
            }
            else
            {
                Assert.Null(cursor);
            }
        } while (cursor != null);

        // Expected 4 pages: 3, 3, 3, 2 = 11
        Assert.Equal(4, pageCount);
        Assert.Equal(totalCount, collected.Count);

        // Invariant: No duplicates
        var uniqueIds = collected.Select(m => m.Id).ToHashSet();
        Assert.Equal(totalCount, uniqueIds.Count);

        // Invariant: No skips (all originally created messages are present)
        foreach (var msg in expectedMessages)
        {
            Assert.Contains(msg.Id, uniqueIds);
        }

        // Invariant: Strict descending order throughout
        for (var i = 0; i < collected.Count - 1; i++)
        {
            var isStrictlyDescending = collected[i].CreatedAtUtc > collected[i + 1].CreatedAtUtc ||
                (collected[i].CreatedAtUtc == collected[i + 1].CreatedAtUtc && collected[i].Id > collected[i + 1].Id);
            Assert.True(isStrictlyDescending, $"Order violated at index {i}");
        }
    }



    [Fact]
    public async Task ClearedBoundary_MessagesBeforeClearedAtUtc_OmittedForUser_VisibleToOther()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();
        var convPersistence = provider.GetRequiredService<IConversationPersistence>();

        var userA = await CreateUserAsync(userManager, "alice_clear_1", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_clear_1", "Bob");
        var userAId = new UserId(userA.Id);
        var userBId = new UserId(userB.Id);
        var conv = await CreateConversationAsync(db, userAId, userBId);

        var baseTime = DateTimeOffset.UtcNow;
        var msg1 = Message.Create(conv.Id, userAId, "c1", "Msg 1 before clear", baseTime.AddMinutes(-30));
        var msg2 = Message.Create(conv.Id, userBId, "c2", "Msg 2 before clear", baseTime.AddMinutes(-20));
        var msg3 = Message.Create(conv.Id, userAId, "c3", "Msg 3 after clear", baseTime.AddMinutes(-5));
        var msg4 = Message.Create(conv.Id, userBId, "c4", "Msg 4 after clear", baseTime);

        db.Messages.AddRange(msg1, msg2, msg3, msg4);
        await db.SaveChangesAsync();

        // Set User A's cleared boundary between msg2 and msg3 (-10 minutes)
        var stateA = UserConversationState.Create(conv.Id, userAId);
        stateA.ClearChat(baseTime.AddMinutes(-10));
        await convPersistence.UpsertUserStateAsync(stateA);

        // User A's perspective: should only see msg3 and msg4
        var pageA = await messageService.GetMessagesAsync(userAId, conv.Id, cursor: null, pageSize: 20);
        Assert.Equal(2, pageA.Messages.Count);
        Assert.Equal(msg4.Id, pageA.Messages[0].Id);
        Assert.Equal(msg3.Id, pageA.Messages[1].Id);

        // User B's perspective: has no cleared boundary, should see all 4 messages
        var pageB = await messageService.GetMessagesAsync(userBId, conv.Id, cursor: null, pageSize: 20);
        Assert.Equal(4, pageB.Messages.Count);
        Assert.Equal(msg4.Id, pageB.Messages[0].Id);
        Assert.Equal(msg3.Id, pageB.Messages[1].Id);
        Assert.Equal(msg2.Id, pageB.Messages[2].Id);
        Assert.Equal(msg1.Id, pageB.Messages[3].Id);

        // Sending a new message after clear
        var req = new SendMessageRequestDto("Msg 5 newly sent", "c5");
        var newMsg = await messageService.SendMessageAsync(userAId, conv.Id, req);

        // User A now sees 3 messages (msg5, msg4, msg3)
        var pageAUpdated = await messageService.GetMessagesAsync(userAId, conv.Id, cursor: null, pageSize: 20);
        Assert.Equal(3, pageAUpdated.Messages.Count);
        Assert.Equal(newMsg.Id, pageAUpdated.Messages[0].Id);
        Assert.Equal(msg4.Id, pageAUpdated.Messages[1].Id);
        Assert.Equal(msg3.Id, pageAUpdated.Messages[2].Id);
    }



    [Fact]
    public async Task Authorization_NonParticipant_CannotReadOrSendMessage()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_auth_1", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_auth_1", "Bob");
        var eve = await CreateUserAsync(userManager, "eve_intruder", "Eve");

        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var eveId = new UserId(eve.Id);

        // Eve cannot read messages
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            messageService.GetMessagesAsync(eveId, conv.Id, cursor: null));

        // Eve cannot send messages
        var req = new SendMessageRequestDto("Eve intrusive message", "eve-key-1");
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            messageService.SendMessageAsync(eveId, conv.Id, req));
    }

    [Fact]
    public async Task Authorization_NonExistentConversation_ThrowsNotFoundException()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_nf_1", "Alice");
        var fakeConvId = ConversationId.New();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            messageService.GetMessagesAsync(new UserId(userA.Id), fakeConvId, cursor: null));

        var req = new SendMessageRequestDto("Hello nowhere", "nf-key-1");
        await Assert.ThrowsAsync<NotFoundException>(() =>
            messageService.SendMessageAsync(new UserId(userA.Id), fakeConvId, req));
    }

    [Fact]
    public async Task Privacy_SendingRejected_WhenEitherParticipantHasBlockedTheOther()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();
        var blockService = provider.GetRequiredService<IBlockService>();

        var userA = await CreateUserAsync(userManager, "alice_block_1", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_block_1", "Bob");
        var userAId = new UserId(userA.Id);
        var userBId = new UserId(userB.Id);
        var conv = await CreateConversationAsync(db, userAId, userBId);

        // User A blocks User B
        await blockService.BlockUserAsync(userAId, userBId);

        // User A cannot send
        var reqA = new SendMessageRequestDto("A to B blocked", "blk-key-a");
        var exA = await Assert.ThrowsAsync<ForbiddenException>(() =>
            messageService.SendMessageAsync(userAId, conv.Id, reqA));
        Assert.Contains("blocked", exA.Message, StringComparison.OrdinalIgnoreCase);

        // User B cannot send (bidirectional enforcement)
        var reqB = new SendMessageRequestDto("B to A blocked", "blk-key-b");
        var exB = await Assert.ThrowsAsync<ForbiddenException>(() =>
            messageService.SendMessageAsync(userBId, conv.Id, reqB));
        Assert.Contains("blocked", exB.Message, StringComparison.OrdinalIgnoreCase);

        // Unblock
        await blockService.UnblockUserAsync(userAId, userBId);

        // Sending now succeeds
        var successResult = await messageService.SendMessageAsync(userAId, conv.Id, new SendMessageRequestDto("Unblocked message", "blk-key-success"));
        Assert.NotNull(successResult);
        Assert.Equal("Unblocked message", successResult.Content);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n  ")]
    public async Task Validation_EmptyOrWhitespaceContent_ThrowsDomainException(string invalidContent)
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, $"alice_val_{Guid.NewGuid():N}"[..15], "Alice");
        var userB = await CreateUserAsync(userManager, $"bob_val_{Guid.NewGuid():N}"[..15], "Bob");
        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var req = new SendMessageRequestDto(invalidContent, "val-key-1");
        await Assert.ThrowsAsync<DomainException>(() =>
            messageService.SendMessageAsync(new UserId(userA.Id), conv.Id, req));
    }

    [Fact]
    public async Task Validation_ContentExceeding4000Characters_ThrowsDomainException()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_len_1", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_len_1", "Bob");
        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        // 4001 characters
        var excessiveContent = new string('x', 4001);
        var req = new SendMessageRequestDto(excessiveContent, "val-len-key");
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            messageService.SendMessageAsync(new UserId(userA.Id), conv.Id, req));
        Assert.Contains("4000", ex.Message);

        // Exactly 4000 characters must succeed
        var validContent = new string('y', 4000);
        var validReq = new SendMessageRequestDto(validContent, "val-valid-4000");
        var result = await messageService.SendMessageAsync(new UserId(userA.Id), conv.Id, validReq);
        Assert.NotNull(result);
        Assert.Equal(4000, result.Content.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Validation_EmptyOrWhitespaceIdempotencyKey_ThrowsDomainException(string invalidKey)
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, $"alice_k_{Guid.NewGuid():N}"[..15], "Alice");
        var userB = await CreateUserAsync(userManager, $"bob_k_{Guid.NewGuid():N}"[..15], "Bob");
        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var req = new SendMessageRequestDto("Valid content", invalidKey);
        await Assert.ThrowsAsync<DomainException>(() =>
            messageService.SendMessageAsync(new UserId(userA.Id), conv.Id, req));
    }

    [Fact]
    public async Task Validation_IdempotencyKeyExceeding128Characters_ThrowsDomainException()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_klen_1", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_klen_1", "Bob");
        var conv = await CreateConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var excessiveKey = new string('k', 129);
        var req = new SendMessageRequestDto("Valid content", excessiveKey);
        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            messageService.SendMessageAsync(new UserId(userA.Id), conv.Id, req));
        Assert.Contains("128", ex.Message);
    }

    [Fact]
    public async Task Validation_ReplyToMessage_CrossConversation_ThrowsNotFoundException()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var messageService = provider.GetRequiredService<IMessageService>();

        var userA = await CreateUserAsync(userManager, "alice_cross_1", "Alice");
        var userB = await CreateUserAsync(userManager, "bob_cross_1", "Bob");
        var userC = await CreateUserAsync(userManager, "carol_cross_1", "Carol");

        var userAId = new UserId(userA.Id);
        var userBId = new UserId(userB.Id);
        var userCId = new UserId(userC.Id);

        var conv1 = await CreateConversationAsync(db, userAId, userBId);
        var conv2 = await CreateConversationAsync(db, userAId, userCId);

        // Send message in conv2
        var msgConv2 = await messageService.SendMessageAsync(userAId, conv2.Id, new SendMessageRequestDto("Msg in conv 2", "c2-key-1"));

        // Attempt to reply in conv1 using parent message from conv2
        var crossReplyReq = new SendMessageRequestDto("Cross reply attempt", "cross-reply-key", msgConv2.Id);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            messageService.SendMessageAsync(userAId, conv1.Id, crossReplyReq));
    }



    [Fact]
    public void LayerPurity_ApplicationAssembly_HasZeroReferencesTo_EntityFrameworkCore_Or_Infrastructure()
    {
        var applicationAssembly = typeof(IMessageService).Assembly;
        var referencedAssemblies = applicationAssembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        // 1. Must not reference Infrastructure
        Assert.DoesNotContain(referencedAssemblies, name =>
            name.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));

        // 2. Must not reference Web
        Assert.DoesNotContain(referencedAssemblies, name =>
            name.Contains("Web", StringComparison.OrdinalIgnoreCase));

        // 3. Must not reference EntityFrameworkCore
        Assert.DoesNotContain(referencedAssemblies, name =>
            name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));

        // 4. Must not reference Microsoft.AspNetCore.Identity.EntityFrameworkCore
        Assert.DoesNotContain(referencedAssemblies, name =>
            name.Contains("Identity.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LayerPurity_ApplicationTypes_HaveZeroDependenciesOn_InfrastructureTypes()
    {
        var applicationAssembly = typeof(IMessageService).Assembly;
        var appTypes = applicationAssembly.GetTypes().Where(t => t.IsPublic).ToList();

        foreach (var type in appTypes)
        {
            // Verify constructors
            foreach (var ctor in type.GetConstructors())
            {
                foreach (var param in ctor.GetParameters())
                {
                    AssertTypeNotFromInfrastructureOrEfCore(param.ParameterType, type.FullName!, $"constructor parameter '{param.Name}'");
                }
            }

            // Verify properties
            foreach (var prop in type.GetProperties())
            {
                AssertTypeNotFromInfrastructureOrEfCore(prop.PropertyType, type.FullName!, $"property '{prop.Name}'");
            }

            // Verify methods
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                AssertTypeNotFromInfrastructureOrEfCore(method.ReturnType, type.FullName!, $"method '{method.Name}' return type");
                foreach (var param in method.GetParameters())
                {
                    AssertTypeNotFromInfrastructureOrEfCore(param.ParameterType, type.FullName!, $"method '{method.Name}' parameter '{param.Name}'");
                }
            }
        }
    }

    private static void AssertTypeNotFromInfrastructureOrEfCore(Type type, string targetTypeName, string memberDescription)
    {
        var unwrapped = Nullable.GetUnderlyingType(type) ?? type;
        if (unwrapped.IsGenericType)
        {
            foreach (var genericArg in unwrapped.GetGenericArguments())
            {
                AssertTypeNotFromInfrastructureOrEfCore(genericArg, targetTypeName, memberDescription);
            }
        }

        var ns = unwrapped.Namespace ?? string.Empty;
        var isForbidden = ns.StartsWith("AOS.Messenger.Infrastructure", StringComparison.OrdinalIgnoreCase) ||
                          ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
                          ns.StartsWith("AOS.Messenger.Web", StringComparison.OrdinalIgnoreCase);

        Assert.False(isForbidden,
            $"Application type '{targetTypeName}' ({memberDescription}) violates layer purity by referencing forbidden type '{unwrapped.FullName}'.");
    }
}
