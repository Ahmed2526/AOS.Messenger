using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AOS.Messenger.Application.Services;

public sealed class ProfileService : IProfileService
{
    private readonly IUserReader _userReader;
    private readonly IUserWriter _userWriter;
    private readonly IBlockPersistence _blockPersistence;
    private readonly IRealtimeNotifier _realtimeNotifier;
    private readonly ILogger<ProfileService>? _logger;

    [ActivatorUtilitiesConstructor]
    public ProfileService(
        IUserReader userReader,
        IUserWriter userWriter,
        IBlockPersistence blockPersistence,
        IRealtimeNotifier realtimeNotifier,
        ILogger<ProfileService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(userReader);
        ArgumentNullException.ThrowIfNull(userWriter);
        ArgumentNullException.ThrowIfNull(blockPersistence);
        ArgumentNullException.ThrowIfNull(realtimeNotifier);

        _userReader = userReader;
        _userWriter = userWriter;
        _blockPersistence = blockPersistence;
        _realtimeNotifier = realtimeNotifier;
        _logger = logger;
    }

    public async Task<UserProfileDto?> GetProfileAsync(UserId callerId, UserId targetUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(targetUserId);

        if (callerId != targetUserId)
        {
            var isBlocked = await _blockPersistence.IsBlockedBidirectionalAsync(callerId, targetUserId, ct);
            if (isBlocked)
            {
                return null;
            }

            var isVerified = await _userReader.ExistsAsync(targetUserId, ct);
            if (!isVerified)
            {
                return null;
            }

            var targetUser = await _userReader.GetByIdAsync(targetUserId, ct);
            if (targetUser is null || targetUser.IsDeleted)
            {
                return null;
            }

            return targetUser;
        }

        var callerUser = await _userReader.GetByIdAsync(callerId, ct);
        if (callerUser is null || callerUser.IsDeleted)
        {
            return null;
        }

        return callerUser;
    }

    public Task<UserProfileDto?> GetProfileAsync(UserId userId, CancellationToken ct = default)
        => GetProfileAsync(userId, userId, ct);

    public async Task<bool> UpdateProfileAsync(UserId userId, UpdateProfileDto dto, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.DisplayName))
        {
            return false;
        }

        var trimmedDisplayName = dto.DisplayName.Trim();
        if (trimmedDisplayName.Length > 64)
        {
            return false;
        }

        var trimmedBio = string.IsNullOrWhiteSpace(dto.Bio) ? null : dto.Bio.Trim();
        if (trimmedBio is not null && trimmedBio.Length > 500)
        {
            return false;
        }

        var updated = await _userWriter.UpdateProfileAsync(userId, trimmedDisplayName, trimmedBio, ct);
        if (updated)
        {
            try
            {
                var profile = await _userReader.GetByIdAsync(userId, ct);
                await _realtimeNotifier.NotifyUserProfileUpdatedAsync(
                    userId,
                    new UserProfileUpdatedDto(userId, trimmedDisplayName, profile?.AvatarVersion),
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _logger?.LogWarning(ex, "Failed to broadcast profile update notification for user {UserId}.", userId.Value);
            }
        }

        return updated;
    }
}
