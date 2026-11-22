using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface IBlockService
{
    Task<bool> BlockUserAsync(UserId blockerId, UserId targetUserId, CancellationToken ct = default);
    Task<bool> UnblockUserAsync(UserId blockerId, UserId targetUserId, CancellationToken ct = default);
    Task<IReadOnlyList<BlockedUserDto>> GetBlockedUsersAsync(UserId blockerId, CancellationToken ct = default);
    Task<bool> IsBlockedBidirectionalAsync(UserId userA, UserId userB, CancellationToken ct = default);
}
