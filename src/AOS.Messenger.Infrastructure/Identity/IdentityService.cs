using System.Security.Claims;
using System.Text.RegularExpressions;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Identity;

public sealed class IdentityService : IIdentityService
{
    private static readonly ApplicationUser DummyUser = new();
    private static readonly string DummyPasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(DummyUser, "DummyTimingSafePassword123!");
    private static readonly Regex UsernameRegex = new(@"^[a-zA-Z0-9._-]{3,32}$", RegexOptions.Compiled);

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ISessionService _sessionService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ApplicationDbContext _dbContext;
    private readonly IPasswordHasher<ApplicationUser> _passwordHasher;
    private readonly ISessionConnectionTerminator _terminator;
    private readonly ISessionCache _sessionCache;
    private readonly TimeProvider _timeProvider;

    public IdentityService(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ISessionService sessionService,
        IHttpContextAccessor httpContextAccessor,
        ApplicationDbContext dbContext,
        IPasswordHasher<ApplicationUser>? passwordHasher = null,
        ISessionConnectionTerminator? terminator = null,
        ISessionCache? sessionCache = null,
        TimeProvider? timeProvider = null)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _sessionService = sessionService;
        _httpContextAccessor = httpContextAccessor;
        _dbContext = dbContext;
        _passwordHasher = passwordHasher ?? new PasswordHasher<ApplicationUser>();
        _terminator = terminator ?? NullSessionConnectionTerminator.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionCache = sessionCache ?? new SessionCache(_timeProvider);
    }

    public async Task<(bool Succeeded, UserId? UserId, IEnumerable<string> Errors)> RegisterAsync(
        RegisterUserDto dto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.UserName))
        {
            return (false, null, ["Username is required."]);
        }

        var trimmedUserName = dto.UserName.Trim();
        if (!UsernameRegex.IsMatch(trimmedUserName))
        {
            return (false, null, ["Username must be between 3 and 32 characters and contain only letters, numbers, periods, hyphens, and underscores."]);
        }

        if (string.IsNullOrWhiteSpace(dto.Email))
        {
            return (false, null, ["Email is required."]);
        }

        var user = new ApplicationUser
        {
            UserName = trimmedUserName,
            Email = dto.Email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? trimmedUserName : dto.DisplayName.Trim(),
            CreatedAtUtc = _timeProvider.GetUtcNow()
        };

        var result = await _userManager.CreateAsync(user, dto.Password);

        if (!result.Succeeded)
        {
            return (false, null, result.Errors.Select(e => e.Description));
        }

        return (true, user.ToDomainUserId(), []);
    }

    public async Task<(bool Succeeded, bool RequiresTwoFactor, bool IsLockedOut, bool IsDeleted, string? Error)> LoginAsync(
        LoginUserDto dto, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var identifier = dto.UserNameOrEmail.Trim();
        var user = await _userManager.FindByNameAsync(identifier)
                   ?? await _userManager.FindByEmailAsync(identifier);

        if (user is null)
        {
            _passwordHasher.VerifyHashedPassword(DummyUser, DummyPasswordHash, dto.Password);
            return (false, false, false, false, "Invalid username or password.");
        }

        if (user.IsDeleted)
        {
            _passwordHasher.VerifyHashedPassword(user, user.PasswordHash ?? DummyPasswordHash, dto.Password);
            return (false, false, false, false, "Invalid username or password.");
        }

        if (!user.EmailConfirmed)
        {
            _passwordHasher.VerifyHashedPassword(user, user.PasswordHash ?? DummyPasswordHash, dto.Password);
            return (false, false, false, false, "Invalid username or password.");
        }

        var result = await _signInManager.CheckPasswordSignInAsync(user, dto.Password, lockoutOnFailure: true);

        if (result.IsLockedOut)
        {
            return (false, false, true, false, "Account is temporarily locked out due to multiple failed login attempts.");
        }

        if (result.RequiresTwoFactor)
        {
            return (false, true, false, false, null);
        }

        if (!result.Succeeded)
        {
            return (false, false, false, false, "Invalid username or password.");
        }

        var deviceName = ParseDeviceName(userAgent);
        var sessionId = await _sessionService.CreateSessionAsync(
            user.ToDomainUserId(), deviceName, ipAddress, userAgent, ct);

        var additionalClaims = new[]
        {
            new Claim("SessionId", sessionId.ToString()),
            new Claim("DisplayName", user.DisplayName ?? user.UserName ?? string.Empty)
        };

        await _signInManager.SignInWithClaimsAsync(user, dto.RememberMe, additionalClaims);

        return (true, false, false, false, null);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext?.User is not null)
        {
            var sessionClaim = httpContext.User.FindFirstValue("SessionId");
            var userClaim = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);

            if (Guid.TryParse(sessionClaim, out var sessionId) && Guid.TryParse(userClaim, out var userGuid))
            {
                await _sessionService.RevokeSessionAsync(new UserId(userGuid), sessionId, ct);
            }
        }

        if (_httpContextAccessor.HttpContext is not null)
        {
            await _signInManager.SignOutAsync();
        }
    }

    public async Task<bool> SoftDeleteAccountAsync(UserId userId, CancellationToken ct = default)
    {
        var deleted = false;
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _dbContext.ChangeTracker.Clear();

            var user = await _userManager.FindByIdAsync(userId.Value.ToString());
            if (user is null)
            {
                deleted = false;
                return;
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
            try
            {
                var now = _timeProvider.GetUtcNow();
                user.IsDeleted = true;
                user.DeletedAtUtc = now;
                user.ScheduledPermanentDeletionAtUtc = now.AddDays(30);

                await _userManager.UpdateSecurityStampAsync(user);
                await _userManager.UpdateAsync(user);

                var sessions = await _dbContext.UserSessions
                    .Where(s => s.UserId == userId.Value && !s.IsRevoked)
                    .ToListAsync(ct);

                foreach (var session in sessions)
                {
                    session.IsRevoked = true;
                    session.RevokedAtUtc = now;
                    _sessionCache.Invalidate(session.Id);
                }

                await _dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                deleted = true;
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });

        if (!deleted)
        {
            return false;
        }

        await _terminator.DisconnectUserSessionsAsync(userId, ct);

        if (_httpContextAccessor.HttpContext is not null)
        {
            await _signInManager.SignOutAsync();
        }

        return true;
    }

    public async Task<InitiateAccountRecoveryResult?> InitiateAccountRecoveryAsync(
        string usernameOrEmail, string password, CancellationToken ct = default)
    {
        var identifier = usernameOrEmail.Trim();
        var user = await _userManager.FindByNameAsync(identifier)
                   ?? await _userManager.FindByEmailAsync(identifier);

        if (user is null || !user.IsDeleted)
        {
            _passwordHasher.VerifyHashedPassword(DummyUser, DummyPasswordHash, password);
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        if (!user.DeletedAtUtc.HasValue || now > user.DeletedAtUtc.Value.AddDays(30))
        {
            _passwordHasher.VerifyHashedPassword(DummyUser, DummyPasswordHash, password);
            return null;
        }

        var checkPassword = await _userManager.CheckPasswordAsync(user, password);
        if (!checkPassword)
        {
            return null;
        }

        var token = await _userManager.GenerateUserTokenAsync(user, TokenOptions.DefaultProvider, "AccountRecovery");
        return new InitiateAccountRecoveryResult(user.ToDomainUserId(), user.Email!, user.DisplayName, token);
    }

    public async Task<bool> ConfirmAccountRecoveryAsync(
        UserId userId, string token, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.Value.ToString());
        if (user is null || !user.IsDeleted)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        if (!user.DeletedAtUtc.HasValue || now > user.DeletedAtUtc.Value.AddDays(30))
        {
            return false;
        }

        var valid = await _userManager.VerifyUserTokenAsync(user, TokenOptions.DefaultProvider, "AccountRecovery", token);
        if (!valid)
        {
            return false;
        }

        user.IsDeleted = false;
        user.DeletedAtUtc = null;
        user.ScheduledPermanentDeletionAtUtc = null;

        await _userManager.UpdateSecurityStampAsync(user);
        var result = await _userManager.UpdateAsync(user);
        return result.Succeeded;
    }

    public async Task<bool> RecoverAccountAsync(string usernameOrEmail, string password, CancellationToken ct = default)
    {
        var initiated = await InitiateAccountRecoveryAsync(usernameOrEmail, password, ct);
        if (initiated is null)
        {
            return false;
        }

        return await ConfirmAccountRecoveryAsync(initiated.UserId, initiated.Token, ct);
    }

    public async Task<bool> ConfirmEmailAsync(UserId userId, string token, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.Value.ToString());
        if (user is null)
        {
            return false;
        }

        var result = await _userManager.ConfirmEmailAsync(user, token);
        return result.Succeeded;
    }

    public async Task<string?> GenerateEmailConfirmationTokenAsync(UserId userId, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.Value.ToString());
        if (user is null)
        {
            return null;
        }

        return await _userManager.GenerateEmailConfirmationTokenAsync(user);
    }

    public async Task<string?> GeneratePasswordResetTokenAsync(string email, CancellationToken ct = default)
    {
        var user = await _userManager.FindByEmailAsync(email.Trim());
        if (user is null || user.IsDeleted)
        {
            _passwordHasher.VerifyHashedPassword(DummyUser, DummyPasswordHash, "dummyPassword123!");
            return null;
        }

        return await _userManager.GeneratePasswordResetTokenAsync(user);
    }

    public async Task<(bool Succeeded, IEnumerable<string> Errors)> ResetPasswordAsync(
        ResetPasswordDto dto, CancellationToken ct = default)
    {
        var trimmedEmail = dto.Email.Trim();
        (bool Succeeded, IEnumerable<string> Errors, Guid UserGuid) outcome = (false, [], Guid.Empty);
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _dbContext.ChangeTracker.Clear();

            var user = await _userManager.FindByEmailAsync(trimmedEmail);
            if (user is null || user.IsDeleted)
            {
                _passwordHasher.VerifyHashedPassword(DummyUser, DummyPasswordHash, dto.NewPassword);
                outcome = (false, ["User not found."], Guid.Empty);
                return;
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await _userManager.ResetPasswordAsync(user, dto.Token, dto.NewPassword);
                if (!result.Succeeded)
                {
                    await transaction.RollbackAsync(ct);
                    outcome = (false, result.Errors.Select(e => e.Description).ToArray(), Guid.Empty);
                    return;
                }

                await _userManager.UpdateSecurityStampAsync(user);

                var now = _timeProvider.GetUtcNow();
                var sessions = await _dbContext.UserSessions
                    .Where(s => s.UserId == user.Id && !s.IsRevoked)
                    .ToListAsync(ct);

                foreach (var session in sessions)
                {
                    session.IsRevoked = true;
                    session.RevokedAtUtc = now;
                    _sessionCache.Invalidate(session.Id);
                }

                await _dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                outcome = (true, [], user.Id);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });

        if (!outcome.Succeeded)
        {
            return (false, outcome.Errors);
        }

        await _terminator.DisconnectUserSessionsAsync(new UserId(outcome.UserGuid), ct);

        return (true, []);
    }

    public async Task<(bool Succeeded, IEnumerable<string> Errors)> ChangePasswordAsync(
        UserId userId, string currentPassword, string newPassword, CancellationToken ct = default)
    {
        (bool Succeeded, IEnumerable<string> Errors) outcome = (false, []);
        var strategy = _dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _dbContext.ChangeTracker.Clear();

            var user = await _userManager.FindByIdAsync(userId.Value.ToString());
            if (user is null)
            {
                _passwordHasher.VerifyHashedPassword(DummyUser, DummyPasswordHash, newPassword);
                outcome = (false, ["User not found."]);
                return;
            }

            if (user.IsDeleted)
            {
                throw new ForbiddenException("Account is deleted");
            }

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await _userManager.ChangePasswordAsync(user, currentPassword, newPassword);
                if (!result.Succeeded)
                {
                    await transaction.RollbackAsync(ct);
                    outcome = (false, result.Errors.Select(e => e.Description).ToArray());
                    return;
                }

                await _userManager.UpdateSecurityStampAsync(user);

                var now = _timeProvider.GetUtcNow();
                var sessions = await _dbContext.UserSessions
                    .Where(s => s.UserId == user.Id && !s.IsRevoked)
                    .ToListAsync(ct);

                foreach (var session in sessions)
                {
                    session.IsRevoked = true;
                    session.RevokedAtUtc = now;
                    _sessionCache.Invalidate(session.Id);
                }

                await _dbContext.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                outcome = (true, []);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        });

        if (!outcome.Succeeded)
        {
            return outcome;
        }

        await _terminator.DisconnectUserSessionsAsync(userId, ct);

        return (true, []);
    }

    private static string ParseDeviceName(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Desktop Web";
        }

        if (userAgent.Contains("Mobile", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase))
        {
            return "Mobile Web";
        }

        if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase))
        {
            return "macOS Desktop";
        }

        if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows Desktop";
        }

        if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase))
        {
            return "Linux Desktop";
        }

        return "Web Client";
    }
}
