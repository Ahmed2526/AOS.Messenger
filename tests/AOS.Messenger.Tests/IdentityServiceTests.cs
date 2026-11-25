using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class IdentityServiceTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public IdentityServiceTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private IServiceProvider BuildTestServiceProvider()
    {
        return _fixture.CreateServiceProvider();
    }

    [Fact]
    public async Task RegisterAsync_RejectsEmptyUsernameOrEmail()
    {
        var provider = BuildTestServiceProvider();
        var service = provider.GetRequiredService<IIdentityService>();

        var res1 = await service.RegisterAsync(new RegisterUserDto("", "user@example.com", "User", "Password123!"));
        Assert.False(res1.Succeeded);
        Assert.Contains(res1.Errors, e => e.Contains("Username"));

        var res2 = await service.RegisterAsync(new RegisterUserDto("validuser", "", "User", "Password123!"));
        Assert.False(res2.Succeeded);
        Assert.Contains(res2.Errors, e => e.Contains("Email"));
    }

    [Fact]
    public async Task RegisterAsync_SucceedsWithValidCredentials()
    {
        var provider = BuildTestServiceProvider();
        var service = provider.GetRequiredService<IIdentityService>();

        var res = await service.RegisterAsync(
            new RegisterUserDto("alice_wonder", "alice@example.com", "Alice W.", "Password123!"));

        Assert.True(res.Succeeded);
        Assert.NotNull(res.UserId);
        Assert.NotEqual(Guid.Empty, res.UserId.Value);
    }

    [Theory]
    [InlineData("ab")] // Too short (< 3)
    [InlineData("this_username_is_way_too_long_for_registration_33_chars")] // Too long (> 32)
    [InlineData("user@name")] // Invalid character '@'
    [InlineData("user name")] // Space
    [InlineData("user!name")] // Special char '!'
    [InlineData("user#name")] // Special char '#'
    [InlineData("юзернейм")] // Non-ASCII characters
    public async Task RegisterAsync_RejectsInvalidUsernameRegex(string invalidUsername)
    {
        var provider = BuildTestServiceProvider();
        var service = provider.GetRequiredService<IIdentityService>();

        var res = await service.RegisterAsync(
            new RegisterUserDto(invalidUsername, $"{Guid.NewGuid():N}@example.com", "Test User", "Password123!"));

        Assert.False(res.Succeeded);
        Assert.Contains(res.Errors, e => e.Contains("Username"));
    }

    [Theory]
    [InlineData("abc")] // Exactly 3 chars
    [InlineData("valid_user-123.test")] // Letters, digits, dots, hyphens, underscores
    [InlineData("username-exactly-32-chars-long12")] // Exactly 32 chars
    public async Task RegisterAsync_AcceptsValidUsernameRegex(string validUsername)
    {
        var provider = BuildTestServiceProvider();
        var service = provider.GetRequiredService<IIdentityService>();

        var res = await service.RegisterAsync(
            new RegisterUserDto(validUsername, $"{Guid.NewGuid():N}@example.com", "Test User", "Password123!"));

        Assert.True(res.Succeeded);
        Assert.NotNull(res.UserId);
    }

    [Fact]
    public async Task RegisterAsync_RejectsDuplicateUsernameOrEmail()
    {
        var provider = BuildTestServiceProvider();
        var service = provider.GetRequiredService<IIdentityService>();

        var res1 = await service.RegisterAsync(
            new RegisterUserDto("bob_builder", "bob@example.com", "Bob B.", "Password123!"));
        Assert.True(res1.Succeeded);

        // Duplicate username
        var res2 = await service.RegisterAsync(
            new RegisterUserDto("bob_builder", "bob2@example.com", "Bob B.", "Password123!"));
        Assert.False(res2.Succeeded);

        // Duplicate email
        var res3 = await service.RegisterAsync(
            new RegisterUserDto("bob_other", "bob@example.com", "Bob B.", "Password123!"));
        Assert.False(res3.Succeeded);
    }

    [Fact]
    public async Task SoftDeleteAccountAsync_MarksUserDeletedAndSetsRecoveryWindow()
    {
        var provider = BuildTestServiceProvider();
        var service = provider.GetRequiredService<IIdentityService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        var registerRes = await service.RegisterAsync(
            new RegisterUserDto("charlie", "charlie@example.com", "Charlie", "Password123!"));
        Assert.True(registerRes.Succeeded);

        var softDeleteRes = await service.SoftDeleteAccountAsync(registerRes.UserId!);
        Assert.True(softDeleteRes);

        var user = await userManager.FindByIdAsync(registerRes.UserId!.Value.ToString());
        Assert.NotNull(user);
        Assert.True(user.IsDeleted);
        Assert.NotNull(user.DeletedAtUtc);
        Assert.NotNull(user.ScheduledPermanentDeletionAtUtc);
    }

    [Fact]
    public async Task LoginAsync_UnverifiedUser_ReturnsGenericError_AndPreservesTimingParity()
    {
        var services = _fixture.CreateServiceCollection();
        var hasher = new TrackingPasswordHasher();
        services.AddSingleton<IPasswordHasher<ApplicationUser>>(hasher);
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IIdentityService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        var reg = await service.RegisterAsync(new RegisterUserDto("unverified_user", "unverified@example.com", "Unverified", "Password123!"));
        Assert.True(reg.Succeeded);
        var user = await userManager.FindByIdAsync(reg.UserId!.Value.ToString());
        Assert.NotNull(user);
        Assert.False(user.EmailConfirmed);

        // Login with correct password must fail with generic error
        var initialHashCount = hasher.VerifyCount;
        var resCorrectPassword = await service.LoginAsync(new LoginUserDto("unverified_user", "Password123!", false), "127.0.0.1", "UA");
        Assert.False(resCorrectPassword.Succeeded);
        Assert.Equal("Invalid username or password.", resCorrectPassword.Error);
        Assert.True(hasher.VerifyCount > initialHashCount, "Password hasher must execute to preserve timing parity.");

        // Login with wrong password must return the exact same generic error
        var countBeforeWrong = hasher.VerifyCount;
        var resWrongPassword = await service.LoginAsync(new LoginUserDto("unverified_user", "WrongPassword123!", false), "127.0.0.1", "UA");
        Assert.False(resWrongPassword.Succeeded);
        Assert.Equal("Invalid username or password.", resWrongPassword.Error);
        Assert.True(hasher.VerifyCount > countBeforeWrong, "Password hasher must execute on bad password.");

        // Nonexistent user returns the exact same generic error
        var resNonExistent = await service.LoginAsync(new LoginUserDto("no_such_user", "Password123!", false), "127.0.0.1", "UA");
        Assert.False(resNonExistent.Succeeded);
        Assert.Equal("Invalid username or password.", resNonExistent.Error);

        // After confirming email, login succeeds
        user.EmailConfirmed = true;
        await userManager.UpdateAsync(user);

        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = new DefaultHttpContext { RequestServices = provider };

        var resConfirmed = await service.LoginAsync(new LoginUserDto("unverified_user", "Password123!", false), "127.0.0.1", "UA");
        Assert.True(resConfirmed.Succeeded);
        Assert.Null(resConfirmed.Error);
    }

    [Fact]
    public async Task UnverifiedAccountCleanupService_PurgesExpiredAccounts_AndLeavesRecentOrVerifiedUntouched()
    {
        var services = _fixture.CreateServiceCollection();
        var now = new DateTimeOffset(2026, 11, 21, 14, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        services.AddSingleton<TimeProvider>(timeProvider);
        var provider = services.BuildServiceProvider();

        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var cleanupService = new UnverifiedAccountCleanupService(scopeFactory, timeProvider);

        // 1. Expired unverified account (> 48h old)
        var expiredUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"expired_{Guid.NewGuid():N}",
            Email = $"expired_{Guid.NewGuid():N}@example.com",
            EmailConfirmed = false,
            CreatedAtUtc = now.AddHours(-49)
        };
        var res1 = await userManager.CreateAsync(expiredUser, "Password123!");
        Assert.True(res1.Succeeded);

        // 2. Recent unverified account (< 48h old)
        var recentUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"recent_{Guid.NewGuid():N}",
            Email = $"recent_{Guid.NewGuid():N}@example.com",
            EmailConfirmed = false,
            CreatedAtUtc = now.AddHours(-10)
        };
        var res2 = await userManager.CreateAsync(recentUser, "Password123!");
        Assert.True(res2.Succeeded);

        // 3. Verified account (even if old)
        var verifiedUser = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"verified_{Guid.NewGuid():N}",
            Email = $"verified_{Guid.NewGuid():N}@example.com",
            EmailConfirmed = true,
            CreatedAtUtc = now.AddHours(-100)
        };
        var res3 = await userManager.CreateAsync(verifiedUser, "Password123!");
        Assert.True(res3.Succeeded);

        // Run cleanup
        var purgedCount = await cleanupService.PurgeExpiredUnverifiedAccountsAsync();
        Assert.True(purgedCount >= 1);

        // Verify database state using a fresh scope to ensure reading committed database state
        using var verifyScope = scopeFactory.CreateScope();
        var verifyManager = verifyScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await verifyManager.FindByIdAsync(expiredUser.Id.ToString()));
        Assert.NotNull(await verifyManager.FindByIdAsync(recentUser.Id.ToString()));
        Assert.NotNull(await verifyManager.FindByIdAsync(verifiedUser.Id.ToString()));
    }

    [Fact]
    public async Task TwoStepAccountRecovery_FullWorkflow_Succeeds_AndRotatesSecurityStamp()
    {
        var provider = BuildTestServiceProvider();
        var service = provider.GetRequiredService<IIdentityService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        var registerRes = await service.RegisterAsync(
            new RegisterUserDto("dave_recover_2step", "dave2step@example.com", "Dave", "Password123!"));
        Assert.True(registerRes.Succeeded);
        var userId = registerRes.UserId!;

        await service.SoftDeleteAccountAsync(userId);
        var userBefore = await userManager.FindByIdAsync(userId.Value.ToString());
        Assert.True(userBefore!.IsDeleted);
        var originalSecurityStamp = userBefore.SecurityStamp;

        // Step A: Initiate account recovery
        var initiateResult = await service.InitiateAccountRecoveryAsync("dave_recover_2step", "Password123!");
        Assert.NotNull(initiateResult);
        Assert.Equal(userId, initiateResult.UserId);
        Assert.Equal("dave2step@example.com", initiateResult.Email);
        Assert.False(string.IsNullOrWhiteSpace(initiateResult.Token));

        // Step B: Confirm account recovery
        var confirmed = await service.ConfirmAccountRecoveryAsync(userId, initiateResult.Token);
        Assert.True(confirmed);

        var userAfter = await userManager.FindByIdAsync(userId.Value.ToString());
        Assert.NotNull(userAfter);
        Assert.False(userAfter.IsDeleted);
        Assert.Null(userAfter.DeletedAtUtc);
        Assert.Null(userAfter.ScheduledPermanentDeletionAtUtc);
        Assert.NotEqual(originalSecurityStamp, userAfter.SecurityStamp);

        // Replay of same recovery token MUST fail because SecurityStamp was rotated
        var replayResult = await service.ConfirmAccountRecoveryAsync(userId, initiateResult.Token);
        Assert.False(replayResult, "Token replay must fail due to security stamp rotation.");
    }

    [Fact]
    public async Task InitiateAccountRecoveryAsync_FailsWithInvalidPassword_OrActiveAccount()
    {
        var provider = BuildTestServiceProvider();
        var service = provider.GetRequiredService<IIdentityService>();

        var registerRes = await service.RegisterAsync(
            new RegisterUserDto("eve_recover_active", "eveactive@example.com", "Eve", "Password123!"));
        Assert.True(registerRes.Succeeded);

        // Account is active (not soft deleted) -> must return null
        var activeRes = await service.InitiateAccountRecoveryAsync("eve_recover_active", "Password123!");
        Assert.Null(activeRes);

        // Soft delete account
        await service.SoftDeleteAccountAsync(registerRes.UserId!);

        // Wrong password -> must return null
        var wrongPassRes = await service.InitiateAccountRecoveryAsync("eve_recover_active", "WrongPassword123!");
        Assert.Null(wrongPassRes);
    }

    [Fact]
    public async Task TwoStepAccountRecovery_RejectsAccountPast30Days()
    {
        var services = _fixture.CreateServiceCollection();
        var now = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(now);
        services.AddSingleton<TimeProvider>(timeProvider);
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IIdentityService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        var registerRes = await service.RegisterAsync(
            new RegisterUserDto("frank_expired_rec", "frankrec@example.com", "Frank", "Password123!"));
        Assert.True(registerRes.Succeeded);
        var userId = registerRes.UserId!;

        await service.SoftDeleteAccountAsync(userId);

        // Advance time past 30 days
        timeProvider.Advance(TimeSpan.FromDays(31));

        var initiateResult = await service.InitiateAccountRecoveryAsync("frank_expired_rec", "Password123!");
        Assert.Null(initiateResult);

        var confirmResult = await service.ConfirmAccountRecoveryAsync(userId, "any-token");
        Assert.False(confirmResult);
    }

    [Fact]
    public async Task SoftDeleteAccountAsync_RotatesSecurityStamp_RevokesSessions_AndInvokesTerminator()
    {
        var services = _fixture.CreateServiceCollection();
        var terminator = new TrackingSessionConnectionTerminator();
        services.AddSingleton<ISessionConnectionTerminator>(terminator);
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IIdentityService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var sessionService = provider.GetRequiredService<ISessionService>();
        using var db = provider.GetRequiredService<ApplicationDbContext>();

        var reg = await service.RegisterAsync(new RegisterUserDto("frank_del", "frank@example.com", "Frank", "Password123!"));
        Assert.True(reg.Succeeded);
        var userId = reg.UserId!;

        var userBefore = await userManager.FindByIdAsync(userId.Value.ToString());
        var oldSecurityStamp = userBefore!.SecurityStamp;

        var sessionId = await sessionService.CreateSessionAsync(userId, "Browser", "127.0.0.1", "Mozilla/5.0");
        Assert.True(await sessionService.IsSessionActiveAsync(sessionId));

        var deleted = await service.SoftDeleteAccountAsync(userId);
        Assert.True(deleted);

        var userAfter = await userManager.FindByIdAsync(userId.Value.ToString());
        Assert.True(userAfter!.IsDeleted);
        Assert.NotEqual(oldSecurityStamp, userAfter.SecurityStamp);

        // Session must be revoked
        var sessionInDb = await db.UserSessions.FindAsync(sessionId);
        Assert.True(sessionInDb!.IsRevoked);

        // Terminator invoked
        Assert.Contains(userId, terminator.TerminatedUserIds);
    }

    [Fact]
    public async Task ResetPasswordAsync_RotatesSecurityStamp_RevokesSessions_AndInvokesTerminator()
    {
        var services = _fixture.CreateServiceCollection();
        var terminator = new TrackingSessionConnectionTerminator();
        services.AddSingleton<ISessionConnectionTerminator>(terminator);
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IIdentityService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var sessionService = provider.GetRequiredService<ISessionService>();
        using var db = provider.GetRequiredService<ApplicationDbContext>();

        var reg = await service.RegisterAsync(new RegisterUserDto("grace_reset", "grace@example.com", "Grace", "Password123!"));
        var userId = reg.UserId!;

        var userBefore = await userManager.FindByIdAsync(userId.Value.ToString());
        var oldSecurityStamp = userBefore!.SecurityStamp;

        var sessionId = await sessionService.CreateSessionAsync(userId, "Browser", "127.0.0.1", "Mozilla/5.0");
        var token = await service.GeneratePasswordResetTokenAsync("grace@example.com");
        Assert.NotNull(token);

        var (succeeded, errors) = await service.ResetPasswordAsync(new ResetPasswordDto("grace@example.com", token, "NewPassword456!"));
        Assert.True(succeeded, string.Join(", ", errors));

        var userAfter = await userManager.FindByIdAsync(userId.Value.ToString());
        Assert.NotEqual(oldSecurityStamp, userAfter!.SecurityStamp);

        var sessionInDb = await db.UserSessions.FindAsync(sessionId);
        Assert.True(sessionInDb!.IsRevoked);

        Assert.Contains(userId, terminator.TerminatedUserIds);

        // Verify login with new password succeeds
        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = new DefaultHttpContext { RequestServices = provider };
        var passwordValid = await userManager.CheckPasswordAsync(userAfter, "NewPassword456!");
        Assert.True(passwordValid);
        var oldPasswordInvalid = await userManager.CheckPasswordAsync(userAfter, "Password123!");
        Assert.False(oldPasswordInvalid);
    }

    [Fact]
    public async Task ResetPasswordAsync_WhenUserIsSoftDeleted_ReturnsUserNotFoundWithoutOracle_AndRunsDummyHash()
    {
        var services = _fixture.CreateServiceCollection();
        var hasher = new TrackingPasswordHasher();
        services.AddSingleton<IPasswordHasher<ApplicationUser>>(hasher);
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IIdentityService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        var reg = await service.RegisterAsync(new RegisterUserDto("heidi_del", "heidi@example.com", "Heidi", "Password123!"));
        var userId = reg.UserId!;

        var token = await service.GeneratePasswordResetTokenAsync("heidi@example.com");
        Assert.NotNull(token);

        await service.SoftDeleteAccountAsync(userId);

        var verifyCountBefore = hasher.VerifyCount;
        var (succeeded, errors) = await service.ResetPasswordAsync(new ResetPasswordDto("heidi@example.com", token, "NewPassword456!"));

        Assert.False(succeeded);
        Assert.Equal(["User not found."], errors);
        Assert.True(hasher.VerifyCount > verifyCountBefore, "Dummy password verification must run when user is soft-deleted.");

        var heidiAfter = await userManager.FindByIdAsync(userId.Value.ToString());
        Assert.False(await userManager.CheckPasswordAsync(heidiAfter!, "NewPassword456!"));
        Assert.True(await userManager.CheckPasswordAsync(heidiAfter!, "Password123!"));
    }

    [Fact]
    public async Task ChangePasswordAsync_WhenUserIsSoftDeleted_ThrowsForbiddenException()
    {
        var provider = BuildTestServiceProvider();
        var service = provider.GetRequiredService<IIdentityService>();

        var reg = await service.RegisterAsync(new RegisterUserDto("ivan_del", "ivan@example.com", "Ivan", "Password123!"));
        var userId = reg.UserId!;

        await service.SoftDeleteAccountAsync(userId);

        var ex = await Assert.ThrowsAsync<ForbiddenException>(() =>
            service.ChangePasswordAsync(userId, "Password123!", "NewPassword456!"));
        Assert.Equal("Account is deleted", ex.Message);
    }

    [Fact]
    public async Task TimingSafe_LoginAsync_CallsPasswordHasher_OnUserLookupMiss()
    {
        var services = _fixture.CreateServiceCollection();
        var hasher = new TrackingPasswordHasher();
        services.AddSingleton<IPasswordHasher<ApplicationUser>>(hasher);
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IIdentityService>();

        var initialCount = hasher.VerifyCount;
        var res = await service.LoginAsync(new LoginUserDto("nonexistent_user", "AnyPassword123!", false), "127.0.0.1", "UA");

        Assert.False(res.Succeeded);
        Assert.Equal("Invalid username or password.", res.Error);
        Assert.True(hasher.VerifyCount > initialCount, "Dummy password verification must be invoked on lookup miss.");
    }

    [Fact]
    public async Task TimingSafe_RecoverAccountAsync_CallsPasswordHasher_OnLookupMiss()
    {
        var services = _fixture.CreateServiceCollection();
        var hasher = new TrackingPasswordHasher();
        services.AddSingleton<IPasswordHasher<ApplicationUser>>(hasher);
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IIdentityService>();

        var initialCount = hasher.VerifyCount;
        var res = await service.RecoverAccountAsync("nonexistent_user", "AnyPassword123!");

        Assert.False(res);
        Assert.True(hasher.VerifyCount > initialCount, "Dummy password verification must be invoked on recovery lookup miss.");
    }

    [Fact]
    public async Task TimingSafe_InitiateAccountRecoveryAsync_CallsPasswordHasher_OnLookupMiss_OrActiveUser()
    {
        var services = _fixture.CreateServiceCollection();
        var hasher = new TrackingPasswordHasher();
        services.AddSingleton<IPasswordHasher<ApplicationUser>>(hasher);
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IIdentityService>();

        // Miss
        var initialCount = hasher.VerifyCount;
        var resMiss = await service.InitiateAccountRecoveryAsync("nobody_here", "AnyPassword123!");
        Assert.Null(resMiss);
        Assert.True(hasher.VerifyCount > initialCount, "Dummy password verification must be invoked on recovery initiate miss.");

        // Active user (not deleted)
        var reg = await service.RegisterAsync(new RegisterUserDto("active_timing_user", "active_rec@example.com", "Active", "Password123!"));
        Assert.True(reg.Succeeded);

        var countBeforeActive = hasher.VerifyCount;
        var resActive = await service.InitiateAccountRecoveryAsync("active_timing_user", "AnyPassword123!");
        Assert.Null(resActive);
        Assert.True(hasher.VerifyCount > countBeforeActive, "Dummy password verification must be invoked when user is not deleted.");
    }

    [Fact]
    public async Task TimingSafe_ResetPasswordAsync_CallsPasswordHasher_OnLookupMiss()
    {
        var services = _fixture.CreateServiceCollection();
        var hasher = new TrackingPasswordHasher();
        services.AddSingleton<IPasswordHasher<ApplicationUser>>(hasher);
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IIdentityService>();

        var initialCount = hasher.VerifyCount;
        var (succeeded, errors) = await service.ResetPasswordAsync(new ResetPasswordDto("nobody@example.com", "token", "NewPass123!"));

        Assert.False(succeeded);
        Assert.True(hasher.VerifyCount > initialCount, "Dummy password verification must be invoked on reset lookup miss.");
    }

    [Fact]
    public async Task TimingSafe_GeneratePasswordResetTokenAsync_CallsPasswordHasher_OnLookupMiss_OrDeletedUser()
    {
        var services = _fixture.CreateServiceCollection();
        var hasher = new TrackingPasswordHasher();
        services.AddSingleton<IPasswordHasher<ApplicationUser>>(hasher);
        var provider = services.BuildServiceProvider();

        var service = provider.GetRequiredService<IIdentityService>();

        var initialCount = hasher.VerifyCount;
        var token = await service.GeneratePasswordResetTokenAsync("nobody@example.com");

        Assert.Null(token);
        Assert.True(hasher.VerifyCount > initialCount, "Dummy password verification must be invoked on reset token lookup miss.");

        var reg = await service.RegisterAsync(new RegisterUserDto("judy_timing", "judy@example.com", "Judy", "Password123!"));
        await service.SoftDeleteAccountAsync(reg.UserId!);

        var countBeforeDeleted = hasher.VerifyCount;
        var tokenDeleted = await service.GeneratePasswordResetTokenAsync("judy@example.com");
        Assert.Null(tokenDeleted);
        Assert.True(hasher.VerifyCount > countBeforeDeleted, "Dummy password verification must be invoked when user is deleted.");
    }
}
