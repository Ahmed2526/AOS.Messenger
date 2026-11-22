using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
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

public sealed class ConversationServiceTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public ConversationServiceTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private (IServiceProvider Provider, ApplicationDbContext Db, UserManager<ApplicationUser> UserManager) CreateTestEnvironment()
    {
        var scope = _fixture.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        return (scope.ServiceProvider, db, userManager);
    }

    private static async Task<ApplicationUser> CreateUserAsync(
        UserManager<ApplicationUser> userManager,
        string userName,
        string displayName,
        string email,
        string? bio = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"{userName}_{suffix}",
            DisplayName = displayName,
            Email = $"{suffix}_{email}",
            Bio = bio,
            EmailConfirmed = true
        };

        var result = await userManager.CreateAsync(user, "Password123!");
        Assert.True(result.Succeeded);
        return user;
    }

    [Fact]
    public async Task GetOrCreateConversationAsync_CreatesConversation_WithValidTargetUser()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com", "Alice's bio");
        var userB = await CreateUserAsync(userManager, "bob", "Bob Builder", "bob@test.com", "Bob's bio");

        var callerId = new UserId(userA.Id);
        var targetId = new UserId(userB.Id);

        var result = await service.GetOrCreateConversationAsync(callerId, targetId);

        Assert.NotNull(result);
        Assert.Equal(targetId, result.OtherParticipantId);
        Assert.Equal("Bob Builder", result.OtherParticipantName);
        Assert.Equal(userB.UserName, result.OtherParticipantHandle);
        Assert.Equal("Bob's bio", result.OtherParticipantBio);
        Assert.False(result.IsBlocked);
        Assert.False(result.IsPinned);
        Assert.False(result.IsArchived);
        Assert.False(result.IsMuted);
    }

    [Fact]
    public async Task GetOrCreateConversationAsync_RejectsSelfConversation_WithDomainException()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com");
        var callerId = new UserId(userA.Id);

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.GetOrCreateConversationAsync(callerId, callerId));

        Assert.Contains("oneself", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetOrCreateConversationAsync_RejectsNonExistentTarget_WithNotFoundException()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com");
        var callerId = new UserId(userA.Id);
        var nonExistentTargetId = new UserId(Guid.NewGuid());

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetOrCreateConversationAsync(callerId, nonExistentTargetId));
    }

    [Fact]
    public async Task GetOrCreateConversationAsync_RejectsUnconfirmedEmailTarget_WithNotFoundException()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com");
        var callerId = new UserId(userA.Id);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var unconfirmedUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"unconfirmed_{suffix}",
            DisplayName = "Unconfirmed User",
            Email = $"{suffix}_unconfirmed@test.com",
            EmailConfirmed = false
        };
        await userManager.CreateAsync(unconfirmedUser, "Password123!");

        var targetId = new UserId(unconfirmedUser.Id);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetOrCreateConversationAsync(callerId, targetId));
    }

    [Fact]
    public async Task GetOrCreateConversationAsync_RejectsSoftDeletedTarget_WithNotFoundException()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com");
        var callerId = new UserId(userA.Id);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var deletedUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"deleted_{suffix}",
            DisplayName = "Deleted User",
            Email = $"{suffix}_deleted@test.com",
            EmailConfirmed = true,
            IsDeleted = true,
            DeletedAtUtc = DateTimeOffset.UtcNow
        };
        await userManager.CreateAsync(deletedUser, "Password123!");

        var targetId = new UserId(deletedUser.Id);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetOrCreateConversationAsync(callerId, targetId));
    }

    [Fact]
    public async Task GetOrCreateConversationAsync_RejectsBlockedUser_WithForbiddenException()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();
        var blockService = provider.GetRequiredService<IBlockService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com");
        var userB = await CreateUserAsync(userManager, "bob", "Bob Builder", "bob@test.com");

        var userAId = new UserId(userA.Id);
        var userBId = new UserId(userB.Id);

        // Scenario 1: Alice blocks Bob
        await blockService.BlockUserAsync(userAId, userBId);

        var ex1 = await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetOrCreateConversationAsync(userAId, userBId));
        Assert.Contains("blocked", ex1.Message, StringComparison.OrdinalIgnoreCase);

        // Bob also cannot start a conversation with Alice
        var ex2 = await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetOrCreateConversationAsync(userBId, userAId));
        Assert.Contains("blocked", ex2.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetOrCreateConversationAsync_ReturnsExistingConversation_Idempotently()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com");
        var userB = await CreateUserAsync(userManager, "bob", "Bob Builder", "bob@test.com");

        var userAId = new UserId(userA.Id);
        var userBId = new UserId(userB.Id);

        var conv1 = await service.GetOrCreateConversationAsync(userAId, userBId);
        var conv2 = await service.GetOrCreateConversationAsync(userBId, userAId);
        var conv3 = await service.GetOrCreateConversationAsync(userAId, userBId);

        Assert.Equal(conv1.Id, conv2.Id);
        Assert.Equal(conv1.Id, conv3.Id);
    }

    [Fact]
    public async Task GetConversationAsync_ThrowsNotFound_WhenConversationDoesNotExist()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com");
        var userAId = new UserId(userA.Id);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            service.GetConversationAsync(userAId, ConversationId.New()));
    }

    [Fact]
    public async Task GetConversationAsync_RejectsNonParticipant_WithForbiddenException()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com");
        var userB = await CreateUserAsync(userManager, "bob", "Bob Builder", "bob@test.com");
        var intruder = await CreateUserAsync(userManager, "charlie", "Charlie Intruder", "charlie@test.com");

        var userAId = new UserId(userA.Id);
        var userBId = new UserId(userB.Id);
        var intruderId = new UserId(intruder.Id);

        var conv = await service.GetOrCreateConversationAsync(userAId, userBId);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.GetConversationAsync(intruderId, conv.Id));
    }

    [Fact]
    public async Task PerUserStateMutations_UpdateOnlyCallerState_AndIsolateOtherUser()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com");
        var userB = await CreateUserAsync(userManager, "bob", "Bob Builder", "bob@test.com");

        var userAId = new UserId(userA.Id);
        var userBId = new UserId(userB.Id);

        var conv = await service.GetOrCreateConversationAsync(userAId, userBId);

        // Alice pins, mutes, archives, clears, and deletes conversation
        await service.PinConversationAsync(userAId, conv.Id, true);
        await service.MuteConversationAsync(userAId, conv.Id, true);
        await service.ArchiveConversationAsync(userAId, conv.Id, true);
        await service.ClearChatAsync(userAId, conv.Id);
        await service.DeleteConversationAsync(userAId, conv.Id);

        // Alice's view reflects her mutations
        var aliceView = await service.GetConversationAsync(userAId, conv.Id);
        Assert.True(aliceView.IsPinned);
        Assert.True(aliceView.IsMuted);
        Assert.True(aliceView.IsArchived);

        // Bob's view must be completely default (unpinned, unmuted, unarchived)
        var bobView = await service.GetConversationAsync(userBId, conv.Id);
        Assert.False(bobView.IsPinned);
        Assert.False(bobView.IsMuted);
        Assert.False(bobView.IsArchived);

        // Alice unpins, unmutes, unarchives
        await service.PinConversationAsync(userAId, conv.Id, false);
        await service.MuteConversationAsync(userAId, conv.Id, false);
        await service.ArchiveConversationAsync(userAId, conv.Id, false);

        var aliceViewAfter = await service.GetConversationAsync(userAId, conv.Id);
        Assert.False(aliceViewAfter.IsPinned);
        Assert.False(aliceViewAfter.IsMuted);
        Assert.False(aliceViewAfter.IsArchived);
    }

    [Fact]
    public async Task PerUserStateMutations_RejectNonParticipant_WithForbiddenException()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userA = await CreateUserAsync(userManager, "alice", "Alice Wonder", "alice@test.com");
        var userB = await CreateUserAsync(userManager, "bob", "Bob Builder", "bob@test.com");
        var intruder = await CreateUserAsync(userManager, "charlie", "Charlie Intruder", "charlie@test.com");

        var userAId = new UserId(userA.Id);
        var userBId = new UserId(userB.Id);
        var intruderId = new UserId(intruder.Id);

        var conv = await service.GetOrCreateConversationAsync(userAId, userBId);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.PinConversationAsync(intruderId, conv.Id, true));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.ArchiveConversationAsync(intruderId, conv.Id, true));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.MuteConversationAsync(intruderId, conv.Id, true));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.ClearChatAsync(intruderId, conv.Id));

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.DeleteConversationAsync(intruderId, conv.Id));
    }

    [Fact]
    public async Task ConversationList_OrdersPinnedFirst_AndFiltersDeletedProperly()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userMe = await CreateUserAsync(userManager, "me", "My Name", "me@test.com");
        var user1 = await CreateUserAsync(userManager, "user1", "User One", "user1@test.com");
        var user2 = await CreateUserAsync(userManager, "user2", "User Two", "user2@test.com");
        var user3 = await CreateUserAsync(userManager, "user3", "User Three", "user3@test.com");

        var meId = new UserId(userMe.Id);
        var id1 = new UserId(user1.Id);
        var id2 = new UserId(user2.Id);
        var id3 = new UserId(user3.Id);

        var conv1 = await service.GetOrCreateConversationAsync(meId, id1);
        var conv2 = await service.GetOrCreateConversationAsync(meId, id2);
        var conv3 = await service.GetOrCreateConversationAsync(meId, id3);

        // conv2 has a message sent 10 minutes ago
        var baseTime = DateTimeOffset.UtcNow;
        var msg2 = Message.Create(conv2.Id, id2, "k2", "Msg in conv 2", baseTime.AddMinutes(-10));
        db.Messages.Add(msg2);
        await db.SaveChangesAsync();

        // Pin conv1 (no messages, but pinned)
        await service.PinConversationAsync(meId, conv1.Id, true);

        // Delete conv3 (deleted at baseTime, no subsequent messages -> must be filtered out)
        await service.DeleteConversationAsync(meId, conv3.Id);

        var list = await service.GetConversationListAsync(meId, includeArchived: false);

        // Conv3 should be filtered out
        Assert.DoesNotContain(list, c => c.Id == conv3.Id);

        // Conv1 and Conv2 should be present
        Assert.Equal(2, list.Count);

        // Conv1 is pinned, so it must be first
        Assert.Equal(conv1.Id, list[0].Id);
        Assert.True(list[0].IsPinned);

        // Conv2 is second
        Assert.Equal(conv2.Id, list[1].Id);
        Assert.False(list[1].IsPinned);
        Assert.Equal("Msg in conv 2", list[1].LastMessagePreview);
        Assert.Equal(1, list[1].UnreadCount);

        // Now Bob sends a new message in Conv3 AFTER deletion -> Conv3 should reappear!
        var msg3 = Message.Create(conv3.Id, id3, "k3", "New message after delete", DateTimeOffset.UtcNow.AddSeconds(2));
        db.Messages.Add(msg3);
        await db.SaveChangesAsync();

        var listAfterNewMsg = await service.GetConversationListAsync(meId, includeArchived: false);
        Assert.Equal(3, listAfterNewMsg.Count);
        Assert.Contains(listAfterNewMsg, c => c.Id == conv3.Id);
    }

    [Fact]
    public async Task ConversationList_FiltersArchived_BasedOnIncludeArchived()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userMe = await CreateUserAsync(userManager, "me", "My Name", "me@test.com");
        var user1 = await CreateUserAsync(userManager, "user1", "User One", "user1@test.com");
        var user2 = await CreateUserAsync(userManager, "user2", "User Two", "user2@test.com");

        var meId = new UserId(userMe.Id);
        var id1 = new UserId(user1.Id);
        var id2 = new UserId(user2.Id);

        var conv1 = await service.GetOrCreateConversationAsync(meId, id1);
        var conv2 = await service.GetOrCreateConversationAsync(meId, id2);

        // Archive conv1
        await service.ArchiveConversationAsync(meId, conv1.Id, true);

        // includeArchived = false (default) -> only active conv2
        var activeOnly = await service.GetConversationListAsync(meId, includeArchived: false);
        Assert.Single(activeOnly);
        Assert.Equal(conv2.Id, activeOnly[0].Id);

        // includeArchived = true -> both conv1 and conv2
        var all = await service.GetConversationListAsync(meId, includeArchived: true);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, c => c.Id == conv1.Id && c.IsArchived);
        Assert.Contains(all, c => c.Id == conv2.Id && !c.IsArchived);
    }

    [Fact]
    public async Task ConversationList_Batched_CorrectlyComputesUnreadAndClearedBoundaries()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IConversationService>();

        var userMe = await CreateUserAsync(userManager, "me", "My Name", "me@test.com");
        var user1 = await CreateUserAsync(userManager, "user1", "User One", "user1@test.com");
        var user2 = await CreateUserAsync(userManager, "user2", "User Two", "user2@test.com");

        var meId = new UserId(userMe.Id);
        var id1 = new UserId(user1.Id);
        var id2 = new UserId(user2.Id);

        var conv1 = await service.GetOrCreateConversationAsync(meId, id1);
        var conv2 = await service.GetOrCreateConversationAsync(meId, id2);

        var now = DateTimeOffset.UtcNow;

        // Conv 1: 3 messages from user1
        var m1_1 = Message.Create(conv1.Id, id1, "c1-m1", "First message", now);
        var m1_2 = Message.Create(conv1.Id, id1, "c1-m2", "Second message", now.AddMinutes(1));
        var m1_3 = Message.Create(conv1.Id, id1, "c1-m3", "Third message", now.AddMinutes(2));

        // Conv 2: 2 messages from user2
        var m2_1 = Message.Create(conv2.Id, id2, "c2-m1", "Conv2 message 1", now);
        var m2_2 = Message.Create(conv2.Id, id2, "c2-m2", "Conv2 message 2", now.AddMinutes(1));

        db.Messages.AddRange(m1_1, m1_2, m1_3, m2_1, m2_2);
        await db.SaveChangesAsync();

        // Me has read up to m1_2 in conv1
        var state1 = UserConversationState.Create(conv1.Id, meId);
        state1.MarkRead(m1_2.Id, now.AddMinutes(1));
        db.UserConversationStates.Add(state1);

        // Me cleared chat in conv2 before m2_2
        var state2 = UserConversationState.Create(conv2.Id, meId);
        state2.ClearChat(now.AddSeconds(30)); // clears m2_1
        db.UserConversationStates.Add(state2);

        await db.SaveChangesAsync();

        var list = await service.GetConversationListAsync(meId);

        Assert.Equal(2, list.Count);

        var c1Summary = list.Single(c => c.Id == conv1.Id);
        Assert.Equal(1, c1Summary.UnreadCount); // Only m1_3 is unread
        Assert.Equal("Third message", c1Summary.LastMessagePreview);

        var c2Summary = list.Single(c => c.Id == conv2.Id);
        Assert.Equal(1, c2Summary.UnreadCount); // Only m2_2 is unread (m2_1 was cleared)
        Assert.Equal("Conv2 message 2", c2Summary.LastMessagePreview);
    }
}
