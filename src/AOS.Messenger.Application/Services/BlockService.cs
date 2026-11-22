using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Services;

public sealed class BlockService : IBlockService
{
    private readonly IBlockPersistence _blockPersistence;
    private readonly IUserReader _userReader;
    private readonly TimeProvider _timeProvider;

    public BlockService(
        IBlockPersistence blockPersistence,
        IUserReader userReader,
        TimeProvider? timeProvider = null)
    {
        _blockPersistence = blockPersistence;
        _userReader = userReader;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<bool> BlockUserAsync(UserId blockerId, UserId targetUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(blockerId);
        ArgumentNullException.ThrowIfNull(targetUserId);

        if (blockerId == targetUserId)
        {
            throw new DomainException("A user cannot block themselves.");
        }

        var targetUser = await _userReader.GetByIdAsync(targetUserId, ct);
        if (targetUser is null)
        {
            throw new NotFoundException("User", targetUserId.Value);
        }

        var alreadyBlocked = await _blockPersistence.IsBlockedAsync(blockerId, targetUserId, ct);
        if (alreadyBlocked)
        {
            return true;
        }

        var block = UserBlock.Create(blockerId, targetUserId, _timeProvider.GetUtcNow());

        await _blockPersistence.AddBlockAsync(block, ct);

        return true;
    }

    public async Task<bool> UnblockUserAsync(UserId blockerId, UserId targetUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(blockerId);
        ArgumentNullException.ThrowIfNull(targetUserId);

        await _blockPersistence.RemoveBlockAsync(blockerId, targetUserId, ct);
        return true;
    }

    public async Task<IReadOnlyList<BlockedUserDto>> GetBlockedUsersAsync(UserId blockerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(blockerId);

        var blocks = await _blockPersistence.GetBlocksByBlockerIdAsync(blockerId, ct);
        if (blocks.Count == 0)
        {
            return [];
        }

        var blockedUserIds = blocks.Select(b => b.BlockedUserId).Distinct().ToList();
        var userProfiles = await _userReader.GetByIdsAsync(blockedUserIds, ct);
        var profilesById = userProfiles.ToDictionary(p => p.Id);

        var result = new List<BlockedUserDto>(blocks.Count);
        foreach (var block in blocks)
        {
            if (profilesById.TryGetValue(block.BlockedUserId, out var profile))
            {
                var displayName = profile.IsDeleted ? "Deleted Account" : profile.DisplayName;
                var userName = profile.IsDeleted ? "deleted_user" : profile.UserName;

                result.Add(new BlockedUserDto(
                    block.BlockedUserId,
                    userName,
                    displayName,
                    block.CreatedAtUtc));
            }
            else
            {
                result.Add(new BlockedUserDto(
                    block.BlockedUserId,
                    "deleted_user",
                    "Deleted Account",
                    block.CreatedAtUtc));
            }
        }

        return result;
    }

    public async Task<bool> IsBlockedBidirectionalAsync(UserId userA, UserId userB, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userA);
        ArgumentNullException.ThrowIfNull(userB);

        return await _blockPersistence.IsBlockedBidirectionalAsync(userA, userB, ct);
    }
}
