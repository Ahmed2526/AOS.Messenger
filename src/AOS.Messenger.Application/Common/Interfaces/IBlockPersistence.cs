using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Persistence contract for user blocks.
/// Implemented by Infrastructure using EF Core.
/// </summary>
public interface IBlockPersistence
{
    Task<bool> IsBlockedAsync(UserId userA, UserId userB, CancellationToken ct = default);
    Task<bool> IsBlockedBidirectionalAsync(UserId userA, UserId userB, CancellationToken ct = default);
    Task<UserBlock?> GetBlockAsync(UserId blockerId, UserId blockedUserId, CancellationToken ct = default);
    Task AddBlockAsync(UserBlock block, CancellationToken ct = default);
    Task RemoveBlockAsync(UserId blockerId, UserId blockedUserId, CancellationToken ct = default);
    Task<IReadOnlyList<UserId>> GetBlockedUserIdsAsync(UserId blockerId, CancellationToken ct = default);
    Task<IReadOnlyList<UserBlock>> GetBlocksByBlockerIdAsync(UserId blockerId, CancellationToken ct = default);
    Task<IReadOnlySet<UserId>> GetBidirectionalBlockedUserIdsAsync(UserId userId, CancellationToken ct = default);
}
