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

public sealed class BlockPersistenceAndUserReaderTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public BlockPersistenceAndUserReaderTests(SqlTestDatabaseFixture fixture)
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
    public async Task BlockPersistence_Add_Get_Remove_WorkCorrectly()
    {
        var (provider, _, _) = CreateTestEnvironment();
        var repo = provider.GetRequiredService<IBlockPersistence>();

        var userA = new UserId(Guid.NewGuid());
        var userB = new UserId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;

        // Initially not blocked
        Assert.False(await repo.IsBlockedAsync(userA, userB));
        Assert.False(await repo.IsBlockedBidirectionalAsync(userA, userB));

        // Add block
        var block = UserBlock.Create(userA, userB, now);
        await repo.AddBlockAsync(block);

        Assert.True(await repo.IsBlockedAsync(userA, userB));
        Assert.False(await repo.IsBlockedAsync(userB, userA)); // Directional check
        Assert.True(await repo.IsBlockedBidirectionalAsync(userA, userB)); // Bidirectional check
        Assert.True(await repo.IsBlockedBidirectionalAsync(userB, userA)); // Bidirectional check

        var retrieved = await repo.GetBlockAsync(userA, userB);
        Assert.NotNull(retrieved);
        Assert.Equal(userA, retrieved.BlockerId);
        Assert.Equal(userB, retrieved.BlockedUserId);

        var blockedIds = await repo.GetBlockedUserIdsAsync(userA);
        Assert.Single(blockedIds);
        Assert.Equal(userB, blockedIds[0]);

        var bidiSet = await repo.GetBidirectionalBlockedUserIdsAsync(userB);
        Assert.Contains(userA, bidiSet);

        // Remove block
        await repo.RemoveBlockAsync(userA, userB);
        Assert.False(await repo.IsBlockedAsync(userA, userB));
        Assert.False(await repo.IsBlockedBidirectionalAsync(userA, userB));
    }

    [Fact]
    public async Task UserReader_Queries_WorkCorrectly()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var reader = provider.GetRequiredService<IUserReader>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "reader_test_user",
            Email = "reader@test.com",
            DisplayName = "Reader Test",
            Bio = "Testing user reader",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(user, "Password123!");

        var domainId = user.ToDomainUserId();

        // Create unconfirmed user
        var unconfirmedUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "unconfirmed_reader_user",
            Email = "unconfirmed@test.com",
            DisplayName = "Unconfirmed User",
            EmailConfirmed = false
        };
        await userManager.CreateAsync(unconfirmedUser, "Password123!");

        // Create deleted user
        var deletedUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "deleted_reader_user",
            Email = "deleted@test.com",
            DisplayName = "Deleted User",
            EmailConfirmed = true,
            IsDeleted = true,
            DeletedAtUtc = DateTimeOffset.UtcNow
        };
        await userManager.CreateAsync(deletedUser, "Password123!");

        // ExistsAsync requires confirmed email and !IsDeleted
        Assert.True(await reader.ExistsAsync(domainId));
        Assert.False(await reader.ExistsAsync(unconfirmedUser.ToDomainUserId()));
        Assert.False(await reader.ExistsAsync(deletedUser.ToDomainUserId()));
        Assert.False(await reader.ExistsAsync(new UserId(Guid.NewGuid())));

        // GetByIdAsync
        var byId = await reader.GetByIdAsync(domainId);
        Assert.NotNull(byId);
        Assert.Equal("reader_test_user", byId.UserName);
        Assert.Equal("Reader Test", byId.DisplayName);
        Assert.Equal("Testing user reader", byId.Bio);

        // GetByUserNameAsync
        var byName = await reader.GetByUserNameAsync("reader_test_user");
        Assert.NotNull(byName);
        Assert.Equal(domainId, byName.Id);

        // GetByEmailAsync
        var byEmail = await reader.GetByEmailAsync("reader@test.com");
        Assert.NotNull(byEmail);
        Assert.Equal(domainId, byEmail.Id);

        // GetByIdsAsync
        var byIds = await reader.GetByIdsAsync([domainId]);
        Assert.Single(byIds);
        Assert.Equal(domainId, byIds[0].Id);
    }

    [Fact]
    public async Task SearchUsersAsync_ExcludesUnconfirmedAndDeletedUsers_AndRequiresExactEmailMatch()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var reader = provider.GetRequiredService<IUserReader>();

        var currentUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "search_curr",
            Email = "search_curr@test.com",
            DisplayName = "Current Searcher",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(currentUser, "Password123!");

        var validUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "valid_search_target",
            Email = "target@test.com",
            DisplayName = "Valid Search Target",
            EmailConfirmed = true
        };
        await userManager.CreateAsync(validUser, "Password123!");

        var unconfirmedUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "unconfirmed_search_target",
            Email = "target_unconf@test.com",
            DisplayName = "Target Unconfirmed",
            EmailConfirmed = false
        };
        await userManager.CreateAsync(unconfirmedUser, "Password123!");

        var deletedUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "deleted_search_target",
            Email = "target_del@test.com",
            DisplayName = "Target Deleted",
            EmailConfirmed = true,
            IsDeleted = true,
            DeletedAtUtc = DateTimeOffset.UtcNow
        };
        await userManager.CreateAsync(deletedUser, "Password123!");

        var currentUserId = currentUser.ToDomainUserId();
        var emptyExclusions = new HashSet<UserId>();

        // Exact email match
        var emailResults = await reader.SearchUsersAsync(currentUserId, "target@test.com", emptyExclusions, 10);
        Assert.Single(emailResults);
        Assert.Equal(validUser.ToDomainUserId(), emailResults[0].Id);

        // Substring email match should NOT return any results
        var partialEmailResults = await reader.SearchUsersAsync(currentUserId, "target@", emptyExclusions, 10);
        Assert.Empty(partialEmailResults);

        // Name match finds only confirmed non-deleted user
        var nameResults = await reader.SearchUsersAsync(currentUserId, "search_target", emptyExclusions, 10);
        Assert.Single(nameResults);
        Assert.Equal(validUser.ToDomainUserId(), nameResults[0].Id);
    }
}
