using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
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

public sealed class BlockServiceTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public BlockServiceTests(SqlTestDatabaseFixture fixture)
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

    [Fact]
    public async Task BlockUserAsync_ThrowsDomainException_WhenUserBlocksThemselves()
    {
        var (provider, _, _) = CreateTestEnvironment();
        var blockService = provider.GetRequiredService<IBlockService>();
        var userId = new UserId(Guid.NewGuid());

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            blockService.BlockUserAsync(userId, userId));

        Assert.Contains("cannot block themselves", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BlockUserAsync_ThrowsNotFoundException_WhenTargetUserDoesNotExist()
    {
        var (provider, _, _) = CreateTestEnvironment();
        var blockService = provider.GetRequiredService<IBlockService>();

        var blockerId = new UserId(Guid.NewGuid());
        var nonExistentTargetId = new UserId(Guid.NewGuid());

        await Assert.ThrowsAsync<NotFoundException>(() =>
            blockService.BlockUserAsync(blockerId, nonExistentTargetId));
    }

    [Fact]
    public async Task BlockUserAsync_Succeeds_AndIsIdempotent()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var blockService = provider.GetRequiredService<IBlockService>();

        var userA = new ApplicationUser { Id = Guid.NewGuid(), UserName = "user_a", Email = "a@test.com", DisplayName = "User A" };
        var userB = new ApplicationUser { Id = Guid.NewGuid(), UserName = "user_b", Email = "b@test.com", DisplayName = "User B" };
        await userManager.CreateAsync(userA, "Password123!");
        await userManager.CreateAsync(userB, "Password123!");

        // First block: should return true
        var res1 = await blockService.BlockUserAsync(userA.ToDomainUserId(), userB.ToDomainUserId());
        Assert.True(res1);

        // Second block (duplicate/idempotent): should return true without error
        var res2 = await blockService.BlockUserAsync(userA.ToDomainUserId(), userB.ToDomainUserId());
        Assert.True(res2);

        // Check bidirectional check
        var isBlocked = await blockService.IsBlockedBidirectionalAsync(userA.ToDomainUserId(), userB.ToDomainUserId());
        Assert.True(isBlocked);

        var isBlockedReverse = await blockService.IsBlockedBidirectionalAsync(userB.ToDomainUserId(), userA.ToDomainUserId());
        Assert.True(isBlockedReverse);
    }

    [Fact]
    public async Task UnblockUserAsync_RemovesBlockSuccessfully()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var blockService = provider.GetRequiredService<IBlockService>();

        var userA = new ApplicationUser { Id = Guid.NewGuid(), UserName = "user_a2", Email = "a2_unblock@test.com", DisplayName = "User A2" };
        var userB = new ApplicationUser { Id = Guid.NewGuid(), UserName = "user_b2", Email = "b2_unblock@test.com", DisplayName = "User B2" };
        await userManager.CreateAsync(userA, "Password123!");
        await userManager.CreateAsync(userB, "Password123!");

        await blockService.BlockUserAsync(userA.ToDomainUserId(), userB.ToDomainUserId());
        Assert.True(await blockService.IsBlockedBidirectionalAsync(userA.ToDomainUserId(), userB.ToDomainUserId()));

        var unblockRes = await blockService.UnblockUserAsync(userA.ToDomainUserId(), userB.ToDomainUserId());
        Assert.True(unblockRes);

        Assert.False(await blockService.IsBlockedBidirectionalAsync(userA.ToDomainUserId(), userB.ToDomainUserId()));
    }

    [Fact]
    public async Task GetBlockedUsersAsync_ReturnsBlockedUsersWithDetails()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var blockService = provider.GetRequiredService<IBlockService>();

        var userA = new ApplicationUser { Id = Guid.NewGuid(), UserName = "blocker_user", Email = "blocker@test.com", DisplayName = "Blocker" };
        var userB = new ApplicationUser { Id = Guid.NewGuid(), UserName = "blocked_user_1", Email = "b1@test.com", DisplayName = "Blocked One" };
        var userC = new ApplicationUser { Id = Guid.NewGuid(), UserName = "blocked_user_2", Email = "b2@test.com", DisplayName = "Blocked Two" };
        await userManager.CreateAsync(userA, "Password123!");
        await userManager.CreateAsync(userB, "Password123!");
        await userManager.CreateAsync(userC, "Password123!");

        await blockService.BlockUserAsync(userA.ToDomainUserId(), userB.ToDomainUserId());
        await blockService.BlockUserAsync(userA.ToDomainUserId(), userC.ToDomainUserId());

        var blockedList = await blockService.GetBlockedUsersAsync(userA.ToDomainUserId());
        Assert.Equal(2, blockedList.Count);
        Assert.Contains(blockedList, u => u.Id == userB.ToDomainUserId() && u.UserName == "blocked_user_1" && u.DisplayName == "Blocked One");
        Assert.Contains(blockedList, u => u.Id == userC.ToDomainUserId() && u.UserName == "blocked_user_2" && u.DisplayName == "Blocked Two");
    }

    [Fact]
    public async Task GetBlockedUsersAsync_HandlesSoftDeletedUsers_WithPlaceholder()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var blockService = provider.GetRequiredService<IBlockService>();

        var userA = new ApplicationUser { Id = Guid.NewGuid(), UserName = "blocker_del", Email = "del_b@test.com", DisplayName = "Blocker" };
        var userB = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "original_deleted",
            Email = "del@test.com",
            DisplayName = "Original Name",
            IsDeleted = true,
            DeletedAtUtc = DateTimeOffset.UtcNow
        };
        await userManager.CreateAsync(userA, "Password123!");
        await userManager.CreateAsync(userB, "Password123!");

        await blockService.BlockUserAsync(userA.ToDomainUserId(), userB.ToDomainUserId());

        var blockedList = await blockService.GetBlockedUsersAsync(userA.ToDomainUserId());
        Assert.Single(blockedList);
        Assert.Equal(userB.ToDomainUserId(), blockedList[0].Id);
        Assert.Equal("deleted_user", blockedList[0].UserName);
        Assert.Equal("Deleted Account", blockedList[0].DisplayName);
    }

    [Fact]
    public async Task IsBlockedBidirectionalAsync_ReturnsFalse_WhenNeitherBlockedTheOther()
    {
        var (provider, _, _) = CreateTestEnvironment();
        var blockService = provider.GetRequiredService<IBlockService>();

        var userA = new UserId(Guid.NewGuid());
        var userB = new UserId(Guid.NewGuid());

        var result = await blockService.IsBlockedBidirectionalAsync(userA, userB);
        Assert.False(result);
    }

    [Fact]
    public async Task BlockUserAsync_UsesInjectedTimeProvider_ForBlockCreatedAtUtc()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var blockPersistence = provider.GetRequiredService<IBlockPersistence>();
        var userReader = provider.GetRequiredService<IUserReader>();

        var fixedTime = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(fixedTime);
        var blockService = new BlockService(blockPersistence, userReader, timeProvider);

        var userA = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"userA_{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.com"
        };
        var userB = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"userB_{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@test.com"
        };
        await userManager.CreateAsync(userA, "Password123!");
        await userManager.CreateAsync(userB, "Password123!");

        var result = await blockService.BlockUserAsync(userA.ToDomainUserId(), userB.ToDomainUserId());
        Assert.True(result);

        var blockEntity = await db.UserBlocks.FirstOrDefaultAsync(b => b.BlockerId == userA.ToDomainUserId() && b.BlockedUserId == userB.ToDomainUserId());
        Assert.NotNull(blockEntity);
        Assert.Equal(fixedTime, blockEntity.CreatedAtUtc);
    }
}
