using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.DTOs;

public sealed record UserProfileDto(
    UserId Id,
    string UserName,
    string DisplayName,
    string? Bio,
    bool IsDeleted);

public sealed record UpdateProfileDto(
    string DisplayName,
    string? Bio);

public sealed record UserSearchResultDto(
    UserId Id,
    string UserName,
    string DisplayName,
    string? Bio);

public sealed record BlockedUserDto(
    UserId Id,
    string UserName,
    string DisplayName,
    DateTimeOffset BlockedAtUtc);
