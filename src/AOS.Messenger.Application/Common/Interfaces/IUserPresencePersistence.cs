using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface IUserPresencePersistence
{
    Task UpdateLastSeenAsync(UserId userId, DateTimeOffset lastSeenAtUtc, CancellationToken ct = default);
    Task<IReadOnlyDictionary<UserId, DateTimeOffset?>> GetLastSeenBatchAsync(IEnumerable<UserId> userIds, CancellationToken ct = default);
}
