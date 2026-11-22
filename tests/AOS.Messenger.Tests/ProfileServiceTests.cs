using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Application.Services;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class ProfileServiceTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public ProfileServiceTests(SqlTestDatabaseFixture fixture)
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
    public async Task GetProfileAsync_ReturnsNull_WhenUserDoesNotExist()
    {
        var (provider, _, _) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var profile = await service.GetProfileAsync(new UserId(Guid.NewGuid()));

        Assert.Null(profile);
    }

    [Fact]
    public async Task GetProfileAsync_ReturnsNull_WhenUserIsSoftDeleted()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "testuser",
            Email = "test@example.com",
            DisplayName = "Test User",
            Bio = "Original Bio",
            IsDeleted = true,
            DeletedAtUtc = DateTimeOffset.UtcNow
        };
        await userManager.CreateAsync(user, "Password123!");

        var profile = await service.GetProfileAsync(user.ToDomainUserId());

        Assert.Null(profile);
    }

    [Fact]
    public async Task GetProfileAsync_ReturnsActiveProfile_WhenUserExists()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "activeuser",
            Email = "active@example.com",
            DisplayName = "Active User",
            Bio = "Hello world!",
            IsDeleted = false
        };
        await userManager.CreateAsync(user, "Password123!");

        var profile = await service.GetProfileAsync(user.ToDomainUserId());

        Assert.NotNull(profile);
        Assert.Equal(user.ToDomainUserId(), profile.Id);
        Assert.Equal("activeuser", profile.UserName);
        Assert.Equal("Active User", profile.DisplayName);
        Assert.Equal("Hello world!", profile.Bio);
        Assert.False(profile.IsDeleted);
    }

    [Fact]
    public async Task UpdateProfileAsync_UpdatesDisplayNameAndBio_AndNeverTouchesUserNameOrEmail()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "immutableuser",
            Email = "immutable@example.com",
            DisplayName = "Old Display Name",
            Bio = "Old Bio"
        };
        await userManager.CreateAsync(user, "Password123!");

        var success = await service.UpdateProfileAsync(
            user.ToDomainUserId(),
            new UpdateProfileDto("  New Display Name  ", "  Updated Bio  "));

        Assert.True(success);

        var updated = await userManager.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(updated);
        Assert.Equal("New Display Name", updated.DisplayName);
        Assert.Equal("Updated Bio", updated.Bio);
        Assert.Equal("immutableuser", updated.UserName);
        Assert.Equal("immutable@example.com", updated.Email);
    }

    [Fact]
    public async Task UpdateProfileAsync_ClearsBio_WhenBioIsWhitespaceOrEmpty()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "bioclear",
            Email = "bioclear@example.com",
            DisplayName = "Bio Clear",
            Bio = "Existing Bio"
        };
        await userManager.CreateAsync(user, "Password123!");

        var success = await service.UpdateProfileAsync(
            user.ToDomainUserId(),
            new UpdateProfileDto("Bio Clear", "   "));

        Assert.True(success);

        var updated = await userManager.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(updated);
        Assert.Null(updated.Bio);
    }

    [Fact]
    public async Task UpdateProfileAsync_Fails_WhenDisplayNameIsWhitespace()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "noname",
            Email = "noname@example.com",
            DisplayName = "Valid Name",
            Bio = "Bio"
        };
        await userManager.CreateAsync(user, "Password123!");

        var success = await service.UpdateProfileAsync(
            user.ToDomainUserId(),
            new UpdateProfileDto("   ", "New Bio"));

        Assert.False(success);

        var unchanged = await userManager.FindByIdAsync(user.Id.ToString());
        Assert.NotNull(unchanged);
        Assert.Equal("Valid Name", unchanged.DisplayName);
    }

    [Fact]
    public async Task UpdateProfileAsync_Fails_WhenUserIsSoftDeleted()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "deleteduser",
            Email = "deleted@example.com",
            DisplayName = "Deleted User",
            IsDeleted = true,
            DeletedAtUtc = DateTimeOffset.UtcNow
        };
        await userManager.CreateAsync(user, "Password123!");

        var success = await service.UpdateProfileAsync(
            user.ToDomainUserId(),
            new UpdateProfileDto("Trying To Update", "New Bio"));

        Assert.False(success);
    }

    [Fact]
    public async Task UpdateProfileAsync_Fails_WhenUserDoesNotExist()
    {
        var (provider, _, _) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var success = await service.UpdateProfileAsync(
            new UserId(Guid.NewGuid()),
            new UpdateProfileDto("Any Name", "Any Bio"));

        Assert.False(success);
    }

    [Fact]
    public async Task UpdateProfileAsync_Fails_WhenDisplayNameExceeds64Chars()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "longname",
            Email = "longname@example.com",
            DisplayName = "Valid Name",
            Bio = "Bio"
        };
        await userManager.CreateAsync(user, "Password123!");

        var tooLongName = new string('a', 65);
        var success = await service.UpdateProfileAsync(
            user.ToDomainUserId(),
            new UpdateProfileDto(tooLongName, "New Bio"));

        Assert.False(success);
    }

    [Fact]
    public async Task UpdateProfileAsync_Succeeds_WhenDisplayNameIsExactly64Chars()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "exact64",
            Email = "exact64@example.com",
            DisplayName = "Valid Name",
            Bio = "Bio"
        };
        await userManager.CreateAsync(user, "Password123!");

        var exact64Name = new string('a', 64);
        var success = await service.UpdateProfileAsync(
            user.ToDomainUserId(),
            new UpdateProfileDto(exact64Name, "New Bio"));

        Assert.True(success);
    }

    [Fact]
    public void ApplicationUserConfiguration_EnforcesDisplayNameMaxLength64()
    {
        var (provider, db, _) = CreateTestEnvironment();
        var entityType = db.Model.FindEntityType(typeof(ApplicationUser));
        Assert.NotNull(entityType);

        var displayNameProp = entityType.FindProperty(nameof(ApplicationUser.DisplayName));
        Assert.NotNull(displayNameProp);
        Assert.Equal(64, displayNameProp.GetMaxLength());
    }

    [Fact]
    public async Task UpdateProfileAsync_Fails_WhenBioExceeds500Chars()
    {
        var (provider, _, userManager) = CreateTestEnvironment();
        var service = provider.GetRequiredService<IProfileService>();

        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = "longbio",
            Email = "longbio@example.com",
            DisplayName = "Valid Name",
            Bio = "Bio"
        };
        await userManager.CreateAsync(user, "Password123!");

        var tooLongBio = new string('b', 501);
        var success = await service.UpdateProfileAsync(
            user.ToDomainUserId(),
            new UpdateProfileDto("Valid Name", tooLongBio));

        Assert.False(success);
    }
}
