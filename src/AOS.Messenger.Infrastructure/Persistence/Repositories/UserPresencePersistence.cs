using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of user presence and last seen timestamp persistence.
/// </summary>
public sealed class UserPresencePersistence : IUserPresencePersistence
{
    private readonly ApplicationDbContext _db;

    public UserPresencePersistence(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task UpdateLastSeenAsync(UserId userId, DateTimeOffset lastSeenAtUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        await _db.Users
            .Where(u => u.Id == userId.Value)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAtUtc, lastSeenAtUtc), ct);
    }

    public async Task<IReadOnlyDictionary<UserId, DateTimeOffset?>> GetLastSeenBatchAsync(
        IEnumerable<UserId> userIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        var distinctIds = userIds
            .Where(u => u != null)
            .Select(u => u.Value)
            .Distinct()
            .ToList();

        if (distinctIds.Count == 0)
        {
            return new Dictionary<UserId, DateTimeOffset?>();
        }

        var result = new Dictionary<UserId, DateTimeOffset?>();

        foreach (var chunk in distinctIds.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var rows = await _db.Users
                .AsNoTracking()
                .Where(u => chunkList.Contains(u.Id) && !u.IsDeleted && u.EmailConfirmed)
                .Select(u => new { u.Id, u.LastSeenAtUtc })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                result[new UserId(row.Id)] = row.LastSeenAtUtc;
            }
        }

        return result;
    }
}
