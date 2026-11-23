using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Converters;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class Phase8MessageInteractionsTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public Phase8MessageInteractionsTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
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

    private (MessageService Service, ApplicationDbContext Db, TestTimeProvider TimeProvider) CreateService(
        DateTimeOffset? initialTime = null)
    {
        var time = initialTime ?? DateTimeOffset.UtcNow;
        var fakeTime = new TestTimeProvider(time);

        var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var messagePersistence = new MessagePersistence(db);
        var conversationPersistence = new ConversationPersistence(db);
        var blockPersistence = new BlockPersistence(db);
        var userReader = new UserReader(db);
        var reactionPersistence = new ReactionPersistence(db);
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<MessageService>>();

        var service = new MessageService(
            messagePersistence,
            conversationPersistence,
            blockPersistence,
            userReader,
            new NullRealtimeNotifier(),
            reactionPersistence,
            logger,
            fakeTime);

        return (service, db, fakeTime);
    }

    private sealed class NullRealtimeNotifier : IRealtimeNotifier
    {
        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ==========================================
    // 1. Message Edit Tests
    // ==========================================

    [Fact]
    public async Task EditMessage_AuthorCanEdit_UpdatesContentAndSetsEditedAtUtc()
    {
        var (service, db, time) = CreateService();
        var userA = await SeedUserAsync(db, "alice_edit", "Alice Edit");
        var userB = await SeedUserAsync(db, "bob_edit", "Bob Edit");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var sendResult = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Original Content", Guid.NewGuid().ToString("N")));

        time.Advance(TimeSpan.FromMinutes(5));

        var editResult = await service.EditMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            sendResult.Id,
            new EditMessageRequestDto("Updated Content"));

        Assert.Equal("Updated Content", editResult.Content);
        Assert.Equal(time.GetUtcNow(), editResult.EditedAtUtc);

        // Verify in DB
        var persisted = await db.Messages.FirstOrDefaultAsync(m => m.ConversationId == conv.Id && m.Id == sendResult.Id);
        Assert.NotNull(persisted);
        Assert.Equal("Updated Content", persisted.Content);
        Assert.Equal(time.GetUtcNow(), persisted.EditedAtUtc);
    }

    [Fact]
    public async Task EditMessage_NonAuthor_ThrowsForbiddenException()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_nonauthor", "Alice");
        var userB = await SeedUserAsync(db, "bob_nonauthor", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var sendResult = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Original Content", Guid.NewGuid().ToString("N")));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.EditMessageAsync(
            new UserId(userB.Id),
            conv.Id,
            sendResult.Id,
            new EditMessageRequestDto("Malicious Edit")));
    }

    [Fact]
    public async Task EditMessage_WhenDeletedForEveryone_ThrowsForbiddenException()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_edittomb", "Alice");
        var userB = await SeedUserAsync(db, "bob_edittomb", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var sendResult = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Original Content", Guid.NewGuid().ToString("N")));

        await service.DeleteForEveryoneAsync(new UserId(userA.Id), conv.Id, sendResult.Id);

        await Assert.ThrowsAsync<ForbiddenException>(() => service.EditMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            sendResult.Id,
            new EditMessageRequestDto("Should Fail")));
    }

    [Fact]
    public async Task EditMessage_InvalidContent_ThrowsDomainException()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_invcontent", "Alice");
        var userB = await SeedUserAsync(db, "bob_invcontent", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var sendResult = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Original Content", Guid.NewGuid().ToString("N")));

        await Assert.ThrowsAsync<DomainException>(() => service.EditMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            sendResult.Id,
            new EditMessageRequestDto("   ")));

        await Assert.ThrowsAsync<DomainException>(() => service.EditMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            sendResult.Id,
            new EditMessageRequestDto(new string('x', 4001))));
    }

    // ==========================================
    // 2. Delete for Me Tests
    // ==========================================

    [Fact]
    public async Task DeleteForMe_HidesMessageForCaller_RemainsVisibleToPeer()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_delme", "Alice");
        var userB = await SeedUserAsync(db, "bob_delme", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var msg1 = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Message 1", Guid.NewGuid().ToString("N")));

        var msg2 = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Message 2", Guid.NewGuid().ToString("N")));

        // Alice hides msg1 for herself
        await service.DeleteForMeAsync(new UserId(userA.Id), conv.Id, msg1.Id);

        // Keyset fetch for Alice should only return msg2
        var alicePage = await service.GetMessagesAsync(new UserId(userA.Id), conv.Id, null);
        Assert.Single(alicePage.Messages);
        Assert.Equal(msg2.Id, alicePage.Messages[0].Id);

        // Keyset fetch for Bob should return both messages
        var bobPage = await service.GetMessagesAsync(new UserId(userB.Id), conv.Id, null);
        Assert.Equal(2, bobPage.Messages.Count);
        Assert.Contains(bobPage.Messages, m => m.Id == msg1.Id);
        Assert.Contains(bobPage.Messages, m => m.Id == msg2.Id);
    }

    // ==========================================
    // 3. Delete for Everyone Tests
    // ==========================================

    [Fact]
    public async Task DeleteForEveryone_Within3Hours_ScrubsContentAndDeletesReactions()
    {
        var (service, db, time) = CreateService();
        var userA = await SeedUserAsync(db, "alice_delforeveryone", "Alice");
        var userB = await SeedUserAsync(db, "bob_delforeveryone", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var msg = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Secret Message", Guid.NewGuid().ToString("N")));

        // Add a reaction from Bob
        await service.ToggleReactionAsync(new UserId(userB.Id), conv.Id, msg.Id, new ToggleReactionRequestDto("❤️"));

        // Advance 2 hours (within 3 hour limit)
        time.Advance(TimeSpan.FromHours(2));

        var delResult = await service.DeleteForEveryoneAsync(new UserId(userA.Id), conv.Id, msg.Id);
        Assert.Equal(conv.Id.Value, delResult.ConversationId);
        Assert.Equal(msg.Id.Value, delResult.MessageId);

        // Check SQL Server persistence directly
        var messageInDb = await db.Messages.FirstOrDefaultAsync(m => m.ConversationId == conv.Id && m.Id == msg.Id);
        Assert.NotNull(messageInDb);
        Assert.Null(messageInDb.Content); // Physically NULL'd in SQL Server!
        Assert.True(messageInDb.IsDeletedForEveryone);
        Assert.NotNull(messageInDb.DeletedForEveryoneAtUtc);
        Assert.Null(messageInDb.EditedAtUtc);

        // Verify reactions deleted from DB
        var reactionsInDb = await db.MessageReactions
            .Where(r => r.ConversationId == conv.Id && r.MessageId == msg.Id)
            .ToListAsync();
        Assert.Empty(reactionsInDb);

        // Fetching page displays tombstone
        var page = await service.GetMessagesAsync(new UserId(userA.Id), conv.Id, null);
        Assert.Single(page.Messages);
        Assert.True(page.Messages[0].IsDeletedForEveryone);
        Assert.Equal(string.Empty, page.Messages[0].Content);
        Assert.Empty(page.Messages[0].Reactions);

        // Fresh-DbContext proof bypassing EF tracked identity map
        using var verifyScope = _fixture.CreateScope();
        var freshDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var freshMsg = await freshDb.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ConversationId == conv.Id && m.Id == msg.Id);
        Assert.NotNull(freshMsg);
        Assert.Null(freshMsg.Content);
        Assert.True(freshMsg.IsDeletedForEveryone);
        Assert.Null(freshMsg.EditedAtUtc);

        var freshReactions = await freshDb.MessageReactions
            .AsNoTracking()
            .Where(r => r.ConversationId == conv.Id && r.MessageId == msg.Id)
            .ToListAsync();
        Assert.Empty(freshReactions);
    }

    [Fact]
    public async Task DeleteForEveryone_Beyond3Hours_ThrowsForbiddenException()
    {
        var (service, db, time) = CreateService();
        var userA = await SeedUserAsync(db, "alice_del3h", "Alice");
        var userB = await SeedUserAsync(db, "bob_del3h", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var msg = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Old Message", Guid.NewGuid().ToString("N")));

        // Advance 3 hours and 1 minute
        time.Advance(TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(1)));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.DeleteForEveryoneAsync(
            new UserId(userA.Id),
            conv.Id,
            msg.Id));
    }

    [Fact]
    public async Task DeleteForEveryone_NonAuthor_ThrowsForbiddenException()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_delnonauth", "Alice");
        var userB = await SeedUserAsync(db, "bob_delnonauth", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var msg = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Author is Alice", Guid.NewGuid().ToString("N")));

        await Assert.ThrowsAsync<ForbiddenException>(() => service.DeleteForEveryoneAsync(
            new UserId(userB.Id),
            conv.Id,
            msg.Id));
    }

    // ==========================================
    // 4. Replies Tests
    // ==========================================

    [Fact]
    public async Task Replies_SameConversation_HydratesReplyPreview()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_reply", "Alice A");
        var userB = await SeedUserAsync(db, "bob_reply", "Bob B");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var parentMsg = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Question to answer", Guid.NewGuid().ToString("N")));

        var replyMsg = await service.SendMessageAsync(
            new UserId(userB.Id),
            conv.Id,
            new SendMessageRequestDto("Here is the answer", Guid.NewGuid().ToString("N"), parentMsg.Id));

        Assert.NotNull(replyMsg.ReplyTo);
        Assert.Equal(parentMsg.Id.Value, replyMsg.ReplyTo.MessageId);
        Assert.Equal(new UserId(userA.Id), replyMsg.ReplyTo.SenderId);
        Assert.Equal("Alice A", replyMsg.ReplyTo.SenderDisplayName);
        Assert.Equal("Question to answer", replyMsg.ReplyTo.ContentPreview);
        Assert.False(replyMsg.ReplyTo.IsDeletedForEveryone);
    }

    [Fact]
    public async Task Replies_ToDeletedForEveryoneMessage_RendersTombstonePreview()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_reptomb", "Alice");
        var userB = await SeedUserAsync(db, "bob_reptomb", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var parentMsg = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("Will be deleted", Guid.NewGuid().ToString("N")));

        var replyMsg = await service.SendMessageAsync(
            new UserId(userB.Id),
            conv.Id,
            new SendMessageRequestDto("Replying to soon-deleted", Guid.NewGuid().ToString("N"), parentMsg.Id));

        // Alice deletes parent for everyone
        await service.DeleteForEveryoneAsync(new UserId(userA.Id), conv.Id, parentMsg.Id);

        // Fetch page again - reply preview should show tombstone text
        var page = await service.GetMessagesAsync(new UserId(userB.Id), conv.Id, null);
        var replyInPage = page.Messages.First(m => m.Id == replyMsg.Id);
        Assert.NotNull(replyInPage.ReplyTo);
        Assert.True(replyInPage.ReplyTo.IsDeletedForEveryone);
        Assert.Equal("This message was deleted.", replyInPage.ReplyTo.ContentPreview);
    }

    [Fact]
    public async Task Replies_CrossConversation_ThrowsNotFoundException()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_crossrep", "Alice");
        var userB = await SeedUserAsync(db, "bob_crossrep", "Bob");
        var userC = await SeedUserAsync(db, "charlie_crossrep", "Charlie");
        var conv1 = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));
        var conv2 = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userC.Id));

        var parentInConv1 = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv1.Id,
            new SendMessageRequestDto("Message in conv 1", Guid.NewGuid().ToString("N")));

        // Attempt to reply in conv2 pointing to parent from conv1
        await Assert.ThrowsAsync<NotFoundException>(() => service.SendMessageAsync(
            new UserId(userA.Id),
            conv2.Id,
            new SendMessageRequestDto("Illegal cross-conv reply", Guid.NewGuid().ToString("N"), parentInConv1.Id)));
    }

    // ==========================================
    // 5. Reactions Tests
    // ==========================================

    [Fact]
    public async Task Reactions_Allowlist_TogglesAndSwitchesEmoji()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_rx", "Alice");
        var userB = await SeedUserAsync(db, "bob_rx", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var msg = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("React to me", Guid.NewGuid().ToString("N")));

        // Bob reacts with thumbs up
        var r1 = await service.ToggleReactionAsync(new UserId(userB.Id), conv.Id, msg.Id, new ToggleReactionRequestDto("👍"));
        Assert.Equal("👍", r1.Emoji);

        var page1 = await service.GetMessagesAsync(new UserId(userB.Id), conv.Id, null);
        var msgSummary1 = page1.Messages[0];
        Assert.Single(msgSummary1.Reactions);
        Assert.Equal("👍", msgSummary1.Reactions[0].Emoji);
        Assert.Equal(1, msgSummary1.Reactions[0].Count);
        Assert.True(msgSummary1.Reactions[0].ReactedByCurrentUser);

        // Bob reacts with heart -> switches emoji
        var r2 = await service.ToggleReactionAsync(new UserId(userB.Id), conv.Id, msg.Id, new ToggleReactionRequestDto("❤️"));
        Assert.Equal("❤️", r2.Emoji);

        var page2 = await service.GetMessagesAsync(new UserId(userB.Id), conv.Id, null);
        var msgSummary2 = page2.Messages[0];
        Assert.Single(msgSummary2.Reactions);
        Assert.Equal("❤️", msgSummary2.Reactions[0].Emoji);

        // Bob reacts with heart again -> toggles off (removes)
        var r3 = await service.ToggleReactionAsync(new UserId(userB.Id), conv.Id, msg.Id, new ToggleReactionRequestDto("❤️"));
        Assert.Null(r3.Emoji);

        var page3 = await service.GetMessagesAsync(new UserId(userB.Id), conv.Id, null);
        Assert.Empty(page3.Messages[0].Reactions);
    }

    [Fact]
    public async Task Reactions_InvalidEmoji_ThrowsDomainException()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_invalrx", "Alice");
        var userB = await SeedUserAsync(db, "bob_invalrx", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var msg = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("React test", Guid.NewGuid().ToString("N")));

        await Assert.ThrowsAsync<DomainException>(() => service.ToggleReactionAsync(
            new UserId(userB.Id),
            conv.Id,
            msg.Id,
            new ToggleReactionRequestDto("💩")));

        await Assert.ThrowsAsync<DomainException>(() => service.ToggleReactionAsync(
            new UserId(userB.Id),
            conv.Id,
            msg.Id,
            new ToggleReactionRequestDto("<script>alert(1)</script>")));
    }

    [Fact]
    public async Task Reactions_OnTombstonedMessage_ThrowsForbiddenException()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_rxtomb", "Alice");
        var userB = await SeedUserAsync(db, "bob_rxtomb", "Bob");
        var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));

        var msg = await service.SendMessageAsync(
            new UserId(userA.Id),
            conv.Id,
            new SendMessageRequestDto("React test", Guid.NewGuid().ToString("N")));

        await service.DeleteForEveryoneAsync(new UserId(userA.Id), conv.Id, msg.Id);

        await Assert.ThrowsAsync<ForbiddenException>(() => service.ToggleReactionAsync(
            new UserId(userB.Id),
            conv.Id,
            msg.Id,
            new ToggleReactionRequestDto("👍")));
    }

    // ==========================================
    // 6. Security & Controller Metadata Tests
    // ==========================================

    [Theory]
    [InlineData(nameof(MessagesController.EditMessage), typeof(HttpPutAttribute))]
    [InlineData(nameof(MessagesController.DeleteForMe), typeof(HttpPostAttribute))]
    [InlineData(nameof(MessagesController.DeleteForEveryone), typeof(HttpPostAttribute))]
    [InlineData(nameof(MessagesController.ToggleReaction), typeof(HttpPostAttribute))]
    [InlineData(nameof(MessagesController.RemoveReaction), typeof(HttpDeleteAttribute))]
    public void Controller_Phase8Endpoints_HaveCorrectAttributes(string actionName, Type httpVerbAttributeType)
    {
        var method = typeof(MessagesController).GetMethod(actionName);
        Assert.NotNull(method);

        var verbAttr = method.GetCustomAttribute(httpVerbAttributeType);
        Assert.NotNull(verbAttr);

        var rateLimitAttr = method.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimitAttr);
        Assert.Equal("messages", rateLimitAttr.PolicyName);
    }

    [Fact]
    public void Controller_HasAutoValidateAntiforgeryToken_OrAuthorize()
    {
        var controllerType = typeof(MessagesController);
        var authAttr = controllerType.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(authAttr);

        var antiforgeryAttr = controllerType.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>();
        Assert.NotNull(antiforgeryAttr);
    }

    // ==========================================
    // 7. View & CSP HTML Assertions
    // ==========================================

    [Fact]
    public void ConversationCshtml_HasZeroInlineStyles_AndRequiredModalIds()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.slnx"))
                               && !File.Exists(Path.Combine(current.FullName, "AOS.Messenger.sln"))
                               && !Directory.Exists(Path.Combine(current.FullName, "src", "AOS.Messenger.Web")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var solutionDir = current.FullName;
        var conversationViewPath = Path.Combine(solutionDir, "src", "AOS.Messenger.Web", "Views", "Chat", "Conversation.cshtml");
        var modalsPartialPath = Path.Combine(solutionDir, "src", "AOS.Messenger.Web", "Views", "Chat", "_ChatModals.cshtml");
        Assert.True(File.Exists(conversationViewPath), $"Conversation.cshtml not found at {conversationViewPath}");

        var html = File.ReadAllText(conversationViewPath)
            + (File.Exists(modalsPartialPath) ? "\n" + File.ReadAllText(modalsPartialPath) : string.Empty);

        // Strict CSP: Zero inline style="..." attributes
        var inlineStyleMatches = Regex.Matches(html, @"\sstyle\s*=\s*""[^""]*""", RegexOptions.IgnoreCase);
        Assert.Empty(inlineStyleMatches);

        // Zero <style> tags
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);

        // Required interactive modal / popover elements
        Assert.Contains("id=\"edit-message-modal\"", html);
        Assert.Contains("id=\"delete-message-modal\"", html);
        Assert.Contains("id=\"reaction-picker-popover\"", html);
    }

    // ==========================================
    // 8. Phase 8 Review Remediation Regression Tests
    // ==========================================

    private sealed class CapturingPhase8Notifier : IRealtimeNotifier
    {
        public List<MessageEditedDto> Edited { get; } = [];
        public List<MessageDeletedForEveryoneDto> DeletedForEveryone { get; } = [];
        public List<(UserId CallerUserId, MessageDeletedForMeDto Message)> DeletedForMe { get; } = [];
        public List<ReactionUpdatedDto> ReactionUpdates { get; } = [];

        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;

        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default)
        {
            Edited.Add(message);
            return Task.CompletedTask;
        }

        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default)
        {
            DeletedForEveryone.Add(message);
            return Task.CompletedTask;
        }

        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default)
        {
            DeletedForMe.Add((callerUserId, message));
            return Task.CompletedTask;
        }

        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default)
        {
            ReactionUpdates.Add(reaction);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ConcurrentEditAndDeleteForEveryone_ThrowsConflictException()
    {
        using var scope1 = _fixture.CreateScope();
        using var scope2 = _fixture.CreateScope();

        var db1 = scope1.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var userA = await SeedUserAsync(db1, "alice_concedit", "Alice");
        var userB = await SeedUserAsync(db1, "bob_concedit", "Bob");
        var conv = await SeedConversationAsync(db1, new UserId(userA.Id), new UserId(userB.Id));

        var time = new TestTimeProvider(DateTimeOffset.UtcNow);
        var persistence1 = new MessagePersistence(db1);
        var persistence2 = new MessagePersistence(db2);

        var msg = Message.Create(conv.Id, new UserId(userA.Id), Guid.NewGuid().ToString("N"), "Initial", time.GetUtcNow());
        var added = await persistence1.AddAsync(msg);

        // Load stale tracked copy in scope 2
        var staleMsg = await persistence2.GetByIdAsync(conv.Id, added.Message.Id);
        Assert.NotNull(staleMsg);

        // Scope 1 deletes the message (advancing SQL Server rowversion)
        await persistence1.DeleteForEveryoneTransactionalAsync(conv.Id, added.Message.Id, time.GetUtcNow().AddSeconds(10));

        // Scope 2 attempts to persist an edit on stale RowVersion -> must throw ConflictException (409), not DbUpdateConcurrencyException
        staleMsg.Edit("Conflicting Edit", time.GetUtcNow().AddSeconds(20));
        await Assert.ThrowsAsync<ConflictException>(() => persistence2.UpdateAsync(staleMsg));
    }

    [Fact]
    public async Task RemoveReaction_EnforcesBlockTombstoneCutoffDeletedForMeAndCrossConversation()
    {
        var (service, db, time) = CreateService();
        var userA = await SeedUserAsync(db, "alice_remrx", "Alice");
        var userB = await SeedUserAsync(db, "bob_remrx", "Bob");
        var userC = await SeedUserAsync(db, "charlie_remrx", "Charlie");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var idC = new UserId(userC.Id);

        var conv = await SeedConversationAsync(db, idA, idB);
        var convOther = await SeedConversationAsync(db, idA, idC);

        var msg = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("React target", Guid.NewGuid().ToString("N")));
        var msgHidden = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Hidden target", Guid.NewGuid().ToString("N")));
        var msgTomb = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Tomb target", Guid.NewGuid().ToString("N")));
        var msgOther = await service.SendMessageAsync(idA, convOther.Id, new SendMessageRequestDto("Other conv", Guid.NewGuid().ToString("N")));

        await service.ToggleReactionAsync(idB, conv.Id, msg.Id, new ToggleReactionRequestDto("❤️"));
        await service.ToggleReactionAsync(idA, conv.Id, msg.Id, new ToggleReactionRequestDto("👍"));

        // Happy path: removes only caller's reaction and returns remaining group state
        var removed = await service.RemoveReactionAsync(idB, conv.Id, msg.Id);
        Assert.Null(removed.Emoji);
        Assert.NotNull(removed.Reactions);
        var remaining = Assert.Single(removed.Reactions);
        Assert.Equal("👍", remaining.Emoji);
        Assert.Contains(idA.Value, remaining.UserIds);
        Assert.DoesNotContain(idB.Value, remaining.UserIds);

        // Cross-conversation check
        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveReactionAsync(idB, conv.Id, msgOther.Id));

        // Tombstoned check
        await service.DeleteForEveryoneAsync(idA, conv.Id, msgTomb.Id);
        await Assert.ThrowsAsync<ForbiddenException>(() => service.RemoveReactionAsync(idB, conv.Id, msgTomb.Id));

        // DeleteForMe check
        await service.DeleteForMeAsync(idB, conv.Id, msgHidden.Id);
        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveReactionAsync(idB, conv.Id, msgHidden.Id));

        // ClearedAtUtc cutoff check
        time.Advance(TimeSpan.FromMinutes(5));
        var stateB = UserConversationState.Create(conv.Id, idB);
        stateB.ClearChat(time.GetUtcNow());
        db.UserConversationStates.Add(stateB);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveReactionAsync(idB, conv.Id, msg.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ToggleReactionAsync(idB, conv.Id, msg.Id, new ToggleReactionRequestDto("🎉")));

        // Bidirectional block check
        db.UserBlocks.Add(UserBlock.Create(idA, idB, time.GetUtcNow()));
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ForbiddenException>(() => service.RemoveReactionAsync(idB, conv.Id, msg.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.ToggleReactionAsync(idB, conv.Id, msg.Id, new ToggleReactionRequestDto("🎉")));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.EditMessageAsync(idA, conv.Id, msg.Id, new EditMessageRequestDto("Blocked edit")));
    }

    [Fact]
    public async Task EditMessage_WhenBeforeCutoffOrDeletedForMe_ThrowsNotFoundException()
    {
        var (service, db, time) = CreateService();
        var userA = await SeedUserAsync(db, "alice_editvis", "Alice");
        var userB = await SeedUserAsync(db, "bob_editvis", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        var m1 = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Msg 1", Guid.NewGuid().ToString("N")));
        var m2 = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Msg 2", Guid.NewGuid().ToString("N")));

        // Hide m1 via DeleteForMe
        await service.DeleteForMeAsync(idA, conv.Id, m1.Id);
        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.EditMessageAsync(idA, conv.Id, m1.Id, new EditMessageRequestDto("Edit hidden")));

        // Advance time and clear chat before editing m2
        time.Advance(TimeSpan.FromMinutes(2));
        var stateA = UserConversationState.Create(conv.Id, idA);
        stateA.ClearChat(time.GetUtcNow());
        db.UserConversationStates.Add(stateA);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.EditMessageAsync(idA, conv.Id, m2.Id, new EditMessageRequestDto("Edit cleared")));
    }

    [Fact]
    public async Task DeleteForMe_ExcludesDeletedMessagesFromUnreadCounts()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_unreaddelme", "Alice");
        var userB = await SeedUserAsync(db, "bob_unreaddelme", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        var m1 = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Msg 1", Guid.NewGuid().ToString("N")));
        var m2 = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Msg 2", Guid.NewGuid().ToString("N")));

        // Bob hides m1 via DeleteForMe
        await service.DeleteForMeAsync(idB, conv.Id, m1.Id);

        var convRepo = new ConversationPersistence(db);
        var singleUnread = await convRepo.GetUnreadCountAsync(conv.Id, idB);
        var batchUnread = await convRepo.GetUnreadCountsForConversationsAsync(
            idB,
            [(conv.Id, (MessageId?)null, (DateTimeOffset?)null)]);

        Assert.Equal(1, singleUnread);
        Assert.Equal(1, batchUnread[conv.Id]);
    }

    [Fact]
    public async Task GetMessages_ReplyParentBeforeClearedCutoff_SuppressesReplyPreview()
    {
        var (service, db, time) = CreateService();
        var userA = await SeedUserAsync(db, "alice_repcutoff", "Alice");
        var userB = await SeedUserAsync(db, "bob_repcutoff", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        var parentMsg = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Secret pre-clear text", Guid.NewGuid().ToString("N")));
        time.Advance(TimeSpan.FromMinutes(5));

        var clearedAt = time.GetUtcNow();
        var stateA = UserConversationState.Create(conv.Id, idA);
        stateA.ClearChat(clearedAt);
        db.UserConversationStates.Add(stateA);
        await db.SaveChangesAsync();

        time.Advance(TimeSpan.FromMinutes(1));
        var childMsg = await service.SendMessageAsync(idB, conv.Id, new SendMessageRequestDto("Post-clear reply", Guid.NewGuid().ToString("N"), parentMsg.Id));

        var alicePage = await service.GetMessagesAsync(idA, conv.Id, cursor: null);
        var fetchedChild = Assert.Single(alicePage.Messages);
        Assert.Equal(childMsg.Id, fetchedChild.Id);
        Assert.Null(fetchedChild.ReplyTo);
    }

    [Fact]
    public async Task Phase8Mutations_BroadcastRealtimeNotificationsPostCommit()
    {
        var fakeTime = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notifier = new CapturingPhase8Notifier();
        var service = new MessageService(
            new MessagePersistence(db),
            new ConversationPersistence(db),
            new BlockPersistence(db),
            new UserReader(db),
            notifier,
            new ReactionPersistence(db),
            scope.ServiceProvider.GetRequiredService<ILogger<MessageService>>(),
            fakeTime);

        var userA = await SeedUserAsync(db, "alice_rt", "Alice");
        var userB = await SeedUserAsync(db, "bob_rt", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        var msg = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Hello", Guid.NewGuid().ToString("N")));
        await service.EditMessageAsync(idA, conv.Id, msg.Id, new EditMessageRequestDto("Edited"));
        await service.ToggleReactionAsync(idB, conv.Id, msg.Id, new ToggleReactionRequestDto("🔥"));
        await service.RemoveReactionAsync(idB, conv.Id, msg.Id);
        await service.DeleteForEveryoneAsync(idA, conv.Id, msg.Id);

        Assert.Single(notifier.Edited);
        Assert.Equal(2, notifier.ReactionUpdates.Count);
        Assert.NotNull(notifier.ReactionUpdates[0].Reactions);
        Assert.Single(notifier.ReactionUpdates[0].Reactions!);
        Assert.NotNull(notifier.ReactionUpdates[1].Reactions);
        Assert.Empty(notifier.ReactionUpdates[1].Reactions!);
        Assert.Single(notifier.DeletedForEveryone);
    }

    [Fact]
    public void SignalRJsonProtocol_RegistersStronglyTypedIdConverters_AndRateLimiterAfterAuth()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("ConnectionStrings:DefaultConnection", _fixture.ConnectionString);
                builder.UseSetting("PublicBaseUrl", "https://localhost:7001");
            });

        var signalROptions = factory.Services.GetRequiredService<IOptions<JsonHubProtocolOptions>>().Value;
        var converters = signalROptions.PayloadSerializerOptions.Converters;

        Assert.Contains(converters, c => c is MessageIdJsonConverter);
        Assert.Contains(converters, c => c is UserIdJsonConverter);
        Assert.Contains(converters, c => c is ConversationIdJsonConverter);

        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null && !Directory.Exists(Path.Combine(current.FullName, "src", "AOS.Messenger.Web")))
        {
            current = current.Parent;
        }

        Assert.NotNull(current);
        var programCs = File.ReadAllText(Path.Combine(current.FullName, "src", "AOS.Messenger.Web", "Program.cs"));
        var authIdx = programCs.IndexOf("app.UseAuthentication();", StringComparison.Ordinal);
        var rateIdx = programCs.IndexOf("app.UseRateLimiter();", StringComparison.Ordinal);
        var authzIdx = programCs.IndexOf("app.UseAuthorization();", StringComparison.Ordinal);

        Assert.True(authIdx > 0 && rateIdx > authIdx && authzIdx > rateIdx,
            "UseRateLimiter must run after UseAuthentication and before UseAuthorization.");
    }

    // ==========================================
    // 9. Hardened Adversarial Red-Team Suites (1-6)
    // ==========================================

    private sealed class Phase8TestCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated => Id is not null;
    }

    private sealed class Phase8TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        private static Guid _userId;
        private static string _userName = string.Empty;
        private static string _displayName = string.Empty;
        private static Guid _sessionId;
        private static string _email = string.Empty;

        public static void SetUser(Guid userId, string userName, string displayName, Guid sessionId, string email)
        {
            _userId = userId;
            _userName = userName;
            _displayName = displayName;
            _sessionId = sessionId;
            _email = email;
        }

        public static void ClearUser()
        {
            _userId = Guid.Empty;
            _userName = string.Empty;
            _displayName = string.Empty;
            _sessionId = Guid.Empty;
            _email = string.Empty;
        }

        public Phase8TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (_userId == Guid.Empty)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, _userId.ToString()),
                new Claim(ClaimTypes.Name, _userName),
                new Claim("DisplayName", _displayName),
                new Claim("SessionId", _sessionId.ToString()),
                new Claim(ClaimTypes.Email, _email)
            };
            var identity = new ClaimsIdentity(claims, "Phase8TestScheme");
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, "Phase8TestScheme");
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }

    [Fact]
    public async Task Phase8HttpEndpoints_MissingCsrfToken_AllReturnBadRequest()
    {
        ConversationId convId;
        MessageId msgId;

        using (var seedScope = _fixture.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userA = await SeedUserAsync(db, "alice_csrfp8", "Alice");
            var userB = await SeedUserAsync(db, "bob_csrfp8", "Bob");
            var conv = await SeedConversationAsync(db, new UserId(userA.Id), new UserId(userB.Id));
            convId = conv.Id;

            var msg = Message.Create(convId, new UserId(userA.Id), Guid.NewGuid().ToString("N"), "CSRF target", DateTimeOffset.UtcNow);
            db.Messages.Add(msg);

            var sessionId = Guid.NewGuid();
            db.UserSessions.Add(UserSession.Create(sessionId, userA.Id, "Test", "127.0.0.1", "TestAgent", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)));
            await db.SaveChangesAsync();
            msgId = msg.Id;

            Phase8TestAuthHandler.SetUser(userA.Id, userA.UserName!, userA.DisplayName!, sessionId, userA.Email!);
        }

        try
        {
            using var factory = new WebApplicationFactory<Program>()
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
                    builder.ConfigureTestServices(services =>
                    {
                        services.PostConfigure<AuthenticationOptions>(options =>
                        {
                            options.DefaultAuthenticateScheme = "Phase8TestScheme";
                            options.DefaultChallengeScheme = "Phase8TestScheme";
                            options.DefaultScheme = "Phase8TestScheme";
                        });
                        services.AddAuthentication()
                            .AddScheme<AuthenticationSchemeOptions, Phase8TestAuthHandler>("Phase8TestScheme", _ => { });
                    });
                });

            var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://localhost"),
                AllowAutoRedirect = false
            });

            var baseUri = $"/api/conversations/{convId.Value}/messages/{msgId.Value}";

            var editResp = await client.PutAsJsonAsync(baseUri, new EditMessageRequestDto("No CSRF"));
            Assert.Equal(HttpStatusCode.BadRequest, editResp.StatusCode);

            var deleteForMeResp = await client.PostAsync($"{baseUri}/delete-for-me", null);
            Assert.Equal(HttpStatusCode.BadRequest, deleteForMeResp.StatusCode);

            var deleteForEveryoneResp = await client.PostAsync($"{baseUri}/delete-for-everyone", null);
            Assert.Equal(HttpStatusCode.BadRequest, deleteForEveryoneResp.StatusCode);

            var reactResp = await client.PostAsJsonAsync($"{baseUri}/reactions", new ToggleReactionRequestDto("👍"));
            Assert.Equal(HttpStatusCode.BadRequest, reactResp.StatusCode);

            var unreactResp = await client.DeleteAsync($"{baseUri}/reactions");
            Assert.Equal(HttpStatusCode.BadRequest, unreactResp.StatusCode);
        }
        finally
        {
            Phase8TestAuthHandler.ClearUser();
        }
    }

    [Fact]
    public async Task DeleteForEveryone_ExactThreeHourBoundaries_EnforcedDeterministically()
    {
        var t0 = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var (service, db, time) = CreateService(t0);
        var userA = await SeedUserAsync(db, "alice_3hb", "Alice");
        var userB = await SeedUserAsync(db, "bob_3hb", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        var msgBeforeBoundary = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Before 3h", Guid.NewGuid().ToString("N")));
        var msgExactBoundary = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Exact 3h", Guid.NewGuid().ToString("N")));
        var msgPastBoundary = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Past 3h", Guid.NewGuid().ToString("N")));

        // +2h 59m 59s -> allowed
        time.SetUtcNow(t0.AddHours(3).AddSeconds(-1));
        var beforeResult = await service.DeleteForEveryoneAsync(idA, conv.Id, msgBeforeBoundary.Id);
        Assert.Equal(msgBeforeBoundary.Id.Value, beforeResult.MessageId);

        // +3h 00m 00s -> allowed
        time.SetUtcNow(t0.AddHours(3));
        var exactResult = await service.DeleteForEveryoneAsync(idA, conv.Id, msgExactBoundary.Id);
        Assert.Equal(msgExactBoundary.Id.Value, exactResult.MessageId);

        // +3h 00m 01s -> rejected
        time.SetUtcNow(t0.AddHours(3).AddSeconds(1));
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.DeleteForEveryoneAsync(idA, conv.Id, msgPastBoundary.Id));
    }

    [Fact]
    public async Task Phase8Operations_CrossConversationIdor_AndNonParticipant_AreRejectedUniformly()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_idor", "Alice");
        var userB = await SeedUserAsync(db, "bob_idor", "Bob");
        var userC = await SeedUserAsync(db, "charlie_idor", "Charlie");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var idC = new UserId(userC.Id);

        var conv1 = await SeedConversationAsync(db, idA, idB);
        var conv2 = await SeedConversationAsync(db, idA, idC);

        var msgInConv2 = await service.SendMessageAsync(idA, conv2.Id, new SendMessageRequestDto("Conv2 secret", Guid.NewGuid().ToString("N")));

        // Alice (participant in conv1) supplies conv1 + msgInConv2 across ALL 5 endpoints + SendMessage reply
        await Assert.ThrowsAsync<NotFoundException>(() => service.EditMessageAsync(idA, conv1.Id, msgInConv2.Id, new EditMessageRequestDto("IDOR edit")));
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteForMeAsync(idA, conv1.Id, msgInConv2.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteForEveryoneAsync(idA, conv1.Id, msgInConv2.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ToggleReactionAsync(idA, conv1.Id, msgInConv2.Id, new ToggleReactionRequestDto("👍")));
        await Assert.ThrowsAsync<NotFoundException>(() => service.RemoveReactionAsync(idA, conv1.Id, msgInConv2.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.SendMessageAsync(idA, conv1.Id, new SendMessageRequestDto("IDOR reply", Guid.NewGuid().ToString("N"), msgInConv2.Id)));

        // Non-participant Bob targeting conv2 receives uniform ForbiddenException without leaking existence
        await Assert.ThrowsAsync<ForbiddenException>(() => service.EditMessageAsync(idB, conv2.Id, msgInConv2.Id, new EditMessageRequestDto("Non-member")));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.DeleteForMeAsync(idB, conv2.Id, msgInConv2.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.DeleteForEveryoneAsync(idB, conv2.Id, msgInConv2.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.ToggleReactionAsync(idB, conv2.Id, msgInConv2.Id, new ToggleReactionRequestDto("👍")));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.RemoveReactionAsync(idB, conv2.Id, msgInConv2.Id));
        await Assert.ThrowsAsync<ForbiddenException>(() => service.SendMessageAsync(idB, conv2.Id, new SendMessageRequestDto("Non-member reply", Guid.NewGuid().ToString("N"), msgInConv2.Id)));
    }

    [Fact]
    public async Task Concurrency_ToggleReaction_DoubleClick_AndDeleteForMe_SucceedWithoutUnhandledExceptions()
    {
        ConversationId convId;
        MessageId msgId;
        UserId idA;
        UserId idB;

        using (var seedScope = _fixture.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userA = await SeedUserAsync(db, "alice_conc", "Alice");
            var userB = await SeedUserAsync(db, "bob_conc", "Bob");
            idA = new UserId(userA.Id);
            idB = new UserId(userB.Id);
            var conv = await SeedConversationAsync(db, idA, idB);
            convId = conv.Id;

            var msg = Message.Create(convId, idA, Guid.NewGuid().ToString("N"), "Concurrency target", DateTimeOffset.UtcNow);
            db.Messages.Add(msg);
            await db.SaveChangesAsync();
            msgId = msg.Id;
        }

        for (var round = 0; round < 3; round++)
        {
            using var scope1 = _fixture.CreateScope();
            using var scope2 = _fixture.CreateScope();

            var s1 = CreateScopedMessageService(scope1.ServiceProvider);
            var s2 = CreateScopedMessageService(scope2.ServiceProvider);

            await Task.WhenAll(
                s1.ToggleReactionAsync(idB, convId, msgId, new ToggleReactionRequestDto("👍")),
                s2.ToggleReactionAsync(idB, convId, msgId, new ToggleReactionRequestDto("👍")));
        }

        using (var scope1 = _fixture.CreateScope())
        using (var scope2 = _fixture.CreateScope())
        {
            var s1 = CreateScopedMessageService(scope1.ServiceProvider);
            var s2 = CreateScopedMessageService(scope2.ServiceProvider);

            await Task.WhenAll(
                s1.DeleteForMeAsync(idB, convId, msgId),
                s2.DeleteForMeAsync(idB, convId, msgId));
        }

        using var verifyScope = _fixture.CreateScope();
        var freshDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var reactions = await freshDb.MessageReactions.AsNoTracking()
            .Where(r => r.ConversationId == convId && r.MessageId == msgId && r.UserId == idB)
            .ToListAsync();
        Assert.True(reactions.Count <= 1);

        var deletions = await freshDb.UserMessageDeletions.AsNoTracking()
            .Where(d => d.ConversationId == convId && d.MessageId == msgId && d.UserId == idB)
            .ToListAsync();
        Assert.Single(deletions);
    }

    [Fact]
    public async Task Concurrency_DeleteForEveryone_Vs_PeerToggleReaction_NeverLeavesOrphanedReaction()
    {
        UserId idA;
        UserId idB;
        ConversationId convId;

        using (var seedScope = _fixture.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var userA = await SeedUserAsync(db, "alice_delrace", "Alice");
            var userB = await SeedUserAsync(db, "bob_delrace", "Bob");
            idA = new UserId(userA.Id);
            idB = new UserId(userB.Id);
            var conv = await SeedConversationAsync(db, idA, idB);
            convId = conv.Id;
        }

        for (var i = 0; i < 5; i++)
        {
            MessageId msgId;
            using (var msgScope = _fixture.CreateScope())
            {
                var db = msgScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var msg = Message.Create(convId, idA, Guid.NewGuid().ToString("N"), $"Race message {i}", DateTimeOffset.UtcNow);
                db.Messages.Add(msg);
                await db.SaveChangesAsync();
                msgId = msg.Id;
            }

            using var scopeDel = _fixture.CreateScope();
            using var scopeReact = _fixture.CreateScope();

            var sDel = CreateScopedMessageService(scopeDel.ServiceProvider);
            var sReact = CreateScopedMessageService(scopeReact.ServiceProvider);

            var delTask = sDel.DeleteForEveryoneAsync(idA, convId, msgId);
            var reactTask = Task.Run(async () =>
            {
                try
                {
                    await sReact.ToggleReactionAsync(idB, convId, msgId, new ToggleReactionRequestDto("🔥"));
                }
                catch (Exception ex) when (ex is ForbiddenException or ConflictException)
                {
                    // Expected when delete-for-everyone acquires lock first
                }
            });

            await Task.WhenAll(delTask, reactTask);

            using var verifyScope = _fixture.CreateScope();
            var freshDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var persisted = await freshDb.Messages.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ConversationId == convId && m.Id == msgId);
            Assert.NotNull(persisted);
            Assert.True(persisted.IsDeletedForEveryone);
            Assert.Null(persisted.Content);

            var remainingReactions = await freshDb.MessageReactions.AsNoTracking()
                .Where(r => r.ConversationId == convId && r.MessageId == msgId)
                .ToListAsync();
            Assert.Empty(remainingReactions);
        }
    }

    [Fact]
    public async Task ReplyPreview_LongTextWithEmojiAtBoundary100_DoesNotSplitUtf16SurrogatePair()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_utf16", "Alice");
        var userB = await SeedUserAsync(db, "bob_utf16", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        // Index 99 is the high surrogate of 🚀
        var contentWithBoundaryEmoji = new string('a', 99) + "🚀" + " trailing text";
        var parent = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto(contentWithBoundaryEmoji, Guid.NewGuid().ToString("N")));
        var reply = await service.SendMessageAsync(idB, conv.Id, new SendMessageRequestDto("Reply check", Guid.NewGuid().ToString("N"), parent.Id));

        Assert.NotNull(reply.ReplyTo);
        var sendPreview = reply.ReplyTo.PreviewContent;
        Assert.Equal(new string('a', 99) + "...", sendPreview);
        Assert.DoesNotContain(sendPreview.EnumerateRunes(), r => r.Value == 0xFFFD);

        var page = await service.GetMessagesAsync(idA, conv.Id, null);
        var fetchedReply = page.Messages.First(m => m.Id == reply.Id);
        Assert.NotNull(fetchedReply.ReplyTo);
        Assert.Equal(new string('a', 99) + "...", fetchedReply.ReplyTo.PreviewContent);
    }

    [Fact]
    public async Task SendAndEdit_InvisibleUnicodeCharacters_ThrowsDomainException()
    {
        var (service, db, _) = CreateService();
        var userA = await SeedUserAsync(db, "alice_zwsp", "Alice");
        var userB = await SeedUserAsync(db, "bob_zwsp", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        var invisiblePayloads = new[] { "\u200B", "\u200C\u200D\uFEFF", " \u2060\u00AD " };
        foreach (var payload in invisiblePayloads)
        {
            await Assert.ThrowsAsync<DomainException>(() =>
                service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto(payload, Guid.NewGuid().ToString("N"))));
        }

        var valid = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Valid text", Guid.NewGuid().ToString("N")));
        foreach (var payload in invisiblePayloads)
        {
            await Assert.ThrowsAsync<DomainException>(() =>
                service.EditMessageAsync(idA, conv.Id, valid.Id, new EditMessageRequestDto(payload)));
        }
    }

    [Fact]
    public async Task EditMessage_NoOpIdenticalTrimmedContent_DoesNotSetEditedAtUtcOrBroadcast()
    {
        var fakeTime = new TestTimeProvider(DateTimeOffset.UtcNow);
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notifier = new CapturingPhase8Notifier();
        var service = new MessageService(
            new MessagePersistence(db),
            new ConversationPersistence(db),
            new BlockPersistence(db),
            new UserReader(db),
            notifier,
            new ReactionPersistence(db),
            scope.ServiceProvider.GetRequiredService<ILogger<MessageService>>(),
            fakeTime);

        var userA = await SeedUserAsync(db, "alice_noop", "Alice");
        var userB = await SeedUserAsync(db, "bob_noop", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        var sent = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Original Content", Guid.NewGuid().ToString("N")));
        fakeTime.Advance(TimeSpan.FromMinutes(10));

        var editResult = await service.EditMessageAsync(idA, conv.Id, sent.Id, new EditMessageRequestDto("  Original Content  "));
        Assert.Null(editResult.EditedAtUtc);
        Assert.Equal("Original Content", editResult.Content);
        Assert.Empty(notifier.Edited);

        using var verifyScope = _fixture.CreateScope();
        var freshDb = verifyScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var persisted = await freshDb.Messages.AsNoTracking().FirstAsync(m => m.ConversationId == conv.Id && m.Id == sent.Id);
        Assert.Null(persisted.EditedAtUtc);
    }

    [Fact]
    public async Task DeleteForEveryone_And_DeleteForMe_EnforceCutoffs_AndBroadcastCallerLastMessageSync()
    {
        var t0 = DateTimeOffset.UtcNow;
        var fakeTime = new TestTimeProvider(t0);
        using var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var notifier = new CapturingPhase8Notifier();
        var service = new MessageService(
            new MessagePersistence(db),
            new ConversationPersistence(db),
            new BlockPersistence(db),
            new UserReader(db),
            notifier,
            new ReactionPersistence(db),
            scope.ServiceProvider.GetRequiredService<ILogger<MessageService>>(),
            fakeTime);

        var userA = await SeedUserAsync(db, "alice_delcut", "Alice");
        var userB = await SeedUserAsync(db, "bob_delcut", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        var m1 = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("First visible", Guid.NewGuid().ToString("N")));
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        var m2 = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Hidden via DeleteForMe", Guid.NewGuid().ToString("N")));
        fakeTime.Advance(TimeSpan.FromSeconds(5));
        var m3 = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Before clear", Guid.NewGuid().ToString("N")));

        // DeleteForMe on m2 -> DeleteForEveryone on m2 must throw NotFoundException, and caller notification carries m3 as last message
        await service.DeleteForMeAsync(idA, conv.Id, m2.Id);
        var notif1 = Assert.Single(notifier.DeletedForMe);
        Assert.Equal(idA, notif1.CallerUserId);
        Assert.Equal(m3.Id.Value, notif1.Message.NewLastMessageId);
        Assert.Equal("Before clear", notif1.Message.NewLastMessagePreview);

        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteForEveryoneAsync(idA, conv.Id, m2.Id));

        // Clear chat after m3 -> both DeleteForMe and DeleteForEveryone on m1/m3 must throw NotFoundException
        fakeTime.Advance(TimeSpan.FromMinutes(1));
        var stateA = UserConversationState.Create(conv.Id, idA);
        stateA.ClearChat(fakeTime.GetUtcNow());
        db.UserConversationStates.Add(stateA);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteForMeAsync(idA, conv.Id, m3.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.DeleteForEveryoneAsync(idA, conv.Id, m1.Id));
    }

    [Fact]
    public async Task ReceiptService_DeletedForMeMessage_ThrowsNotFoundException()
    {
        var (service, db, time) = CreateService();
        var userA = await SeedUserAsync(db, "alice_rcptdm", "Alice");
        var userB = await SeedUserAsync(db, "bob_rcptdm", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        var msg = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Hide from receipt", Guid.NewGuid().ToString("N")));
        await service.DeleteForMeAsync(idB, conv.Id, msg.Id);

        var receiptService = new ReceiptService(
            new Phase8TestCurrentUser { Id = idB },
            new ConversationPersistence(db),
            new MessagePersistence(db),
            new BlockPersistence(db),
            time);

        await Assert.ThrowsAsync<NotFoundException>(() => receiptService.MarkDeliveredAsync(conv.Id.Value, msg.Id.Value));
        await Assert.ThrowsAsync<NotFoundException>(() => receiptService.MarkReadAsync(conv.Id.Value, msg.Id.Value));
    }

    [Fact]
    public async Task ConversationPersistence_GetConversationsWithMessagesAfterAsync_ExcludesDeleteForMeMessages()
    {
        var t0 = DateTimeOffset.UtcNow;
        var (service, db, time) = CreateService(t0);
        var userA = await SeedUserAsync(db, "alice_convdel", "Alice");
        var userB = await SeedUserAsync(db, "bob_convdel", "Bob");
        var idA = new UserId(userA.Id);
        var idB = new UserId(userB.Id);
        var conv = await SeedConversationAsync(db, idA, idB);

        var stateB = UserConversationState.Create(conv.Id, idB);
        stateB.DeleteConversation(t0);
        db.UserConversationStates.Add(stateB);
        await db.SaveChangesAsync();

        time.Advance(TimeSpan.FromMinutes(1));
        var newMsg = await service.SendMessageAsync(idA, conv.Id, new SendMessageRequestDto("Re-surfaces until deleted for me", Guid.NewGuid().ToString("N")));

        var convRepo = new ConversationPersistence(db);
        var beforeDelete = await convRepo.GetConversationsWithMessagesAfterAsync([(conv.Id, t0)], idB);
        Assert.Contains(conv.Id, beforeDelete);

        await service.DeleteForMeAsync(idB, conv.Id, newMsg.Id);

        var afterDelete = await convRepo.GetConversationsWithMessagesAfterAsync([(conv.Id, t0)], idB);
        Assert.DoesNotContain(conv.Id, afterDelete);

        var convService = new ConversationService(
            convRepo,
            new UserReader(db),
            new BlockPersistence(db),
            new BlockService(new BlockPersistence(db), new UserReader(db), time),
            timeProvider: time);
        var list = await convService.GetConversationListAsync(idB);
        Assert.DoesNotContain(list, c => c.Id == conv.Id);
    }

    private static MessageService CreateScopedMessageService(IServiceProvider provider)
    {
        return new MessageService(
            new MessagePersistence(provider.GetRequiredService<ApplicationDbContext>()),
            new ConversationPersistence(provider.GetRequiredService<ApplicationDbContext>()),
            new BlockPersistence(provider.GetRequiredService<ApplicationDbContext>()),
            new UserReader(provider.GetRequiredService<ApplicationDbContext>()),
            new NullRealtimeNotifier(),
            new ReactionPersistence(provider.GetRequiredService<ApplicationDbContext>()),
            provider.GetRequiredService<ILogger<MessageService>>(),
            TimeProvider.System);
    }
}
