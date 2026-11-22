using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface IUserDiscoveryService
{
    Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(
        UserId currentUserId,
        string query,
        int limit = 20,
        CancellationToken ct = default);
}
