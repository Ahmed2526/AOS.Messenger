using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
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

public sealed class UserDiscoveryServiceTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public UserDiscoveryServiceTests(SqlTestDatabaseFixture fixture)
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
    public async Task SearchUsersAsync_ReturnsEmpty_ForEmptyOrWhitespaceOrSymbolOnlyQuery()
    {
        var (provider, _, _) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IUserDiscoveryService>();
        var currentUserId = new UserId(Guid.NewGuid());

        Assert.Empty(await service.SearchUsersAsync(currentUserId, ""));
        Assert.Empty(await service.SearchUsersAsync(currentUserId, "   "));
        Assert.Empty(await service.SearchUsersAsync(currentUserId, "@"));
        Assert.Empty(await service.SearchUsersAsync(currentUserId, "valid", limit: 0));
        Assert.Empty(await service.SearchUsersAsync(currentUserId, "valid", limit: -5));
    }

    [Fact]
    public async Task SearchUsersAsync_ExcludesCurrentUserId()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IUserDiscoveryService>();

        var currentUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "charlie_brown",
            Email = "charlie@example.com",
            EmailConfirmed = true,
            DisplayName = "Charlie Brown"
        };
        await userManager.CreateAsync(currentUser, "Password123!");

        var results = await service.SearchUsersAsync(currentUser.ToDomainUserId(), "charlie");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchUsersAsync_ExcludesSoftDeletedUsers()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IUserDiscoveryService>();
        var currentUserId = new UserId(Guid.NewGuid());

        var deletedUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "ghost_user",
            Email = "ghost@example.com",
            EmailConfirmed = true,
            DisplayName = "Ghost User",
            IsDeleted = true,
            DeletedAtUtc = DateTimeOffset.UtcNow
        };
        await userManager.CreateAsync(deletedUser, "Password123!");

        var results = await service.SearchUsersAsync(currentUserId, "ghost");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchUsersAsync_ExcludesUsersBlockedByCurrentUser()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IUserDiscoveryService>();
        var currentUserId = new UserId(Guid.NewGuid());

        var targetUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "blocked_target",
            Email = "target@example.com",
            EmailConfirmed = true,
            DisplayName = "Target User"
        };
        await userManager.CreateAsync(targetUser, "Password123!");

        // Current user blocks target user
        var block = UserBlock.Create(currentUserId, targetUser.ToDomainUserId(), DateTimeOffset.UtcNow);
        db.UserBlocks.Add(block);
        await db.SaveChangesAsync();

        var results = await service.SearchUsersAsync(currentUserId, "target");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchUsersAsync_ExcludesUsersWhoBlockedCurrentUser_Bidirectional()
    {
        var (provider, db, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IUserDiscoveryService>();
        var currentUserId = new UserId(Guid.NewGuid());

        var blockerUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "blocker_person",
            Email = "blocker@example.com",
            EmailConfirmed = true,
            DisplayName = "Blocker Person"
        };
        await userManager.CreateAsync(blockerUser, "Password123!");

        // Blocker person blocks current user
        var block = UserBlock.Create(blockerUser.ToDomainUserId(), currentUserId, DateTimeOffset.UtcNow);
        db.UserBlocks.Add(block);
        await db.SaveChangesAsync();

        var results = await service.SearchUsersAsync(currentUserId, "blocker");
        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchUsersAsync_FindsMatches_ByUserNameOrEmailOrDisplayName_AndStripsLeadingAt()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IUserDiscoveryService>();
        var currentUserId = new UserId(Guid.NewGuid());

        var user1 = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "alice_wonderland",
            Email = "alice@example.com",
            EmailConfirmed = true,
            DisplayName = "Alice",
            Bio = "Curiouser and curiouser!"
        };
        var user2 = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "bob_builder",
            Email = "fixit@bob.com",
            EmailConfirmed = true,
            DisplayName = "Bob",
            Bio = "Can we fix it?"
        };
        await userManager.CreateAsync(user1, "Password123!");
        await userManager.CreateAsync(user2, "Password123!");

        // Search with '@' prefix on username
        var results1 = await service.SearchUsersAsync(currentUserId, "@alice");
        Assert.Single(results1);
        Assert.Equal("alice_wonderland", results1[0].UserName);
        Assert.Equal("Curiouser and curiouser!", results1[0].Bio);

        // Search by exact email match succeeds
        var results2 = await service.SearchUsersAsync(currentUserId, "fixit@bob.com");
        Assert.Single(results2);
        Assert.Equal("bob_builder", results2[0].UserName);

        // Substring email search MUST NOT match to protect email privacy (AUD-029/030)
        var results3 = await service.SearchUsersAsync(currentUserId, "fixit@");
        Assert.Empty(results3);

        var results4 = await service.SearchUsersAsync(currentUserId, "@bob.com");
        Assert.Empty(results4);
    }

    [Fact]
    public async Task SearchUsersAsync_ExcludesUnconfirmedUsers()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IUserDiscoveryService>();
        var currentUserId = new UserId(Guid.NewGuid());

        var unconfirmedUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "unconfirmed_user",
            Email = "unconfirmed@example.com",
            DisplayName = "Unconfirmed User",
            EmailConfirmed = false
        };
        await userManager.CreateAsync(unconfirmedUser, "Password123!");

        var results = await service.SearchUsersAsync(currentUserId, "unconfirmed");
        Assert.Empty(results);
    }

    [Fact]
    public void UserSearchResultDto_DoesNotExposeEmail()
    {
        var property = typeof(UserSearchResultDto).GetProperty("Email");
        Assert.Null(property);
    }

    [Fact]
    public async Task SearchUsersAsync_LimitsResults()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IUserDiscoveryService>();
        var currentUserId = new UserId(Guid.NewGuid());

        for (int i = 1; i <= 5; i++)
        {
            var u = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                UserName = $"match_user_{i}",
                Email = $"match{i}@example.com",
                DisplayName = $"Match User {i}",
                EmailConfirmed = true
            };
            await userManager.CreateAsync(u, "Password123!");
        }

        var results = await service.SearchUsersAsync(currentUserId, "match", limit: 3);
        Assert.Equal(3, results.Count);
    }
}
