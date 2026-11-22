using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Persistence.Repositories;

public sealed class BlockPersistence : IBlockPersistence
{
    private readonly ApplicationDbContext _db;

    public BlockPersistence(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<bool> IsBlockedAsync(UserId userA, UserId userB, CancellationToken ct = default)
    {
        return await _db.UserBlocks
            .AsNoTracking()
            .AnyAsync(b => b.BlockerId == userA && b.BlockedUserId == userB, ct);
    }

    public async Task<bool> IsBlockedBidirectionalAsync(UserId userA, UserId userB, CancellationToken ct = default)
    {
        return await _db.UserBlocks
            .AsNoTracking()
            .AnyAsync(b => (b.BlockerId == userA && b.BlockedUserId == userB) ||
                           (b.BlockerId == userB && b.BlockedUserId == userA), ct);
    }

    public async Task<UserBlock?> GetBlockAsync(UserId blockerId, UserId blockedUserId, CancellationToken ct = default)
    {
        return await _db.UserBlocks
            .FirstOrDefaultAsync(b => b.BlockerId == blockerId && b.BlockedUserId == blockedUserId, ct);
    }

    public async Task AddBlockAsync(UserBlock block, CancellationToken ct = default)
    {
        try
        {
            _db.UserBlocks.Add(block);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _db.Entry(block).State = EntityState.Detached;
            // Idempotency: handle concurrent race if block was already created
            var exists = await IsBlockedAsync(block.BlockerId, block.BlockedUserId, ct);
            if (!exists)
            {
                throw new ConflictException("Failed to add block due to database conflict.", ex);
            }
        }
    }

    public async Task RemoveBlockAsync(UserId blockerId, UserId blockedUserId, CancellationToken ct = default)
    {
        var block = await _db.UserBlocks
            .FirstOrDefaultAsync(b => b.BlockerId == blockerId && b.BlockedUserId == blockedUserId, ct);

        if (block is not null)
        {
            _db.UserBlocks.Remove(block);
            await _db.SaveChangesAsync(ct);
        }
    }

    public async Task<IReadOnlyList<UserId>> GetBlockedUserIdsAsync(UserId blockerId, CancellationToken ct = default)
    {
        return await _db.UserBlocks
            .AsNoTracking()
            .Where(b => b.BlockerId == blockerId)
            .Select(b => b.BlockedUserId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<UserBlock>> GetBlocksByBlockerIdAsync(UserId blockerId, CancellationToken ct = default)
    {
        return await _db.UserBlocks
            .AsNoTracking()
            .Where(b => b.BlockerId == blockerId)
            .OrderByDescending(b => b.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlySet<UserId>> GetBidirectionalBlockedUserIdsAsync(UserId userId, CancellationToken ct = default)
    {
        var blockedByMe = await _db.UserBlocks
            .AsNoTracking()
            .Where(b => b.BlockerId == userId)
            .Select(b => b.BlockedUserId)
            .ToListAsync(ct);

        var blockedMe = await _db.UserBlocks
            .AsNoTracking()
            .Where(b => b.BlockedUserId == userId)
            .Select(b => b.BlockerId)
            .ToListAsync(ct);

        var set = new HashSet<UserId>(blockedByMe);
        foreach (var id in blockedMe)
        {
            set.Add(id);
        }

        return set;
    }
}
