using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Application read contract for projecting user records without exposing EF Core or Identity types.
/// </summary>
public interface IUserReader
{
    Task<UserProfileDto?> GetByIdAsync(UserId userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserProfileDto>> GetByIdsAsync(IEnumerable<UserId> userIds, CancellationToken ct = default);
    Task<UserProfileDto?> GetByUserNameAsync(string userName, CancellationToken ct = default);
    Task<UserProfileDto?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<bool> ExistsAsync(UserId userId, CancellationToken ct = default);
    Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(UserId currentUserId, string query, IReadOnlySet<UserId> excludedUserIds, int limit, CancellationToken ct = default);
}
