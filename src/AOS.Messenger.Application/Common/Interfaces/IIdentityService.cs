using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface IIdentityService
{
    Task<(bool Succeeded, UserId? UserId, IEnumerable<string> Errors)> RegisterAsync(
        RegisterUserDto dto, CancellationToken ct = default);

    Task<(bool Succeeded, bool RequiresTwoFactor, bool IsLockedOut, bool IsDeleted, string? Error)> LoginAsync(
        LoginUserDto dto, string? ipAddress, string? userAgent, CancellationToken ct = default);

    Task LogoutAsync(CancellationToken ct = default);

    Task<bool> SoftDeleteAccountAsync(UserId userId, CancellationToken ct = default);

    Task<InitiateAccountRecoveryResult?> InitiateAccountRecoveryAsync(string usernameOrEmail, string password, CancellationToken ct = default);

    Task<bool> ConfirmAccountRecoveryAsync(UserId userId, string token, CancellationToken ct = default);

    Task<bool> RecoverAccountAsync(string usernameOrEmail, string password, CancellationToken ct = default);

    Task<bool> ConfirmEmailAsync(UserId userId, string token, CancellationToken ct = default);

    Task<string?> GenerateEmailConfirmationTokenAsync(UserId userId, CancellationToken ct = default);

    Task<string?> GeneratePasswordResetTokenAsync(string email, CancellationToken ct = default);

    Task<(bool Succeeded, IEnumerable<string> Errors)> ResetPasswordAsync(
        ResetPasswordDto dto, CancellationToken ct = default);

    Task<(bool Succeeded, IEnumerable<string> Errors)> ChangePasswordAsync(
        UserId userId, string currentPassword, string newPassword, CancellationToken ct = default);
}
