using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Persistence.Repositories;

public sealed class UserReader : IUserReader
{
    private readonly ApplicationDbContext _db;

    public UserReader(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<UserProfileDto?> GetByIdAsync(UserId userId, CancellationToken ct = default)
    {
        var guid = userId.Value;
        return await _db.Users
            .AsNoTracking()
            .Where(u => u.Id == guid)
            .Select(u => new UserProfileDto(
                new UserId(u.Id),
                u.UserName ?? string.Empty,
                u.DisplayName,
                u.Bio,
                u.IsDeleted,
                u.IsDeleted ? null : u.AvatarVersion))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<UserProfileDto>> GetByIdsAsync(IEnumerable<UserId> userIds, CancellationToken ct = default)
    {
        var guids = userIds.Select(u => u.Value).Distinct().ToList();
        if (guids.Count == 0)
        {
            return [];
        }

        var results = new List<UserProfileDto>(guids.Count);
        foreach (var chunk in guids.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var chunkResults = await _db.Users
                .AsNoTracking()
                .Where(u => chunkList.Contains(u.Id))
                .Select(u => new UserProfileDto(
                    new UserId(u.Id),
                    u.UserName ?? string.Empty,
                    u.DisplayName,
                    u.Bio,
                    u.IsDeleted,
                    u.IsDeleted ? null : u.AvatarVersion))
                .ToListAsync(ct);

            results.AddRange(chunkResults);
        }

        return results;
    }

    public async Task<UserProfileDto?> GetByUserNameAsync(string userName, CancellationToken ct = default)
    {
        var normalized = userName.Trim().ToUpperInvariant();
        return await _db.Users
            .AsNoTracking()
            .Where(u => u.NormalizedUserName == normalized)
            .Select(u => new UserProfileDto(
                new UserId(u.Id),
                u.UserName ?? string.Empty,
                u.DisplayName,
                u.Bio,
                u.IsDeleted,
                u.IsDeleted ? null : u.AvatarVersion))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<UserProfileDto?> GetByEmailAsync(string email, CancellationToken ct = default)
    {
        var normalized = email.Trim().ToUpperInvariant();
        return await _db.Users
            .AsNoTracking()
            .Where(u => u.NormalizedEmail == normalized)
            .Select(u => new UserProfileDto(
                new UserId(u.Id),
                u.UserName ?? string.Empty,
                u.DisplayName,
                u.Bio,
                u.IsDeleted,
                u.IsDeleted ? null : u.AvatarVersion))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<bool> ExistsAsync(UserId userId, CancellationToken ct = default)
    {
        var guid = userId.Value;
        return await _db.Users
            .AsNoTracking()
            .AnyAsync(u => u.Id == guid && !u.IsDeleted && u.EmailConfirmed, ct);
    }

    public async Task<IReadOnlySet<UserId>> GetActiveUserIdsAsync(IEnumerable<UserId> userIds, CancellationToken ct = default)
    {
        var guids = userIds.Select(u => u.Value).Distinct().ToList();
        if (guids.Count == 0)
        {
            return new HashSet<UserId>();
        }

        var active = new HashSet<UserId>(guids.Count);
        foreach (var chunk in guids.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var ids = await _db.Users
                .AsNoTracking()
                .Where(u => chunkList.Contains(u.Id) && !u.IsDeleted && u.EmailConfirmed)
                .Select(u => u.Id)
                .ToListAsync(ct);

            foreach (var id in ids)
            {
                active.Add(new UserId(id));
            }
        }

        return active;
    }

    public async Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(
        UserId currentUserId,
        string query,
        IReadOnlySet<UserId> excludedUserIds,
        int limit,
        CancellationToken ct = default)
    {
        var excludedGuids = excludedUserIds.Select(u => u.Value).ToHashSet();
        var currentGuid = currentUserId.Value;
        var trimmed = query.Trim();
        var normalizedEmail = trimmed.ToUpperInvariant();

        if (excludedGuids.Count > 500)
        {
            var candidates = await _db.Users
                .AsNoTracking()
                .Where(u => !u.IsDeleted && u.EmailConfirmed && u.Id != currentGuid)
                .Where(u => (u.UserName != null && u.UserName.Contains(trimmed)) ||
                            u.NormalizedEmail == normalizedEmail ||
                            u.DisplayName.Contains(trimmed))
                .OrderBy(u => u.UserName != null && u.UserName.StartsWith(trimmed) ? 0 : 1)
                .ThenBy(u => u.DisplayName.StartsWith(trimmed) ? 0 : 1)
                .ThenBy(u => u.UserName)
                .Take(limit + 500)
                .Select(u => new UserSearchResultDto(
                    new UserId(u.Id),
                    u.UserName ?? string.Empty,
                    u.DisplayName,
                    u.Bio,
                    u.AvatarVersion))
                .ToListAsync(ct);

            return candidates.Where(u => !excludedGuids.Contains(u.Id.Value)).Take(limit).ToList();
        }

        return await _db.Users
            .AsNoTracking()
            .Where(u => !u.IsDeleted && u.EmailConfirmed && u.Id != currentGuid && !excludedGuids.Contains(u.Id))
            .Where(u => (u.UserName != null && u.UserName.Contains(trimmed)) ||
                        u.NormalizedEmail == normalizedEmail ||
                        u.DisplayName.Contains(trimmed))
            .OrderBy(u => u.UserName != null && u.UserName.StartsWith(trimmed) ? 0 : 1)
            .ThenBy(u => u.DisplayName.StartsWith(trimmed) ? 0 : 1)
            .ThenBy(u => u.UserName)
            .Take(limit)
            .Select(u => new UserSearchResultDto(
                new UserId(u.Id),
                u.UserName ?? string.Empty,
                u.DisplayName,
                u.Bio,
                u.AvatarVersion))
            .ToListAsync(ct);
    }
}
