using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Services;

public sealed class UserDiscoveryService : IUserDiscoveryService
{
    private readonly IUserReader _userReader;
    private readonly IBlockPersistence _blockPersistence;

    public UserDiscoveryService(IUserReader userReader, IBlockPersistence blockPersistence)
    {
        _userReader = userReader;
        _blockPersistence = blockPersistence;
    }

    public async Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(
        UserId currentUserId,
        string query,
        int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
        {
            return [];
        }

        limit = Math.Clamp(limit, 1, 100);

        var normalizedQuery = query.Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return [];
        }

        // Get all bidirectional blocked user IDs for the current user
        var blockedUserIds = await _blockPersistence.GetBidirectionalBlockedUserIdsAsync(currentUserId, ct);

        return await _userReader.SearchUsersAsync(currentUserId, normalizedQuery, blockedUserIds, limit, ct);
    }
}
