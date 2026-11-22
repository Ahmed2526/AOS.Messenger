using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.DTOs;

public sealed record RegisterUserDto(
    string UserName,
    string Email,
    string DisplayName,
    string Password);

public sealed record LoginUserDto(
    string UserNameOrEmail,
    string Password,
    bool RememberMe);

public sealed record UserSessionDto(
    Guid Id,
    string DeviceName,
    string? IpAddress,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset LastActiveAtUtc,
    bool IsCurrent);

public sealed record ForgotPasswordDto(string Email);

public sealed record ResetPasswordDto(
    string Email,
    string Token,
    string NewPassword);

public sealed record InitiateAccountRecoveryResult(
    UserId UserId,
    string Email,
    string? DisplayName,
    string Token);

