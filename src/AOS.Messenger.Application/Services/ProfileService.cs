using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Services;

public sealed class ProfileService : IProfileService
{
    private readonly IUserReader _userReader;
    private readonly IUserWriter _userWriter;
    private readonly IBlockPersistence _blockPersistence;

    public ProfileService(
        IUserReader userReader,
        IUserWriter userWriter,
        IBlockPersistence blockPersistence)
    {
        _userReader = userReader;
        _userWriter = userWriter;
        _blockPersistence = blockPersistence;
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

        return await _userWriter.UpdateProfileAsync(userId, trimmedDisplayName, trimmedBio, ct);
    }
}
