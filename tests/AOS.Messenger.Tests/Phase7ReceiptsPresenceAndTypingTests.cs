using System.Reflection;
using System.Security.Claims;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Filters;
using AOS.Messenger.Web.Hubs;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AOS.Messenger.Tests;

/// <summary>
/// Comprehensive integration and specification tests for Phase 7:
/// Delivery/Read Receipts, Typing Indicators, Presence &amp; Last Seen.
/// Evaluated against real SQL Server LocalDB with zero InMemory/SQLite mocks.
/// </summary>
public sealed class Phase7ReceiptsPresenceAndTypingTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public Phase7ReceiptsPresenceAndTypingTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private (IServiceProvider Provider, ApplicationDbContext Db) CreateTestEnvironment(UserId? currentUserId = null)
    {
        var services = _fixture.CreateServiceCollection();

        var fakeCurrentUser = new TestCurrentUser { Id = currentUserId };
        services.AddScoped<ICurrentUser>(_ => fakeCurrentUser);

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        return (scope.ServiceProvider, db);
    }

    private static async Task<(ApplicationUser UserA, ApplicationUser UserB, Conversation Conversation)> SeedParticipantsAndConversationAsync(
        ApplicationDbContext db,
        DateTimeOffset now)
    {
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();

        var userA = new ApplicationUser
        {
            Id = idA,
            UserName = $"user_a_{idA:N}",
            NormalizedUserName = $"USER_A_{idA:N}",
            Email = $"user_a_{idA:N}@test.com",
            NormalizedEmail = $"USER_A_{idA:N}@TEST.COM",
            DisplayName = "User Alpha",
            EmailConfirmed = true,
            IsDeleted = false
        };

        var userB = new ApplicationUser
        {
            Id = idB,
            UserName = $"user_b_{idB:N}",
            NormalizedUserName = $"USER_B_{idB:N}",
            Email = $"user_b_{idB:N}@test.com",
            NormalizedEmail = $"USER_B_{idB:N}@TEST.COM",
            DisplayName = "User Beta",
            EmailConfirmed = true,
            IsDeleted = false
        };

        db.Users.AddRange(userA, userB);
        await db.SaveChangesAsync();

        var conv = Conversation.CreateDirect(new UserId(userA.Id), new UserId(userB.Id), now);
        db.Conversations.Add(conv);
        await db.SaveChangesAsync();

        return (userA, userB, conv);
    }

    private static async Task<Message> SeedMessageAsync(
        ApplicationDbContext db,
        ConversationId convId,
        UserId senderId,
        string content,
        DateTimeOffset createdAtUtc)
    {
        var message = Message.Create(convId, senderId, Guid.NewGuid().ToString("N"), content, createdAtUtc);
        db.Messages.Add(message);
        await db.SaveChangesAsync();
        return message;
    }

    // =========================================================================
    // 1. Receipts & Composite FKs (SQL Server LocalDB Integration Tests)
    // =========================================================================

    [Fact]
    public async Task MarkDelivered_PersistsLastDeliveredMessageIdAndTimestampInSqlServer()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var receiverId = new UserId(userB.Id);

        var msg1 = await SeedMessageAsync(db, conv.Id, senderId, "Message 1", now.AddSeconds(1));
        var msg2 = await SeedMessageAsync(db, conv.Id, senderId, "Message 2", now.AddSeconds(2));

        var persistence = provider.GetRequiredService<IConversationPersistence>();
        var timeProvider = new TestTimeProvider(now.AddSeconds(10));

        var currentUser = new TestCurrentUser { Id = receiverId };
        var receiptService = new ReceiptService(
            currentUser,
            persistence,
            provider.GetRequiredService<IMessagePersistence>(),
            provider.GetRequiredService<IBlockPersistence>(),
            timeProvider);

        var result = await receiptService.MarkDeliveredAsync(conv.Id.Value, msg2.Id.Value);

        Assert.NotNull(result);
        Assert.Equal(conv.Id.Value, result.ConversationId);
        Assert.Equal(receiverId.Value, result.UserId);
        Assert.Equal(msg2.Id.Value, result.UpToMessageId);
        Assert.Equal("delivered", result.ReceiptType);

        // Verify directly in SQL Server
        await using var verifyDb = _fixture.CreateDbContext();
        var state = await verifyDb.UserConversationStates
            .FirstOrDefaultAsync(s => s.ConversationId == conv.Id && s.UserId == receiverId);

        Assert.NotNull(state);
        Assert.Equal(msg2.Id, state.LastDeliveredMessageId);
        Assert.Equal(msg2.CreatedAtUtc, state.LastDeliveredAtUtc);
    }

    [Fact]
    public async Task MarkRead_PersistsLastReadMessageIdAndTimestampInSqlServer()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var receiverId = new UserId(userB.Id);

        var msg1 = await SeedMessageAsync(db, conv.Id, senderId, "Message 1", now.AddSeconds(1));
        var msg2 = await SeedMessageAsync(db, conv.Id, senderId, "Message 2", now.AddSeconds(2));

        var persistence = provider.GetRequiredService<IConversationPersistence>();
        var currentUser = new TestCurrentUser { Id = receiverId };
        var receiptService = new ReceiptService(
            currentUser,
            persistence,
            provider.GetRequiredService<IMessagePersistence>(),
            provider.GetRequiredService<IBlockPersistence>());

        var result = await receiptService.MarkReadAsync(conv.Id.Value, msg2.Id.Value);

        Assert.NotNull(result);
        Assert.Equal("read", result.ReceiptType);
        Assert.Equal(msg2.Id.Value, result.UpToMessageId);

        // Verify directly in SQL Server
        await using var verifyDb = _fixture.CreateDbContext();
        var state = await verifyDb.UserConversationStates
            .FirstOrDefaultAsync(s => s.ConversationId == conv.Id && s.UserId == receiverId);

        Assert.NotNull(state);
        Assert.Equal(msg2.Id, state.LastReadMessageId);
        Assert.Equal(msg2.CreatedAtUtc, state.LastReadAtUtc);
    }

    [Fact]
    public async Task MarkRead_ImplicitlyAdvancesLastDeliveredMessageId_WhenBehind()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var receiverId = new UserId(userB.Id);

        var msg1 = await SeedMessageAsync(db, conv.Id, senderId, "Message 1", now.AddSeconds(1));
        var msg2 = await SeedMessageAsync(db, conv.Id, senderId, "Message 2", now.AddSeconds(2));
        var msg3 = await SeedMessageAsync(db, conv.Id, senderId, "Message 3", now.AddSeconds(3));

        var persistence = provider.GetRequiredService<IConversationPersistence>();

        // Set delivered up to msg1 first
        await persistence.AdvanceDeliveredWatermarkAsync(conv.Id, receiverId, msg1.Id, msg1.CreatedAtUtc);

        var currentUser = new TestCurrentUser { Id = receiverId };
        var receiptService = new ReceiptService(
            currentUser,
            persistence,
            provider.GetRequiredService<IMessagePersistence>(),
            provider.GetRequiredService<IBlockPersistence>());

        // Mark read directly up to msg3
        var result = await receiptService.MarkReadAsync(conv.Id.Value, msg3.Id.Value);
        Assert.NotNull(result);

        // Verify that in SQL Server, LastDeliveredMessageId implicitly jumped from msg1 to msg3!
        await using var verifyDb = _fixture.CreateDbContext();
        var state = await verifyDb.UserConversationStates
            .FirstOrDefaultAsync(s => s.ConversationId == conv.Id && s.UserId == receiverId);

        Assert.NotNull(state);
        Assert.Equal(msg3.Id, state.LastReadMessageId);
        Assert.Equal(msg3.Id, state.LastDeliveredMessageId);
        Assert.Equal(msg3.CreatedAtUtc, state.LastReadAtUtc);
        Assert.Equal(msg3.CreatedAtUtc, state.LastDeliveredAtUtc);
    }

    [Fact]
    public async Task WatermarkMonotonicity_SubmittingHigherThenLower_DoesNotRollBack()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var receiverId = new UserId(userB.Id);

        var msg1 = await SeedMessageAsync(db, conv.Id, senderId, "Message 1", now.AddSeconds(1));
        var msg2 = await SeedMessageAsync(db, conv.Id, senderId, "Message 2", now.AddSeconds(2));
        var msg3 = await SeedMessageAsync(db, conv.Id, senderId, "Message 3", now.AddSeconds(3));

        var persistence = provider.GetRequiredService<IConversationPersistence>();
        var currentUser = new TestCurrentUser { Id = receiverId };
        var receiptService = new ReceiptService(
            currentUser,
            persistence,
            provider.GetRequiredService<IMessagePersistence>(),
            provider.GetRequiredService<IBlockPersistence>());

        // 1. Advance to msg3
        var firstDelivered = await receiptService.MarkDeliveredAsync(conv.Id.Value, msg3.Id.Value);
        Assert.NotNull(firstDelivered);

        // 2. Submit stale receipt pointing back to msg1
        var secondDelivered = await receiptService.MarkDeliveredAsync(conv.Id.Value, msg1.Id.Value);
        // Returns null because watermark did not advance
        Assert.Null(secondDelivered);

        // 3. Repeat for MarkRead
        var firstRead = await receiptService.MarkReadAsync(conv.Id.Value, msg3.Id.Value);
        Assert.NotNull(firstRead);

        var secondRead = await receiptService.MarkReadAsync(conv.Id.Value, msg2.Id.Value);
        Assert.Null(secondRead);

        // Verify in SQL Server that watermarks remain at msg3
        await using var verifyDb = _fixture.CreateDbContext();
        var state = await verifyDb.UserConversationStates
            .FirstOrDefaultAsync(s => s.ConversationId == conv.Id && s.UserId == receiverId);

        Assert.NotNull(state);
        Assert.Equal(msg3.Id, state.LastDeliveredMessageId);
        Assert.Equal(msg3.Id, state.LastReadMessageId);
    }

    [Fact]
    public async Task CrossConversationIdor_ApplicationValidationRejectsMessageFromOtherConversation()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv1) = await SeedParticipantsAndConversationAsync(db, now);

        var userC = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"user_c_{Guid.NewGuid():N}",
            NormalizedUserName = $"USER_C_{Guid.NewGuid():N}",
            Email = $"user_c_{Guid.NewGuid():N}@test.com",
            NormalizedEmail = $"USER_C_{Guid.NewGuid():N}@TEST.COM",
            DisplayName = "User Gamma",
            EmailConfirmed = true
        };
        db.Users.Add(userC);
        await db.SaveChangesAsync();

        var conv2 = Conversation.CreateDirect(new UserId(userA.Id), new UserId(userC.Id), now);
        db.Conversations.Add(conv2);
        await db.SaveChangesAsync();

        var msgInConv2 = await SeedMessageAsync(db, conv2.Id, new UserId(userA.Id), "Conv 2 Message", now.AddSeconds(1));

        var persistence = provider.GetRequiredService<IConversationPersistence>();
        var currentUser = new TestCurrentUser { Id = new UserId(userB.Id) };
        var receiptService = new ReceiptService(
            currentUser,
            persistence,
            provider.GetRequiredService<IMessagePersistence>(),
            provider.GetRequiredService<IBlockPersistence>());

        // User B attempts to mark delivered in Conv 1 using a message ID from Conv 2!
        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            receiptService.MarkDeliveredAsync(conv1.Id.Value, msgInConv2.Id.Value));

        Assert.Equal("Message not found in conversation.", ex.Message);
    }

    [Fact]
    public async Task DeliveryCompositeFk_RejectsCrossConversationLastDeliveredMessage_AtDatabaseLevel()
    {
        await using var db = _fixture.CreateDbContext();
        var now = DateTimeOffset.UtcNow;

        var userA = UserId.New();
        var userB = UserId.New();
        var userC = UserId.New();

        var conv1 = Conversation.CreateDirect(userA, userB, now);
        var conv2 = Conversation.CreateDirect(userA, userC, now);
        db.Conversations.AddRange(conv1, conv2);
        await db.SaveChangesAsync();

        var msgInConv2 = Message.Create(conv2.Id, userA, Guid.NewGuid().ToString("N"), "Message in Conv 2", now);
        db.Messages.Add(msgInConv2);
        await db.SaveChangesAsync();

        // Attempt to create UserConversationState for Conv 1 pointing LastDeliveredMessageId to msgInConv2!
        var state = UserConversationState.Create(conv1.Id, userB);
        state.AdvanceDelivered(msgInConv2.Id, now);
        db.UserConversationStates.Add(state);

        // SQL Server composite FK (ConversationId, LastDeliveredMessageId) -> Messages(ConversationId, Id) must reject this!
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.NotNull(ex.InnerException);
        Assert.Contains("FK_UserConversationStates_Messages_ConversationId_LastDeliveredMessageId", ex.InnerException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClearedOrDeletedCutoff_MarkingPriorMessage_ThrowsNotFoundException()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var receiverId = new UserId(userB.Id);

        var msg1 = await SeedMessageAsync(db, conv.Id, senderId, "Before clear", now.AddSeconds(1));
        var clearTime = now.AddSeconds(5);
        var msg2 = await SeedMessageAsync(db, conv.Id, senderId, "After clear", now.AddSeconds(10));

        // Create cleared state for receiver
        var state = UserConversationState.Create(conv.Id, receiverId);
        state.ClearChat(clearTime);
        db.UserConversationStates.Add(state);
        await db.SaveChangesAsync();

        var persistence = provider.GetRequiredService<IConversationPersistence>();
        var currentUser = new TestCurrentUser { Id = receiverId };
        var receiptService = new ReceiptService(
            currentUser,
            persistence,
            provider.GetRequiredService<IMessagePersistence>(),
            provider.GetRequiredService<IBlockPersistence>());

        // Marking msg1 (prior to cutoff) must throw NotFoundException
        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            receiptService.MarkDeliveredAsync(conv.Id.Value, msg1.Id.Value));
        Assert.Equal("Message is cleared or deleted.", ex.Message);

        // Marking msg2 (after cutoff) must succeed
        var result = await receiptService.MarkDeliveredAsync(conv.Id.Value, msg2.Id.Value);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task BidirectionalBlockEnforcement_ReceiptsForbidden()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var receiverId = new UserId(userB.Id);

        var msg1 = await SeedMessageAsync(db, conv.Id, senderId, "Hello", now.AddSeconds(1));

        // UserA blocks UserB
        var blockPersistence = provider.GetRequiredService<IBlockPersistence>();
        await blockPersistence.AddBlockAsync(UserBlock.Create(senderId, receiverId, now));

        var persistence = provider.GetRequiredService<IConversationPersistence>();
        var currentUser = new TestCurrentUser { Id = receiverId };
        var receiptService = new ReceiptService(
            currentUser,
            persistence,
            provider.GetRequiredService<IMessagePersistence>(),
            blockPersistence);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            receiptService.MarkDeliveredAsync(conv.Id.Value, msg1.Id.Value));
        Assert.Contains("blocked", ex.Message, StringComparison.OrdinalIgnoreCase);

        var exRead = await Assert.ThrowsAsync<ForbiddenException>(() =>
            receiptService.MarkReadAsync(conv.Id.Value, msg1.Id.Value));
        Assert.Contains("blocked", exRead.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReceiptsController_MarkDeliveredAndRead_BroadcastsRealtimeReceiptToPeer()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var receiverId = new UserId(userB.Id);

        var msg = await SeedMessageAsync(db, conv.Id, senderId, "Hello", now.AddSeconds(1));

        var persistence = provider.GetRequiredService<IConversationPersistence>();
        var currentUser = new TestCurrentUser { Id = receiverId };
        var receiptService = new ReceiptService(
            currentUser,
            persistence,
            provider.GetRequiredService<IMessagePersistence>(),
            provider.GetRequiredService<IBlockPersistence>());

        var convService = provider.GetRequiredService<IConversationService>();
        var capturingNotifier = new CapturingReceiptNotifier();

        var controller = new ReceiptsController(
            receiptService,
            capturingNotifier,
            currentUser,
            NullLogger<ReceiptsController>.Instance,
            new TestTimeProvider(now.AddSeconds(10)));

        var deliveredRes = await controller.MarkDelivered(
            conv.Id.Value,
            new MarkReceiptRequestDto(msg.Id.Value),
            CancellationToken.None);

        var okDelivered = Assert.IsType<OkObjectResult>(deliveredRes);
        var deliveredDto = Assert.IsType<ReceiptUpdateDto>(okDelivered.Value);
        Assert.Equal("delivered", deliveredDto.ReceiptType);
        Assert.Single(capturingNotifier.ReceiptUpdates);
        Assert.Equal(senderId, capturingNotifier.ReceiptUpdates[0].RecipientId);

        // Mark read
        var readRes = await controller.MarkRead(
            conv.Id.Value,
            new MarkReceiptRequestDto(msg.Id.Value),
            CancellationToken.None);

        var okRead = Assert.IsType<OkObjectResult>(readRes);
        var readDto = Assert.IsType<ReceiptUpdateDto>(okRead.Value);
        Assert.Equal("read", readDto.ReceiptType);
        Assert.Equal(2, capturingNotifier.ReceiptUpdates.Count);
        Assert.Equal("read", capturingNotifier.ReceiptUpdates[1].Receipt.ReceiptType);
    }

    [Fact]
    public async Task SelfMessageReceipt_ServiceThrowsForbidden_AndDoesNotAdvanceWatermark()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var msg = await SeedMessageAsync(db, conv.Id, senderId, "Outgoing message from User A", now.AddSeconds(1));

        var persistence = provider.GetRequiredService<IConversationPersistence>();
        // User A (the sender of msg) attempts to mark their own message as delivered/read
        var currentUser = new TestCurrentUser { Id = senderId };
        var receiptService = new ReceiptService(
            currentUser,
            persistence,
            provider.GetRequiredService<IMessagePersistence>(),
            provider.GetRequiredService<IBlockPersistence>());

        // 1. MarkDelivered with own outgoing message must throw ForbiddenException
        var delEx = await Assert.ThrowsAsync<ForbiddenException>(() =>
            receiptService.MarkDeliveredAsync(conv.Id.Value, msg.Id.Value));
        Assert.Equal("Cannot mark receipts for own outgoing messages.", delEx.Message);

        // 2. MarkRead with own outgoing message must throw ForbiddenException
        var readEx = await Assert.ThrowsAsync<ForbiddenException>(() =>
            receiptService.MarkReadAsync(conv.Id.Value, msg.Id.Value));
        Assert.Equal("Cannot mark receipts for own outgoing messages.", readEx.Message);

        // 3. Verify in SQL Server that User A's watermarks were NOT advanced
        await using var verifyDb = _fixture.CreateDbContext();
        var state = await verifyDb.UserConversationStates
            .FirstOrDefaultAsync(s => s.ConversationId == conv.Id && s.UserId == senderId);

        if (state is not null)
        {
            Assert.Null(state.LastDeliveredMessageId);
            Assert.Null(state.LastReadMessageId);
        }
    }

    [Fact]
    public async Task SelfMessageReceipt_ControllerThrowsForbidden_AndDoesNotBroadcast()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var msg = await SeedMessageAsync(db, conv.Id, senderId, "Outgoing message from User A", now.AddSeconds(1));

        var persistence = provider.GetRequiredService<IConversationPersistence>();
        var currentUser = new TestCurrentUser { Id = senderId };
        var receiptService = new ReceiptService(
            currentUser,
            persistence,
            provider.GetRequiredService<IMessagePersistence>(),
            provider.GetRequiredService<IBlockPersistence>());

        var convService = provider.GetRequiredService<IConversationService>();
        var capturingNotifier = new CapturingReceiptNotifier();

        var controller = new ReceiptsController(
            receiptService,
            capturingNotifier,
            currentUser,
            NullLogger<ReceiptsController>.Instance,
            new TestTimeProvider(now.AddSeconds(10)));

        // Both endpoints must throw ForbiddenException (mapped to 403 Forbidden by exception middleware)
        await Assert.ThrowsAsync<ForbiddenException>(() =>
            controller.MarkDelivered(conv.Id.Value, new MarkReceiptRequestDto(msg.Id.Value), CancellationToken.None));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            controller.MarkRead(conv.Id.Value, new MarkReceiptRequestDto(msg.Id.Value), CancellationToken.None));

        // SignalR notifier must NOT have broadcast any receipt updates
        Assert.Empty(capturingNotifier.ReceiptUpdates);
    }

    [Fact]
    public void ReceiptsController_HasRequiredSecurityAttributes()
    {
        var type = typeof(ReceiptsController);
        Assert.True(type.IsSealed, "ReceiptsController must be sealed.");

        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.NotNull(type.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>());

        var route = type.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("api/conversations/{conversationId:guid}", route.Template);

        var rateLimit = type.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimit);
        Assert.Equal("receipts", rateLimit.PolicyName);

        var deliveredMethod = type.GetMethod(nameof(ReceiptsController.MarkDelivered));
        Assert.NotNull(deliveredMethod);
        var deliveredHttp = deliveredMethod.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(deliveredHttp);
        Assert.Equal("delivered", deliveredHttp.Template);

        var readMethod = type.GetMethod(nameof(ReceiptsController.MarkRead));
        Assert.NotNull(readMethod);
        var readHttp = readMethod.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(readHttp);
        Assert.Equal("read", readHttp.Template);
    }

    // =========================================================================
    // 2. Typing Indicators
    // =========================================================================

    [Fact]
    public async Task Typing_AllowedBetweenUnblockedParticipants()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var typingService = new TypingService(
            provider.GetRequiredService<IConversationPersistence>());

        var recipient = await typingService.ValidateAndGetTypingRecipientAsync(
            new UserId(userA.Id),
            conv.Id.Value);

        Assert.NotNull(recipient);
        Assert.Equal(userB.Id, recipient.Value);
    }

    [Fact]
    public async Task Typing_Dropped_WhenCallerIsNotParticipant()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (_, _, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var nonParticipant = UserId.New();
        var typingService = new TypingService(
            provider.GetRequiredService<IConversationPersistence>());

        var recipient = await typingService.ValidateAndGetTypingRecipientAsync(
            nonParticipant,
            conv.Id.Value);

        Assert.Null(recipient);
    }

    [Fact]
    public async Task Typing_Dropped_WhenEitherUserBlockedTheOther()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var receiverId = new UserId(userB.Id);

        var blockPersistence = provider.GetRequiredService<IBlockPersistence>();
        await blockPersistence.AddBlockAsync(UserBlock.Create(receiverId, senderId, now));

        var typingService = new TypingService(
            provider.GetRequiredService<IConversationPersistence>());

        var recipient = await typingService.ValidateAndGetTypingRecipientAsync(senderId, conv.Id.Value);
        Assert.Null(recipient);
    }

    [Fact]
    public async Task Typing_Dropped_WhenPeerIsSoftDeletedOrUnconfirmed()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var typingService = new TypingService(
            provider.GetRequiredService<IConversationPersistence>());

        Assert.NotNull(await typingService.ValidateAndGetTypingRecipientAsync(new UserId(userA.Id), conv.Id.Value));

        // Unconfirmed peer must drop typing indicator
        userB.EmailConfirmed = false;
        await db.SaveChangesAsync();
        Assert.Null(await typingService.ValidateAndGetTypingRecipientAsync(new UserId(userA.Id), conv.Id.Value));

        // Soft-deleted peer must drop typing indicator even if email confirmed
        userB.EmailConfirmed = true;
        userB.IsDeleted = true;
        await db.SaveChangesAsync();
        Assert.Null(await typingService.ValidateAndGetTypingRecipientAsync(new UserId(userA.Id), conv.Id.Value));
    }

    [Fact]
    public async Task ChatHub_SendTypingIndicator_BroadcastsToRecipient()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, conv) = await SeedParticipantsAndConversationAsync(db, now);

        var senderId = new UserId(userA.Id);
        var receiverId = new UserId(userB.Id);

        var typingService = new TypingService(
            provider.GetRequiredService<IConversationPersistence>());

        var capturingNotifier = new CapturingReceiptNotifier();
        var connectionTracker = new InMemoryConnectionTracker();

        var hub = new ChatHub(
            provider.GetRequiredService<IConversationService>(),
            connectionTracker,
            new TestCurrentUser { Id = senderId },
            NullLogger<ChatHub>.Instance,
            provider.GetRequiredService<IPresenceService>(),
            typingService,
            capturingNotifier);

        hub.Context = new TestHubCallerContext("conn-typing-1", CreateClaimsPrincipal(senderId, Guid.NewGuid()));

        await hub.SendTypingIndicator(conv.Id.Value, true);

        Assert.Single(capturingNotifier.TypingIndicators);
        var typing = capturingNotifier.TypingIndicators[0];
        Assert.Equal(receiverId, typing.RecipientId);
        Assert.Equal(conv.Id.Value, typing.Typing.ConversationId);
        Assert.Equal(senderId.Value, typing.Typing.UserId);
        Assert.True(typing.Typing.IsTyping);
    }

    [Fact]
    public async Task SessionAndThrottleHubFilter_ThrottlesExcessiveInvocations()
    {
        var sessionId = Guid.NewGuid();
        var fakeSessionService = new FakeSessionService(new HashSet<Guid> { sessionId });
        var timeProvider = new TestTimeProvider(DateTimeOffset.UtcNow);
        var filter = new SessionAndThrottleHubFilter(fakeSessionService, timeProvider);

        var connectionId = "conn-flood-test";
        var context = new TestHubCallerContext(connectionId, CreateClaimsPrincipal(UserId.New(), sessionId));
        var invocationContext = new HubInvocationContext(
            context,
            serviceProvider: new ServiceCollection().BuildServiceProvider(),
            hub: new DummyHub(),
            hubMethod: typeof(ChatHub).GetMethod(nameof(ChatHub.SendTypingIndicator))!,
            hubMethodArguments: new object[] { Guid.NewGuid(), true });

        // Max limit is 60 per 1-minute window
        for (int i = 0; i < 60; i++)
        {
            var result = await filter.InvokeMethodAsync(invocationContext, _ => ValueTask.FromResult<object?>(null));
            Assert.Null(result);
        }

        // 61st invocation within the same window must throw HubException
        var ex = await Assert.ThrowsAsync<HubException>(() =>
            filter.InvokeMethodAsync(invocationContext, _ => ValueTask.FromResult<object?>(null)).AsTask());

        Assert.Contains("Rate limit exceeded", ex.Message);
    }

    [Fact]
    public async Task SessionAndThrottleHubFilter_AbortsWhenSessionIsRevoked()
    {
        var sessionId = Guid.NewGuid();
        var fakeSessionService = new FakeSessionService(activeSessions: new HashSet<Guid>()); // empty = revoked

        var filter = new SessionAndThrottleHubFilter(fakeSessionService);

        var aborted = false;
        var context = new TestHubCallerContext("conn-revoked-test", CreateClaimsPrincipal(UserId.New(), sessionId), () => aborted = true);

        var invocationContext = new HubInvocationContext(
            context,
            serviceProvider: new ServiceCollection().BuildServiceProvider(),
            hub: new DummyHub(),
            hubMethod: typeof(ChatHub).GetMethod(nameof(ChatHub.SendTypingIndicator))!,
            hubMethodArguments: new object[] { Guid.NewGuid(), true });

        var ex = await Assert.ThrowsAsync<HubException>(() =>
            filter.InvokeMethodAsync(invocationContext, _ => ValueTask.FromResult<object?>(null)).AsTask());

        Assert.Contains("Session has been revoked", ex.Message);
        Assert.True(aborted);
    }

    // =========================================================================
    // 3. Presence & Last Seen
    // =========================================================================

    [Fact]
    public async Task MultiDeviceTransitions_DisconnectOneKeepsOnline_DisconnectAllWritesLastSeen()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, _, _) = await SeedParticipantsAndConversationAsync(db, now);

        var userId = new UserId(userA.Id);
        var sessionId = Guid.NewGuid();
        var tracker = new InMemoryConnectionTracker();

        var timeProvider = new TestTimeProvider(now);
        var presenceService = new PresenceService(
            provider.GetRequiredService<IConversationPersistence>(),
            provider.GetRequiredService<IUserPresencePersistence>(),
            tracker,
            provider.GetRequiredService<IBlockPersistence>(),
            provider.GetRequiredService<IUserReader>());

        var capturingNotifier = new CapturingReceiptNotifier();

        var hub = new ChatHub(
            provider.GetRequiredService<IConversationService>(),
            tracker,
            new TestCurrentUser { Id = userId },
            NullLogger<ChatHub>.Instance,
            presenceService,
            provider.GetRequiredService<ITypingService>(),
            capturingNotifier,
            timeProvider);

        // Device 1 connects
        hub.Context = new TestHubCallerContext("conn-dev-1", CreateClaimsPrincipal(userId, sessionId));
        await hub.OnConnectedAsync();
        Assert.True(tracker.IsUserOnline(userId));

        // Device 2 connects
        hub.Context = new TestHubCallerContext("conn-dev-2", CreateClaimsPrincipal(userId, sessionId));
        await hub.OnConnectedAsync();
        Assert.True(tracker.IsUserOnline(userId));

        // Advance time by 5 minutes
        var disconnectTime1 = now.AddMinutes(5);
        timeProvider.SetUtcNow(disconnectTime1);

        // Device 1 disconnects
        hub.Context = new TestHubCallerContext("conn-dev-1", CreateClaimsPrincipal(userId, sessionId));
        await hub.OnDisconnectedAsync(null);

        // User should STILL be online!
        Assert.True(tracker.IsUserOnline(userId));

        // LastSeenAtUtc in SQL Server should NOT be set yet
        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var u = await verifyDb.Users.FirstAsync(x => x.Id == userA.Id);
            Assert.Null(u.LastSeenAtUtc);
        }

        // Advance time by another 5 minutes
        var disconnectTime2 = now.AddMinutes(10);
        timeProvider.SetUtcNow(disconnectTime2);

        // Device 2 disconnects -> User transitions offline!
        hub.Context = new TestHubCallerContext("conn-dev-2", CreateClaimsPrincipal(userId, sessionId));
        await hub.OnDisconnectedAsync(null);

        Assert.False(tracker.IsUserOnline(userId));

        // LastSeenAtUtc in SQL Server must match exact TimeProvider timestamp!
        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var u = await verifyDb.Users.FirstAsync(x => x.Id == userA.Id);
            Assert.NotNull(u.LastSeenAtUtc);
            Assert.Equal(disconnectTime2, u.LastSeenAtUtc.Value);
        }
    }

    [Fact]
    public async Task TerminatingLastSession_RecordsLastSeen_AndBroadcastsOffline_WithoutDoubleEmittingOnDisconnected()
    {
        var start = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(start);
        var tracker = new InMemoryConnectionTracker();
        var capturingNotifier = new CapturingReceiptNotifier();

        var services = _fixture.CreateServiceCollection();
        services.AddSingleton<TimeProvider>(timeProvider);
        services.AddSingleton<IConnectionTracker>(tracker);
        services.AddSingleton<IOnlinePresenceTracker>(tracker);
        services.AddSingleton<IRealtimeNotifier>(capturingNotifier);
        services.AddScoped<ICurrentUser>(_ => new TestCurrentUser());
        await using var provider = services.BuildServiceProvider();

        ApplicationUser userA;
        ApplicationUser userB;
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (userA, userB, _) = await SeedParticipantsAndConversationAsync(db, start);
        }

        var userId = new UserId(userA.Id);
        var peerId = new UserId(userB.Id);
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();

        var terminator = new SignalRSessionConnectionTerminator(
            tracker,
            NullLogger<SignalRSessionConnectionTerminator>.Instance,
            provider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider);

        await using var hubScope = provider.CreateAsyncScope();
        var presenceService = hubScope.ServiceProvider.GetRequiredService<IPresenceService>();
        var hub = new ChatHub(
            hubScope.ServiceProvider.GetRequiredService<IConversationService>(),
            tracker,
            new TestCurrentUser { Id = userId },
            NullLogger<ChatHub>.Instance,
            presenceService,
            hubScope.ServiceProvider.GetRequiredService<ITypingService>(),
            capturingNotifier,
            timeProvider);

        // Connect two sessions for userA
        var abortA = false;
        hub.Context = new TestHubCallerContext("conn-abort-a", CreateClaimsPrincipal(userId, sessionA), () => abortA = true);
        await hub.OnConnectedAsync();

        var abortB = false;
        hub.Context = new TestHubCallerContext("conn-abort-b", CreateClaimsPrincipal(userId, sessionB), () => abortB = true);
        await hub.OnConnectedAsync();

        capturingNotifier.PresenceChanges.Clear();

        // Terminate only sessionA -> user stays online on sessionB
        await terminator.DisconnectSessionAsync(sessionA);
        Assert.True(abortA);
        Assert.False(abortB);
        Assert.True(tracker.IsUserOnline(userId));
        Assert.Empty(capturingNotifier.PresenceChanges);

        // Terminate remaining sessionB -> user goes offline, LastSeen recorded, offline broadcast sent once
        var abortTime = start.AddMinutes(7);
        timeProvider.SetUtcNow(abortTime);
        await terminator.DisconnectSessionAsync(sessionB);

        Assert.True(abortB);
        Assert.False(tracker.IsUserOnline(userId));
        var offlineBroadcast = Assert.Single(capturingNotifier.PresenceChanges);
        Assert.Contains(peerId, offlineBroadcast.Recipients);
        Assert.Equal(userId.Value, offlineBroadcast.Presence.UserId);
        Assert.False(offlineBroadcast.Presence.IsOnline);
        Assert.Equal(abortTime, offlineBroadcast.Presence.LastSeenAtUtc);

        await using (var verifyDb = _fixture.CreateDbContext())
        {
            var u = await verifyDb.Users.FirstAsync(x => x.Id == userA.Id);
            Assert.Equal(abortTime, u.LastSeenAtUtc);
        }

        // Subsequent OnDisconnectedAsync fired by aborted SignalR transport must NOT double-emit or alter LastSeen
        timeProvider.SetUtcNow(abortTime.AddMinutes(5));
        hub.Context = new TestHubCallerContext("conn-abort-b", CreateClaimsPrincipal(userId, sessionB));
        await hub.OnDisconnectedAsync(null);
        Assert.Single(capturingNotifier.PresenceChanges);
        await using (var verifyDb2 = _fixture.CreateDbContext())
        {
            var u = await verifyDb2.Users.FirstAsync(x => x.Id == userA.Id);
            Assert.Equal(abortTime, u.LastSeenAtUtc);
        }

        // Verify DisconnectUserSessionsAsync also records LastSeen & broadcasts offline once
        var sessionC = Guid.NewGuid();
        var abortC = false;
        var reconnectTime = abortTime.AddMinutes(10);
        timeProvider.SetUtcNow(reconnectTime);
        hub.Context = new TestHubCallerContext("conn-abort-c", CreateClaimsPrincipal(userId, sessionC), () => abortC = true);
        await hub.OnConnectedAsync();
        capturingNotifier.PresenceChanges.Clear();

        var fullAbortTime = reconnectTime.AddMinutes(3);
        timeProvider.SetUtcNow(fullAbortTime);
        await terminator.DisconnectUserSessionsAsync(userId);

        Assert.True(abortC);
        Assert.False(tracker.IsUserOnline(userId));
        var userOfflineBroadcast = Assert.Single(capturingNotifier.PresenceChanges);
        Assert.False(userOfflineBroadcast.Presence.IsOnline);
        Assert.Equal(fullAbortTime, userOfflineBroadcast.Presence.LastSeenAtUtc);
    }

    [Fact]
    public async Task AntiStalking_GetPeerPresence_ReturnsNotFound_WhenNoSharedConversation()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();

        var userA = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"user_a_{Guid.NewGuid():N}",
            NormalizedUserName = $"USER_A_{Guid.NewGuid():N}",
            Email = $"a_{Guid.NewGuid():N}@test.com",
            NormalizedEmail = $"A_{Guid.NewGuid():N}@TEST.COM",
            DisplayName = "User A",
            EmailConfirmed = true
        };
        var userB = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"user_b_{Guid.NewGuid():N}",
            NormalizedUserName = $"USER_B_{Guid.NewGuid():N}",
            Email = $"b_{Guid.NewGuid():N}@test.com",
            NormalizedEmail = $"B_{Guid.NewGuid():N}@TEST.COM",
            DisplayName = "User B",
            EmailConfirmed = true
        };
        db.Users.AddRange(userA, userB);
        await db.SaveChangesAsync();

        var presenceService = provider.GetRequiredService<IPresenceService>();
        var result = await presenceService.GetPeerPresenceAsync(new UserId(userA.Id), new UserId(userB.Id));

        Assert.Null(result);

        // Also test controller mapping
        var controller = new PresenceController(presenceService, new TestCurrentUser { Id = new UserId(userA.Id) });
        var actionResult = await controller.GetPeerPresence(userB.Id, CancellationToken.None);
        Assert.IsType<NotFoundResult>(actionResult);
    }

    [Fact]
    public async Task AntiStalking_GetPeerPresence_ReturnsNotFound_WhenBlocked()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, _) = await SeedParticipantsAndConversationAsync(db, now);

        var callerId = new UserId(userA.Id);
        var targetId = new UserId(userB.Id);

        var blockPersistence = provider.GetRequiredService<IBlockPersistence>();
        await blockPersistence.AddBlockAsync(UserBlock.Create(targetId, callerId, now));

        var presenceService = provider.GetRequiredService<IPresenceService>();
        var result = await presenceService.GetPeerPresenceAsync(callerId, targetId);

        Assert.Null(result);

        var controller = new PresenceController(presenceService, new TestCurrentUser { Id = callerId });
        var actionResult = await controller.GetPeerPresence(targetId.Value, CancellationToken.None);
        Assert.IsType<NotFoundResult>(actionResult);
    }

    [Fact]
    public async Task AntiStalking_GetPeerPresence_ReturnsNotFound_WhenTargetIsUnverifiedOrSoftDeleted()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, _) = await SeedParticipantsAndConversationAsync(db, now);

        var callerId = new UserId(userA.Id);
        var targetId = new UserId(userB.Id);

        // Soft-delete user B
        var userInDb = await db.Users.FirstAsync(u => u.Id == userB.Id);
        userInDb.IsDeleted = true;
        await db.SaveChangesAsync();

        var presenceService = provider.GetRequiredService<IPresenceService>();
        var result = await presenceService.GetPeerPresenceAsync(callerId, targetId);

        Assert.Null(result);

        // Reset soft-delete, make unconfirmed
        userInDb.IsDeleted = false;
        userInDb.EmailConfirmed = false;
        await db.SaveChangesAsync();

        var result2 = await presenceService.GetPeerPresenceAsync(callerId, targetId);
        Assert.Null(result2);
    }

    [Fact]
    public async Task AntiStalking_GetPeerPresence_ReturnsPresence_WhenSharingUnblockedConversation()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, userB, _) = await SeedParticipantsAndConversationAsync(db, now);

        var callerId = new UserId(userA.Id);
        var targetId = new UserId(userB.Id);

        var lastSeen = now.AddMinutes(-12);
        var userInDb = await db.Users.FirstAsync(u => u.Id == userB.Id);
        userInDb.LastSeenAtUtc = lastSeen;
        await db.SaveChangesAsync();

        var presenceService = provider.GetRequiredService<IPresenceService>();
        var result = await presenceService.GetPeerPresenceAsync(callerId, targetId);

        Assert.NotNull(result);
        Assert.Equal(targetId.Value, result.UserId);
        Assert.False(result.IsOnline);
        Assert.Equal(lastSeen, result.LastSeenAtUtc);

        var controller = new PresenceController(presenceService, new TestCurrentUser { Id = callerId });
        var actionResult = await controller.GetPeerPresence(targetId.Value, CancellationToken.None);
        var okResult = Assert.IsType<OkObjectResult>(actionResult);
        var dto = Assert.IsType<UserPresenceDto>(okResult.Value);
        Assert.Equal(targetId.Value, dto.UserId);
        Assert.Equal(lastSeen, dto.LastSeenAtUtc);
    }

    [Fact]
    public async Task GetEligiblePresencePeerIdsAsync_ExcludesBlockedUnverifiedAndSoftDeletedAccounts()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();

        var mainUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"main_{Guid.NewGuid():N}",
            NormalizedUserName = $"MAIN_{Guid.NewGuid():N}",
            Email = $"main_{Guid.NewGuid():N}@test.com",
            NormalizedEmail = $"MAIN_{Guid.NewGuid():N}@TEST.COM",
            DisplayName = "Main User",
            EmailConfirmed = true
        };

        var validPeer = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"valid_{Guid.NewGuid():N}",
            NormalizedUserName = $"VALID_{Guid.NewGuid():N}",
            Email = $"valid_{Guid.NewGuid():N}@test.com",
            NormalizedEmail = $"VALID_{Guid.NewGuid():N}@TEST.COM",
            DisplayName = "Valid Peer",
            EmailConfirmed = true
        };

        var blockedPeer = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"blocked_{Guid.NewGuid():N}",
            NormalizedUserName = $"BLOCKED_{Guid.NewGuid():N}",
            Email = $"blocked_{Guid.NewGuid():N}@test.com",
            NormalizedEmail = $"BLOCKED_{Guid.NewGuid():N}@TEST.COM",
            DisplayName = "Blocked Peer",
            EmailConfirmed = true
        };

        var unverifiedPeer = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"unverified_{Guid.NewGuid():N}",
            NormalizedUserName = $"UNVERIFIED_{Guid.NewGuid():N}",
            Email = $"unverified_{Guid.NewGuid():N}@test.com",
            NormalizedEmail = $"UNVERIFIED_{Guid.NewGuid():N}@TEST.COM",
            DisplayName = "Unverified Peer",
            EmailConfirmed = false // unverified!
        };

        var deletedPeer = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"deleted_{Guid.NewGuid():N}",
            NormalizedUserName = $"DELETED_{Guid.NewGuid():N}",
            Email = $"deleted_{Guid.NewGuid():N}@test.com",
            NormalizedEmail = $"DELETED_{Guid.NewGuid():N}@TEST.COM",
            DisplayName = "Deleted Peer",
            EmailConfirmed = true,
            IsDeleted = true // deleted!
        };

        db.Users.AddRange(mainUser, validPeer, blockedPeer, unverifiedPeer, deletedPeer);
        await db.SaveChangesAsync();

        var mainId = new UserId(mainUser.Id);
        var validId = new UserId(validPeer.Id);
        var blockedId = new UserId(blockedPeer.Id);
        var unverifiedId = new UserId(unverifiedPeer.Id);
        var deletedId = new UserId(deletedPeer.Id);

        // Create conversations with all 4 peers
        db.Conversations.AddRange(
            Conversation.CreateDirect(mainId, validId, now),
            Conversation.CreateDirect(mainId, blockedId, now),
            Conversation.CreateDirect(mainId, unverifiedId, now),
            Conversation.CreateDirect(mainId, deletedId, now));
        await db.SaveChangesAsync();

        // Add block with blockedPeer
        var blockPersistence = provider.GetRequiredService<IBlockPersistence>();
        await blockPersistence.AddBlockAsync(UserBlock.Create(mainId, blockedId, now));

        var presenceService = provider.GetRequiredService<IPresenceService>();
        var eligiblePeers = await presenceService.GetEligiblePresencePeerIdsAsync(mainId);

        // Only validPeer should be returned!
        Assert.Single(eligiblePeers);
        Assert.Equal(validId, eligiblePeers[0]);
    }

    [Fact]
    public void PresenceController_HasRequiredSecurityAttributes()
    {
        var type = typeof(PresenceController);
        Assert.True(type.IsSealed, "PresenceController must be sealed.");

        Assert.NotNull(type.GetCustomAttribute<AuthorizeAttribute>());
        Assert.NotNull(type.GetCustomAttribute<ApiControllerAttribute>());
        Assert.NotNull(type.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>());

        var route = type.GetCustomAttribute<RouteAttribute>();
        Assert.NotNull(route);
        Assert.Equal("api/presence", route.Template);

        var rateLimit = type.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimit);
        Assert.Equal("presence", rateLimit.PolicyName);

        var getPresences = type.GetMethod(nameof(PresenceController.GetPresences));
        Assert.NotNull(getPresences);
        Assert.NotNull(getPresences.GetCustomAttribute<HttpGetAttribute>());

        var getPeerPresence = type.GetMethod(nameof(PresenceController.GetPeerPresence));
        Assert.NotNull(getPeerPresence);
        var peerHttp = getPeerPresence.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(peerHttp);
        Assert.Equal("{userId:guid}", peerHttp.Template);
    }

    [Fact]
    public async Task ChatHub_OnConnectedAsync_WhenConnectionRemovedBeforePresenceBroadcast_DoesNotBroadcastOnline()
    {
        var now = DateTimeOffset.UtcNow;
        var (provider, db) = CreateTestEnvironment();
        var (userA, _, _) = await SeedParticipantsAndConversationAsync(db, now);
        var callerId = new UserId(userA.Id);
        var sessionId = Guid.NewGuid();

        var connectionTracker = new InMemoryConnectionTracker();
        var capturingNotifier = new CapturingReceiptNotifier();
        var cts = new CancellationTokenSource();

        var hub = new ChatHub(
            provider.GetRequiredService<IConversationService>(),
            connectionTracker,
            new TestCurrentUser { Id = callerId },
            NullLogger<ChatHub>.Instance,
            new RevokingPresenceService(provider.GetRequiredService<IPresenceService>(), connectionTracker, callerId, "aborted-conn-1", cts),
            new TypingService(provider.GetRequiredService<IConversationPersistence>()),
            capturingNotifier)
        {
            Context = new AbortableHubCallerContext("aborted-conn-1", CreateClaimsPrincipal(callerId, sessionId), cts.Token)
        };

        await hub.OnConnectedAsync();

        Assert.Empty(capturingNotifier.PresenceChanges);
    }

    private sealed class RevokingPresenceService : IPresenceService
    {
        private readonly IPresenceService _inner;
        private readonly InMemoryConnectionTracker _tracker;
        private readonly UserId _userId;
        private readonly string _connectionId;
        private readonly CancellationTokenSource _cts;

        public RevokingPresenceService(
            IPresenceService inner,
            InMemoryConnectionTracker tracker,
            UserId userId,
            string connectionId,
            CancellationTokenSource cts)
        {
            _inner = inner;
            _tracker = tracker;
            _userId = userId;
            _connectionId = connectionId;
            _cts = cts;
        }

        public async Task<IReadOnlyList<UserId>> GetEligiblePresencePeerIdsAsync(UserId userId, CancellationToken ct = default)
        {
            var peers = await _inner.GetEligiblePresencePeerIdsAsync(userId, ct);
            // Simulate concurrent session revocation completing while eligibility check was in flight.
            _tracker.RemoveConnection(_userId, _connectionId);
            _cts.Cancel();
            return peers;
        }

        public Task<UserPresenceDto?> GetPeerPresenceAsync(UserId callerId, UserId targetUserId, CancellationToken ct = default) =>
            _inner.GetPeerPresenceAsync(callerId, targetUserId, ct);

        public Task<IReadOnlyDictionary<Guid, UserPresenceDto>> GetPresenceForConversationsAsync(UserId callerId, CancellationToken ct = default) =>
            _inner.GetPresenceForConversationsAsync(callerId, ct);

        public Task RecordLastSeenAsync(UserId userId, DateTimeOffset lastSeenAtUtc, CancellationToken ct = default) =>
            _inner.RecordLastSeenAsync(userId, lastSeenAtUtc, ct);
    }

    private sealed class AbortableHubCallerContext : HubCallerContext
    {
        public AbortableHubCallerContext(string connectionId, ClaimsPrincipal user, CancellationToken abortedToken)
        {
            ConnectionId = connectionId;
            User = user;
            ConnectionAborted = abortedToken;
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted { get; }
        public override void Abort() { }
    }

    // =========================================================================
    // Test Doubles & Helpers
    // =========================================================================

    private static ClaimsPrincipal CreateClaimsPrincipal(UserId userId, Guid sessionId)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
            new Claim("SessionId", sessionId.ToString())
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        return new ClaimsPrincipal(identity);
    }

    private sealed class TestCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated => Id is not null;
    }

    private sealed class CapturingReceiptNotifier : IRealtimeNotifier
    {
        public List<(UserId RecipientId, ReceiptUpdateDto Receipt)> ReceiptUpdates { get; } = new();
        public List<(UserId RecipientId, TypingIndicatorDto Typing)> TypingIndicators { get; } = new();
        public List<(IReadOnlyList<UserId> Recipients, UserPresenceDto Presence)> PresenceChanges { get; } = new();

        public Task NotifyNewMessageAsync(ConversationId conversationId, MessageDto message, CancellationToken ct = default) => Task.CompletedTask;

        public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, ReceiptUpdateDto receipt, CancellationToken ct = default)
        {
            ReceiptUpdates.Add((recipientUserId, receipt));
            return Task.CompletedTask;
        }

        public Task NotifyTypingAsync(UserId recipientUserId, TypingIndicatorDto typing, CancellationToken ct = default)
        {
            TypingIndicators.Add((recipientUserId, typing));
            return Task.CompletedTask;
        }

        public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, UserPresenceDto presence, CancellationToken ct = default)
        {
            PresenceChanges.Add((recipientUserIds, presence));
            return Task.CompletedTask;
        }

        public Task NotifyMessageEditedAsync(ConversationId conversationId, MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyReactionUpdatedAsync(ConversationId conversationId, ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
        public Task NotifyUserProfileUpdatedAsync(UserId userId, UserProfileUpdatedDto profile, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DummyHub : Hub
    {
    }

    private sealed class TestHubCallerContext : HubCallerContext
    {
        private readonly Action? _abortAction;

        public TestHubCallerContext(string connectionId, ClaimsPrincipal? user = null, Action? abortAction = null)
        {
            ConnectionId = connectionId;
            User = user;
            _abortAction = abortAction;
        }

        public override string ConnectionId { get; }
        public override string? UserIdentifier => User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        public override ClaimsPrincipal? User { get; }
        public override IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();
        public override IFeatureCollection Features { get; } = new FeatureCollection();
        public override CancellationToken ConnectionAborted => CancellationToken.None;
        public override void Abort() => _abortAction?.Invoke();
    }

    private sealed class FakeSessionService : ISessionService
    {
        private readonly HashSet<Guid> _activeSessions;

        public FakeSessionService(HashSet<Guid> activeSessions)
        {
            _activeSessions = activeSessions;
        }

        public Task<IReadOnlyList<UserSessionDto>> GetActiveSessionsAsync(UserId userId, Guid? currentSessionId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UserSessionDto>>(Array.Empty<UserSessionDto>());

        public Task<Guid> CreateSessionAsync(UserId userId, string deviceName, string? ipAddress, string? userAgent, CancellationToken ct = default) =>
            Task.FromResult(Guid.NewGuid());

        public Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult(_activeSessions.Contains(sessionId));

        public Task UpdateSessionActivityAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> RevokeSessionAsync(UserId userId, Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task RevokeOtherSessionsAsync(UserId userId, Guid currentSessionId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task RevokeAllSessionsAsync(UserId userId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
