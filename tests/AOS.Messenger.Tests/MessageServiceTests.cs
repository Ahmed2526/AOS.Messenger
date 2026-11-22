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

public sealed class MessageServiceTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public MessageServiceTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private (IServiceProvider Provider, ApplicationDbContext Db, MessageService Service) CreateTestEnvironment()
    {
        var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var service = (MessageService)scope.ServiceProvider.GetRequiredService<IMessageService>();

        return (scope.ServiceProvider, db, service);
    }

    private static async Task<ApplicationUser> SeedUserAsync(ApplicationDbContext db, string userName, string displayName)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var uniqueUserName = $"{userName}_{suffix}";
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = uniqueUserName,
            NormalizedUserName = uniqueUserName.ToUpperInvariant(),
            Email = $"{uniqueUserName}@example.com",
            NormalizedEmail = $"{uniqueUserName.ToUpperInvariant()}@EXAMPLE.COM",
            DisplayName = displayName
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static async Task<Conversation> SeedConversationAsync(ApplicationDbContext db, UserId userA, UserId userB)
    {
        var conversation = Conversation.Create(userA, userB, DateTimeOffset.UtcNow);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync();
        return conversation;
    }

    [Fact]
    public async Task SendMessageAsync_WithValidParticipant_Succeeds()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice Cooper");
        var userB = await SeedUserAsync(db, "bob", "Bob Dylan");
        var userIdA = new UserId(userA.Id);
        var userIdB = new UserId(userB.Id);

        var conv = await SeedConversationAsync(db, userIdA, userIdB);

        var dto = new SendMessageRequestDto("Hello Bob!", "send-key-1");
        var result = await service.SendMessageAsync(userIdA, conv.Id, dto);

        Assert.NotNull(result);
        Assert.Equal(conv.Id, result.ConversationId);
        Assert.Equal(userIdA, result.SenderId);
        Assert.Equal("Alice Cooper", result.SenderName);
        Assert.Equal(userA.UserName, result.SenderHandle);
        Assert.Equal("Hello Bob!", result.Content);
        Assert.True(result.IsOutgoing);
        Assert.False(result.IsDeletedForEveryone);
        Assert.Null(result.ReplyTo);
        Assert.True(result.CreatedAtUtc <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task SendMessageAsync_WhenNonParticipant_ThrowsForbiddenException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var userC = await SeedUserAsync(db, "charlie", "Charlie");

        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var dto = new SendMessageRequestDto("Trespassing message", "key-c");
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.SendMessageAsync(new UserId(userC.Id), conv.Id, dto));

        Assert.Contains("not a participant", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendMessageAsync_WhenConversationDoesNotExist_ThrowsNotFoundException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var nonExistentConvId = ConversationId.New();

        var dto = new SendMessageRequestDto("Hello", "key-missing-conv");
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.SendMessageAsync(new UserId(userA.Id), nonExistentConvId, dto));
    }

    [Fact]
    public async Task SendMessageAsync_WhenBlockedBidirectional_ThrowsForbiddenException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var userIdA = new UserId(userA.Id);
        var userIdB = new UserId(userB.Id);

        var conv = await SeedConversationAsync(db, userIdA, userIdB);

        // Bob blocks Alice
        var block = UserBlock.Create(userIdB, userIdA, DateTimeOffset.UtcNow);
        db.UserBlocks.Add(block);
        await db.SaveChangesAsync();

        var dto = new SendMessageRequestDto("Can you hear me?", "key-blocked-1");
        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.SendMessageAsync(userIdA, conv.Id, dto));

        Assert.Contains("blocked conversation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendMessageAsync_EmptyOrWhitespaceContent_ThrowsDomainException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var dtoEmpty = new SendMessageRequestDto("", "key-empty");
        await Assert.ThrowsAsync<DomainException>(() =>
            service.SendMessageAsync(new UserId(userA.Id), conv.Id, dtoEmpty));

        var dtoWhitespace = new SendMessageRequestDto("   \t\n  ", "key-space");
        await Assert.ThrowsAsync<DomainException>(() =>
            service.SendMessageAsync(new UserId(userA.Id), conv.Id, dtoWhitespace));
    }

    [Fact]
    public async Task SendMessageAsync_ContentExceeding4000Chars_ThrowsDomainException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var hugeContent = new string('x', 4001);
        var dto = new SendMessageRequestDto(hugeContent, "key-huge");

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.SendMessageAsync(new UserId(userA.Id), conv.Id, dto));

        Assert.Contains("4000 characters", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SendMessageAsync_EmptyOrWhitespaceIdempotencyKey_ThrowsDomainException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var dto = new SendMessageRequestDto("Valid content", "   ");
        await Assert.ThrowsAsync<DomainException>(() =>
            service.SendMessageAsync(new UserId(userA.Id), conv.Id, dto));
    }

    [Fact]
    public async Task SendMessageAsync_IdempotencyKeyExceeding128Chars_ThrowsDomainException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var longKey = new string('k', 129);
        var dto = new SendMessageRequestDto("Valid content", longKey);
        await Assert.ThrowsAsync<DomainException>(() =>
            service.SendMessageAsync(new UserId(userA.Id), conv.Id, dto));
    }

    [Fact]
    public async Task SendMessageAsync_ReplayWithSameIdempotencyKeyAndSameContent_ReturnsExistingMessageIdempotently()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var dto1 = new SendMessageRequestDto("Hello first time", "idem-key-1");
        var msg1 = await service.SendMessageAsync(new UserId(userA.Id), conv.Id, dto1);

        var dto2 = new SendMessageRequestDto("Hello first time", "idem-key-1");
        var msg2 = await service.SendMessageAsync(new UserId(userA.Id), conv.Id, dto2);

        Assert.NotNull(msg2);
        Assert.Equal(msg1.Id, msg2.Id);
        Assert.Equal(msg1.CreatedAtUtc, msg2.CreatedAtUtc);
        Assert.Equal("Hello first time", msg2.Content);
    }

    [Fact]
    public async Task SendMessageAsync_ConflictingSubmission_ThrowsConflictException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var dto1 = new SendMessageRequestDto("Original text", "idem-key-conflict");
        await service.SendMessageAsync(new UserId(userA.Id), conv.Id, dto1);

        var dto2 = new SendMessageRequestDto("Conflicting different text", "idem-key-conflict");
        await Assert.ThrowsAsync<ConflictException>(() =>
            service.SendMessageAsync(new UserId(userA.Id), conv.Id, dto2));
    }

    [Fact]
    public async Task SendMessageAsync_WithValidReplyTo_PopulatesMessageReplyDto()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var userIdA = new UserId(userA.Id);
        var userIdB = new UserId(userB.Id);

        var conv = await SeedConversationAsync(db, userIdA, userIdB);

        // Bob sends original message
        var parentMsg = await service.SendMessageAsync(
            userIdB,
            conv.Id,
            new SendMessageRequestDto("How are you doing today?", "parent-key-1"));

        // Alice replies
        var replyDto = new SendMessageRequestDto("I am doing great!", "reply-key-1", parentMsg.Id);
        var replyMsg = await service.SendMessageAsync(userIdA, conv.Id, replyDto);

        Assert.NotNull(replyMsg.ReplyTo);
        Assert.Equal(parentMsg.Id, replyMsg.ReplyTo.Id);
        Assert.Equal(userIdB, replyMsg.ReplyTo.SenderId);
        Assert.Equal("Bob", replyMsg.ReplyTo.SenderName);
        Assert.Equal("How are you doing today?", replyMsg.ReplyTo.PreviewContent);
        Assert.False(replyMsg.ReplyTo.IsDeletedForEveryone);
    }

    [Fact]
    public async Task SendMessageAsync_WithReplyToNonExistentMessage_ThrowsNotFoundException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var nonExistentParentId = MessageId.New();
        var dto = new SendMessageRequestDto("Replying to ghost", "reply-ghost", nonExistentParentId);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.SendMessageAsync(new UserId(userA.Id), conv.Id, dto));
    }

    [Fact]
    public async Task SendMessageAsync_WithReplyToMessageInDifferentConversation_ThrowsNotFoundException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var userC = await SeedUserAsync(db, "charlie", "Charlie");

        var conv1 = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));
        var conv2 = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userC.Id));

        // Message in conv1
        var msgInConv1 = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv1.Id,
            new SendMessageRequestDto("Message in Conv 1", "key-conv1"));

        // Attempt to reply to msgInConv1 from inside conv2
        var replyInConv2 = new SendMessageRequestDto("Replying across conversations", "key-cross", msgInConv1.Id);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.SendMessageAsync(new UserId(userA.Id), conv2.Id, replyInConv2));
    }

    [Fact]
    public async Task SendMessageAsync_WithReplyToTombstonedMessage_DisplaysDeletedTombstonePreview()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var userIdA = new UserId(userA.Id);
        var userIdB = new UserId(userB.Id);

        var conv = await SeedConversationAsync(db, userIdA, userIdB);

        // Bob sends message
        var parent = Message.Create(conv.Id, userIdB, "tomb-p-1", "Soon to be deleted", DateTimeOffset.UtcNow);
        db.Messages.Add(parent);
        await db.SaveChangesAsync();

        // Bob deletes message for everyone (tombstone)
        parent.DeleteForEveryone(userIdB, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        // Alice replies to the tombstoned message
        var replyDto = new SendMessageRequestDto("Replying anyway", "reply-tomb-1", parent.Id);
        var replyMsg = await service.SendMessageAsync(userIdA, conv.Id, replyDto);

        Assert.NotNull(replyMsg.ReplyTo);
        Assert.Equal(parent.Id, replyMsg.ReplyTo.Id);
        Assert.True(replyMsg.ReplyTo.IsDeletedForEveryone);
        Assert.Equal("This message was deleted.", replyMsg.ReplyTo.PreviewContent);
    }

    [Fact]
    public async Task GetMessagesAsync_ReturnsMessages_WithDeterministicOrder_And_IsOutgoingFlag()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice Cooper");
        var userB = await SeedUserAsync(db, "bob", "Bob Dylan");
        var userIdA = new UserId(userA.Id);
        var userIdB = new UserId(userB.Id);

        var conv = await SeedConversationAsync(db, userIdA, userIdB);

        var baseTime = DateTimeOffset.UtcNow;
        var msg1 = Message.Create(conv.Id, userIdA, "seq-1", "Msg 1 from Alice", baseTime.AddMinutes(-2));
        var msg2 = Message.Create(conv.Id, userIdB, "seq-2", "Msg 2 from Bob", baseTime.AddMinutes(-1));
        var msg3 = Message.Create(conv.Id, userIdA, "seq-3", "Msg 3 from Alice", baseTime);

        db.Messages.AddRange(msg1, msg2, msg3);
        await db.SaveChangesAsync();

        // Caller is Alice
        var pageAlice = await service.GetMessagesAsync(userIdA, conv.Id, null, pageSize: 10);

        Assert.Equal(3, pageAlice.Messages.Count);
        // Desending order: msg3, msg2, msg1
        Assert.Equal(msg3.Id, pageAlice.Messages[0].Id);
        Assert.True(pageAlice.Messages[0].IsOutgoing);

        Assert.Equal(msg2.Id, pageAlice.Messages[1].Id);
        Assert.False(pageAlice.Messages[1].IsOutgoing); // Bob sent it

        Assert.Equal(msg1.Id, pageAlice.Messages[2].Id);
        Assert.True(pageAlice.Messages[2].IsOutgoing);
    }

    [Fact]
    public async Task GetMessagesAsync_ExcludesMessagesBeforeClearedAtUtc()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var userIdA = new UserId(userA.Id);
        var userIdB = new UserId(userB.Id);

        var conv = await SeedConversationAsync(db, userIdA, userIdB);

        var baseTime = DateTimeOffset.UtcNow;
        var msgOld = Message.Create(conv.Id, userIdB, "clr-1", "Old Message", baseTime.AddMinutes(-10));
        var msgNew = Message.Create(conv.Id, userIdB, "clr-2", "New Message", baseTime);

        db.Messages.AddRange(msgOld, msgNew);

        // User A clears chat at -5 minutes
        var stateA = UserConversationState.Create(conv.Id, userIdA);
        stateA.ClearChat(baseTime.AddMinutes(-5));
        db.UserConversationStates.Add(stateA);

        await db.SaveChangesAsync();

        // Alice views messages
        var page = await service.GetMessagesAsync(userIdA, conv.Id, null, pageSize: 10);

        Assert.Single(page.Messages);
        Assert.Equal(msgNew.Id, page.Messages[0].Id);
    }

    [Fact]
    public async Task GetMessagesAsync_KeysetCursorPagination_AdvancesCorrectly()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var userIdA = new UserId(userA.Id);
        var userIdB = new UserId(userB.Id);

        var conv = await SeedConversationAsync(db, userIdA, userIdB);

        var baseTime = DateTimeOffset.UtcNow;
        for (int i = 0; i < 5; i++)
        {
            var msg = Message.Create(conv.Id, userIdA, $"page-test-{i}", $"Message {i}", baseTime.AddMinutes(i));
            db.Messages.Add(msg);
        }
        await db.SaveChangesAsync();

        // Page 1: pageSize = 2
        var page1 = await service.GetMessagesAsync(userIdA, conv.Id, null, pageSize: 2);
        Assert.Equal(2, page1.Messages.Count);
        Assert.True(page1.HasMore);
        Assert.NotNull(page1.NextCursor);
        Assert.Equal("Message 4", page1.Messages[0].Content);
        Assert.Equal("Message 3", page1.Messages[1].Content);

        // Page 2: with cursor from page 1
        var page2 = await service.GetMessagesAsync(userIdA, conv.Id, page1.NextCursor, pageSize: 2);
        Assert.Equal(2, page2.Messages.Count);
        Assert.True(page2.HasMore);
        Assert.NotNull(page2.NextCursor);
        Assert.Equal("Message 2", page2.Messages[0].Content);
        Assert.Equal("Message 1", page2.Messages[1].Content);

        // Page 3: with cursor from page 2
        var page3 = await service.GetMessagesAsync(userIdA, conv.Id, page2.NextCursor, pageSize: 2);
        Assert.Single(page3.Messages);
        Assert.False(page3.HasMore);
        Assert.Null(page3.NextCursor);
        Assert.Equal("Message 0", page3.Messages[0].Content);
    }

    [Fact]
    public async Task GetMessagesAsync_WithMalformedCursor_ThrowsDomainException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var ex1 = await Assert.ThrowsAsync<DomainException>(() =>
            service.GetMessagesAsync(new UserId(userA.Id), conv.Id, "not-valid-base64!"));
        Assert.Contains("cursor", ex1.Message, StringComparison.OrdinalIgnoreCase);

        var ex2 = await Assert.ThrowsAsync<DomainException>(() =>
            service.GetMessagesAsync(new UserId(userA.Id), conv.Id, Convert.ToBase64String("invalid:colon:count"u8.ToArray())));
        Assert.Contains("cursor", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetMessagesAsync_WhenNonParticipant_ThrowsForbiddenException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var userB = await SeedUserAsync(db, "bob", "Bob");
        var userC = await SeedUserAsync(db, "charlie", "Charlie");

        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetMessagesAsync(new UserId(userC.Id), conv.Id, null));
    }

    [Fact]
    public async Task GetMessagesAsync_WhenConversationNotFound_ThrowsNotFoundException()
    {
        var (_, db, service) = CreateTestEnvironment();

        var userA = await SeedUserAsync(db, "alice", "Alice");
        var nonExistent = ConversationId.New();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetMessagesAsync(new UserId(userA.Id), nonExistent, null));
    }
}
