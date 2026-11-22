using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface IProfileService
{
    Task<UserProfileDto?> GetProfileAsync(UserId callerId, UserId targetUserId, CancellationToken ct = default);
    Task<UserProfileDto?> GetProfileAsync(UserId userId, CancellationToken ct = default) => GetProfileAsync(userId, userId, ct);
    Task<bool> UpdateProfileAsync(UserId userId, UpdateProfileDto dto, CancellationToken ct = default);
}
