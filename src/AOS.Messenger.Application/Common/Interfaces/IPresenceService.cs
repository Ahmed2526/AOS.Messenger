using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface IPresenceService
{
    Task<IReadOnlyDictionary<Guid, UserPresenceDto>> GetPresenceForConversationsAsync(UserId callerId, CancellationToken ct = default);
    Task<UserPresenceDto?> GetPeerPresenceAsync(UserId callerId, UserId targetUserId, CancellationToken ct = default);
    Task<IReadOnlyList<UserId>> GetEligiblePresencePeerIdsAsync(UserId userId, CancellationToken ct = default);
    Task RecordLastSeenAsync(UserId userId, DateTimeOffset lastSeenAtUtc, CancellationToken ct = default);
}
