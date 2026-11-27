using System.Data;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Constants;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core persistence implementation for sticker packs, custom R2 sticker staging/quotas,
/// favorites, isolated recents upserts, and strict IDOR access checks.
/// </summary>
public sealed class StickerPersistence : IStickerPersistence
{
    private readonly ApplicationDbContext _db;

    public StickerPersistence(ApplicationDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<StickerPack>> GetSystemPacksAsync(CancellationToken ct = default)
    {
        return await _db.StickerPacks
            .AsNoTracking()
            .Where(p => p.IsSystemPack && !p.IsDeleted)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StickerPack>> GetUserCustomPacksAsync(UserId ownerUserId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownerUserId);

        return await _db.StickerPacks
            .AsNoTracking()
            .Where(p => !p.IsSystemPack && p.OwnerUserId == ownerUserId && !p.IsDeleted)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.CreatedAtUtc)
            .ThenBy(p => p.Id)
            .ToListAsync(ct);
    }

    public Task<StickerPack?> GetPackByIdAsync(StickerPackId packId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packId);
        return _db.StickerPacks.FirstOrDefaultAsync(p => p.Id == packId, ct);
    }

    public async Task<StickerPack> CreateCustomPackWithLimitCheckAsync(
        StickerPack pack,
        int maxCustomPacksPerUser,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (pack.OwnerUserId is null)
        {
            throw new DomainException("Custom sticker pack must have an owner.");
        }

        var ownerId = pack.OwnerUserId;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            await AcquireAppLockOrThrowAsync($"sticker-pack:{ownerId.Value:N}", tx, ct);

            var currentCount = await _db.StickerPacks
                .CountAsync(p => !p.IsSystemPack && p.OwnerUserId == ownerId && !p.IsDeleted, ct);
            if (currentCount >= maxCustomPacksPerUser)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException($"You can create at most {maxCustomPacksPerUser} custom sticker packs.");
            }

            var maxSortOrder = await _db.StickerPacks
                .Where(p => !p.IsSystemPack && p.OwnerUserId == ownerId && !p.IsDeleted)
                .MaxAsync(p => (int?)p.SortOrder, ct);
            pack.SetSortOrder((maxSortOrder ?? 0) + 1);

            _db.StickerPacks.Add(pack);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        return pack;
    }

    public async Task UpdatePackAsync(StickerPack pack, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (_db.Entry(pack).State == EntityState.Detached)
        {
            _db.StickerPacks.Update(pack);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _db.Entry(pack).State = EntityState.Detached;
            throw new ConflictException("The sticker pack was modified concurrently. Please try again.", ex);
        }
    }

    public async Task SoftDeletePackAndArchiveStickersAsync(
        StickerPackId packId,
        UserId ownerUserId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packId);
        ArgumentNullException.ThrowIfNull(ownerUserId);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            await AcquireAppLockOrThrowAsync($"sticker-pack:{ownerUserId.Value:N}", tx, ct);
            await AcquireAppLockOrThrowAsync($"media-quota:{ownerUserId.Value:N}", tx, ct);

            var pack = await _db.StickerPacks.FirstOrDefaultAsync(p => p.Id == packId, ct);
            if (pack is null || pack.IsDeleted)
            {
                throw new NotFoundException("StickerPack", packId.Value);
            }

            if (pack.IsSystemPack || pack.OwnerUserId != ownerUserId)
            {
                throw new ForbiddenException("Only the owner can delete this custom sticker pack.");
            }

            pack.MarkDeleted(nowUtc);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                throw new ConflictException("The sticker pack was modified concurrently. Please try again.", ex);
            }

            await _db.Stickers
                .Where(s => s.PackId == packId && s.UploaderId == ownerUserId && s.State == StickerState.Pending)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(st => st.State, StickerState.Failed)
                        .SetProperty(st => st.FailureReason, "Sticker pack was deleted.")
                        .SetProperty(st => st.DeletedAtUtc, nowUtc),
                    ct);

            var packStickerIds = _db.Stickers
                .Where(s => s.PackId == packId && s.UploaderId == ownerUserId)
                .Select(s => s.Id);

            // Preserve all users' starred favorites and recipient recents per Option A (foundbugs.md Issue 3);
            // remove only the pack owner's un-starred recents from the deleted pack.
            await _db.UserRecentStickers
                .Where(r => r.UserId == ownerUserId
                            && packStickerIds.Contains(r.StickerId)
                            && !_db.UserFavoriteStickers.Any(f => f.UserId == ownerUserId && f.StickerId == r.StickerId))
                .ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);
        });
    }

    public async Task<StickerPack> GetOrCreateDefaultCustomPackAsync(
        UserId ownerUserId,
        string authorName,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownerUserId);

        StickerPack? resolved = null;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            await AcquireAppLockOrThrowAsync($"sticker-pack:{ownerUserId.Value:N}", tx, ct);

            var existingPacks = await _db.StickerPacks
                .Where(p => !p.IsSystemPack && p.OwnerUserId == ownerUserId && !p.IsDeleted)
                .OrderBy(p => p.SortOrder)
                .ThenBy(p => p.CreatedAtUtc)
                .ToListAsync(ct);

            var defaultPack = existingPacks.FirstOrDefault(p =>
                string.Equals(p.Name, StickerConstraints.DefaultCustomPackName, StringComparison.OrdinalIgnoreCase));

            if (defaultPack is not null)
            {
                resolved = defaultPack;
                await tx.CommitAsync(ct);
                return;
            }

            if (existingPacks.Count >= StickerConstraints.MaxCustomPacksPerUser)
            {
                resolved = existingPacks[0];
                await tx.CommitAsync(ct);
                return;
            }

            var nextOrder = existingPacks.Count == 0 ? 1 : existingPacks.Max(p => p.SortOrder) + 1;
            var created = StickerPack.CreateCustom(
                ownerUserId,
                StickerConstraints.DefaultCustomPackName,
                authorName,
                nextOrder,
                nowUtc);

            _db.StickerPacks.Add(created);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            resolved = created;
        });

        return resolved!;
    }

    public async Task<Sticker> TryCreatePendingStickerWithQuotaCheckAsync(
        Sticker sticker,
        int maxStickersPerPack,
        int maxTotalCustomStickersPerUser,
        int maxConcurrentPending,
        long dailyQuotaBytes,
        DateTimeOffset dailyQuotaWindowStartUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sticker);
        if (sticker.UploaderId is null)
        {
            throw new DomainException("Custom sticker must specify an uploader.");
        }

        var uploaderId = sticker.UploaderId;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            // Shares the exact same media-quota lock key with MediaPersistence & AvatarPersistence.
            await AcquireAppLockOrThrowAsync($"media-quota:{uploaderId.Value:N}", tx, ct);

            var pack = await _db.StickerPacks
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == sticker.PackId, ct);

            if (pack is null || pack.IsDeleted)
            {
                await tx.RollbackAsync(ct);
                throw new NotFoundException("StickerPack", sticker.PackId.Value);
            }

            if (pack.IsSystemPack || pack.OwnerUserId != uploaderId)
            {
                await tx.RollbackAsync(ct);
                throw new ForbiddenException("You can only add stickers to your own custom packs.");
            }

            var packStickerCount = await _db.Stickers.CountAsync(
                s => s.PackId == sticker.PackId && (s.State == StickerState.Ready || s.State == StickerState.Pending),
                ct);
            if (packStickerCount >= maxStickersPerPack)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException($"A custom sticker pack can contain at most {maxStickersPerPack} stickers.");
            }

            var totalCustomStickers = await (
                from s in _db.Stickers
                join p in _db.StickerPacks on s.PackId equals p.Id
                where s.UploaderId == uploaderId
                      && !p.IsDeleted
                      && (s.State == StickerState.Ready || s.State == StickerState.Pending)
                select s.Id
            ).CountAsync(ct);
            if (totalCustomStickers >= maxTotalCustomStickersPerUser)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException($"You have reached the maximum of {maxTotalCustomStickersPerUser} custom stickers.");
            }

            var mediaPendingCount = await _db.Media
                .CountAsync(m => m.UploaderId == uploaderId && m.State == MediaState.Pending, ct);
            var avatarPendingCount = await _db.PendingAvatarUploads
                .CountAsync(p => p.UserId == uploaderId && p.State == MediaState.Pending, ct);
            var stickerPendingCount = await _db.Stickers
                .CountAsync(s => s.UploaderId == uploaderId && s.State == StickerState.Pending, ct);

            if (mediaPendingCount + avatarPendingCount + stickerPendingCount >= maxConcurrentPending)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException(
                    $"You have reached the maximum of {maxConcurrentPending} concurrent pending uploads.");
            }

            var (totalUsedBytes, totalInitiatedBytes) = await QuotaAccountingQueries.GetDailyQuotaUsageAsync(
                _db,
                uploaderId,
                dailyQuotaWindowStartUtc,
                ct);

            if (sticker.DeclaredSizeBytes > dailyQuotaBytes - totalUsedBytes)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException("Daily upload quota exceeded. Please try again later.");
            }

            var maxInitiatedBytes = dailyQuotaBytes <= long.MaxValue / 2
                ? dailyQuotaBytes * 2
                : long.MaxValue;

            if (sticker.DeclaredSizeBytes > maxInitiatedBytes - totalInitiatedBytes)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException("Daily upload quota exceeded. Please try again later.");
            }

            var maxDisplayOrder = await _db.Stickers
                .Where(s => s.PackId == sticker.PackId)
                .MaxAsync(s => (int?)s.DisplayOrder, ct);
            sticker.SetDisplayOrder((maxDisplayOrder ?? 0) + 1);

            _db.Stickers.Add(sticker);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        return sticker;
    }

    public Task<Sticker?> GetStickerByIdAsync(StickerId stickerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stickerId);
        return _db.Stickers.FirstOrDefaultAsync(s => s.Id == stickerId, ct);
    }

    public Task<Sticker?> GetStickerByIdNoTrackingAsync(StickerId stickerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stickerId);
        return _db.Stickers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == stickerId, ct);
    }

    public async Task<StickerWithPackRecord?> GetStickerWithPackAsync(StickerId stickerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stickerId);

        var row = await (
            from s in _db.Stickers.AsNoTracking()
            join p in _db.StickerPacks.AsNoTracking() on s.PackId equals p.Id
            where s.Id == stickerId
            select new { Sticker = s, Pack = p }
        ).FirstOrDefaultAsync(ct);

        return row is null ? null : new StickerWithPackRecord(row.Sticker, row.Pack);
    }

    public async Task<IReadOnlyDictionary<StickerId, StickerWithPackRecord>> GetStickersWithPacksByIdsAsync(
        IEnumerable<StickerId> stickerIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stickerIds);

        var idList = stickerIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return new Dictionary<StickerId, StickerWithPackRecord>();
        }

        var result = new Dictionary<StickerId, StickerWithPackRecord>(idList.Count);
        foreach (var chunk in idList.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var rows = await (
                from s in _db.Stickers.AsNoTracking()
                join p in _db.StickerPacks.AsNoTracking() on s.PackId equals p.Id
                where chunkList.Contains(s.Id)
                select new { Sticker = s, Pack = p }
            ).ToListAsync(ct);

            foreach (var row in rows)
            {
                result[row.Sticker.Id] = new StickerWithPackRecord(row.Sticker, row.Pack);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<Sticker>> GetReadyStickersByPackIdsAsync(
        IEnumerable<StickerPackId> packIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packIds);

        var idList = packIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return [];
        }

        return await _db.Stickers
            .AsNoTracking()
            .Where(s => idList.Contains(s.PackId) && s.State == StickerState.Ready && s.PurgedAtUtc == null)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.CreatedAtUtc)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);
    }

    public async Task<int> GetNextDisplayOrderInPackAsync(StickerPackId packId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packId);

        var maxOrder = await _db.Stickers
            .AsNoTracking()
            .Where(s => s.PackId == packId)
            .MaxAsync(s => (int?)s.DisplayOrder, ct);

        return (maxOrder ?? 0) + 1;
    }

    public async Task UpdateStickerAsync(Sticker sticker, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sticker);
        if (_db.Entry(sticker).State == EntityState.Detached)
        {
            _db.Stickers.Update(sticker);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _db.Entry(sticker).State = EntityState.Detached;
            throw new ConflictException("The sticker was modified concurrently. Please try again.", ex);
        }
    }

    public async Task CommitReadyStickerIfPackActiveAsync(Sticker sticker, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sticker);
        if (sticker.UploaderId is null)
        {
            throw new DomainException("Custom sticker must have an uploader.");
        }

        if (sticker.State != StickerState.Ready)
        {
            throw new DomainException($"Expected sticker in Ready state, found '{sticker.State}'.");
        }

        var uploaderId = sticker.UploaderId;
        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            await AcquireAppLockOrThrowAsync($"media-quota:{uploaderId.Value:N}", tx, ct);

            var packIsActive = await _db.StickerPacks
                .AsNoTracking()
                .AnyAsync(p => p.Id == sticker.PackId && !p.IsDeleted, ct);

            if (!packIsActive)
            {
                await _db.Stickers
                    .Where(s => s.Id == sticker.Id && s.State == StickerState.Pending)
                    .ExecuteUpdateAsync(
                        s => s
                            .SetProperty(st => st.State, StickerState.Failed)
                            .SetProperty(st => st.FailureReason, "Sticker pack was deleted.")
                            .SetProperty(st => st.DeletedAtUtc, st => st.DeletedAtUtc ?? (sticker.UploadedAtUtc ?? nowUtc)),
                        ct);

                await tx.CommitAsync(ct);
                _db.ChangeTracker.Clear();
                throw new DomainException("The sticker pack was deleted before upload completed.");
            }

            _db.Stickers.Update(sticker);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                await tx.RollbackAsync(ct);
                _db.ChangeTracker.Clear();
                throw new ConflictException("The sticker was modified concurrently. Please try again.", ex);
            }

            await tx.CommitAsync(ct);
        });
    }

    public async Task ArchiveStickerAndPruneReferencesAsync(
        StickerId stickerId,
        UserId ownerUserId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stickerId);
        ArgumentNullException.ThrowIfNull(ownerUserId);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);

            var sticker = await _db.Stickers.FirstOrDefaultAsync(s => s.Id == stickerId, ct);
            if (sticker is null)
            {
                throw new NotFoundException("Sticker", stickerId.Value);
            }

            if (sticker.UploaderId is null || sticker.UploaderId != ownerUserId)
            {
                throw new ForbiddenException("Only the owner can delete this custom sticker.");
            }

            if (sticker.State == StickerState.Archived)
            {
                await _db.UserFavoriteStickers
                    .Where(f => f.StickerId == stickerId)
                    .ExecuteDeleteAsync(ct);

                await _db.UserRecentStickers
                    .Where(r => r.StickerId == stickerId)
                    .ExecuteDeleteAsync(ct);

                await tx.CommitAsync(ct);
                return;
            }

            if (sticker.State != StickerState.Ready)
            {
                throw new ConflictException($"Custom sticker cannot be deleted from state '{sticker.State}'.");
            }

            sticker.MarkArchived(nowUtc);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                throw new ConflictException("The sticker was modified concurrently. Please try again.", ex);
            }

            await _db.UserFavoriteStickers
                .Where(f => f.StickerId == stickerId)
                .ExecuteDeleteAsync(ct);

            await _db.UserRecentStickers
                .Where(r => r.StickerId == stickerId)
                .ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);
        });
    }

    public async Task<IReadOnlyList<StickerWithPackRecord>> GetUserFavoriteStickersAsync(
        UserId userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);

        var rows = await (
            from f in _db.UserFavoriteStickers.AsNoTracking()
            join s in _db.Stickers.AsNoTracking() on f.StickerId equals s.Id
            join p in _db.StickerPacks.AsNoTracking() on s.PackId equals p.Id
            where f.UserId == userId
                  && s.State == StickerState.Ready
                  && s.PurgedAtUtc == null
                  && (!p.IsSystemPack || !p.IsDeleted)
            orderby f.CreatedAtUtc descending
            select new { Sticker = s, Pack = p }
        ).ToListAsync(ct);

        return rows.Select(r => new StickerWithPackRecord(r.Sticker, r.Pack)).ToList();
    }

    public async Task<IReadOnlyList<StickerWithPackRecord>> GetUserRecentStickersAsync(
        UserId userId,
        int maxCount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);

        var rows = await (
            from r in _db.UserRecentStickers.AsNoTracking()
            join s in _db.Stickers.AsNoTracking() on r.StickerId equals s.Id
            join p in _db.StickerPacks.AsNoTracking() on s.PackId equals p.Id
            where r.UserId == userId
                  && s.State == StickerState.Ready
                  && s.PurgedAtUtc == null
                  && (!p.IsDeleted
                      || (!p.IsSystemPack
                          && (s.UploaderId != userId
                              || _db.UserFavoriteStickers.Any(f => f.UserId == userId && f.StickerId == s.Id))))
            orderby r.LastUsedAtUtc descending
            select new { Sticker = s, Pack = p }
        ).Take(maxCount).ToListAsync(ct);

        return rows.Select(r => new StickerWithPackRecord(r.Sticker, r.Pack)).ToList();
    }

    public async Task<HashSet<StickerId>> GetUserFavoriteStickerIdsAsync(
        UserId userId,
        IEnumerable<StickerId> candidateIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(candidateIds);

        var idList = candidateIds.Distinct().ToList();
        if (idList.Count == 0)
        {
            return [];
        }

        var result = new HashSet<StickerId>();
        foreach (var chunk in idList.Chunk(500))
        {
            var chunkList = chunk.ToList();
            var favIds = await _db.UserFavoriteStickers
                .AsNoTracking()
                .Where(f => f.UserId == userId && chunkList.Contains(f.StickerId))
                .Select(f => f.StickerId)
                .ToListAsync(ct);

            foreach (var id in favIds)
            {
                result.Add(id);
            }
        }

        return result;
    }

    public Task<bool> IsStickerFavoritedByUserAsync(UserId userId, StickerId stickerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(stickerId);

        return _db.UserFavoriteStickers
            .AsNoTracking()
            .AnyAsync(f => f.UserId == userId && f.StickerId == stickerId, ct);
    }

    public async Task AddFavoriteWithLimitCheckAsync(
        UserId userId,
        StickerId stickerId,
        int maxFavoritesPerUser,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(stickerId);

        var strategy = _db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

            await AcquireAppLockOrThrowAsync($"sticker-fav:{userId.Value:N}", tx, ct);

            var stickerActive = await (
                from s in _db.Stickers.AsNoTracking()
                join p in _db.StickerPacks.AsNoTracking() on s.PackId equals p.Id
                where s.Id == stickerId
                      && s.State == StickerState.Ready
                      && s.PurgedAtUtc == null
                      && (!p.IsSystemPack || !p.IsDeleted)
                select 1
            ).AnyAsync(ct);
            if (!stickerActive)
            {
                await tx.RollbackAsync(ct);
                throw new NotFoundException("Sticker", stickerId.Value);
            }

            var alreadyExists = await _db.UserFavoriteStickers
                .AnyAsync(f => f.UserId == userId && f.StickerId == stickerId, ct);
            if (alreadyExists)
            {
                await tx.CommitAsync(ct);
                return;
            }

            var currentCount = await _db.UserFavoriteStickers.CountAsync(f => f.UserId == userId, ct);
            if (currentCount >= maxFavoritesPerUser)
            {
                await tx.RollbackAsync(ct);
                throw new DomainException($"You can save at most {maxFavoritesPerUser} favorite stickers.");
            }

            _db.UserFavoriteStickers.Add(UserFavoriteSticker.Create(userId, stickerId, nowUtc));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });
    }

    public async Task RemoveFavoriteAsync(UserId userId, StickerId stickerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(stickerId);

        await _db.UserFavoriteStickers
            .Where(f => f.UserId == userId && f.StickerId == stickerId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task UpsertRecentStickerAsync(
        UserId userId,
        StickerId stickerId,
        DateTimeOffset nowUtc,
        int maxRecentCount,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentNullException.ThrowIfNull(stickerId);

        var updated = await _db.UserRecentStickers
            .Where(r => r.UserId == userId && r.StickerId == stickerId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(r => r.LastUsedAtUtc, nowUtc)
                    .SetProperty(r => r.UseCount, r => r.UseCount + 1),
                ct);

        if (updated == 0)
        {
            var entry = UserRecentSticker.Create(userId, stickerId, nowUtc);
            _db.UserRecentStickers.Add(entry);
            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
            {
                _db.Entry(entry).State = EntityState.Detached;
                await _db.UserRecentStickers
                    .Where(r => r.UserId == userId && r.StickerId == stickerId)
                    .ExecuteUpdateAsync(
                        s => s
                            .SetProperty(r => r.LastUsedAtUtc, nowUtc)
                            .SetProperty(r => r.UseCount, r => r.UseCount + 1),
                        ct);
            }
        }

        var overflowIds = await _db.UserRecentStickers
            .AsNoTracking()
            .Where(r => r.UserId == userId)
            .OrderByDescending(r => r.LastUsedAtUtc)
            .ThenBy(r => r.StickerId)
            .Skip(maxRecentCount)
            .Select(r => r.StickerId)
            .ToListAsync(ct);

        if (overflowIds.Count > 0)
        {
            await _db.UserRecentStickers
                .Where(r => r.UserId == userId && overflowIds.Contains(r.StickerId))
                .ExecuteDeleteAsync(ct);
        }
    }

    public async Task<bool> CanUserAccessStickerAsync(
        UserId callerId,
        StickerId stickerId,
        bool forSending,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(stickerId);

        var record = await GetStickerWithPackAsync(stickerId, ct);
        if (record is null)
        {
            return false;
        }

        return await CanUserAccessStickerAsync(callerId, record, forSending, ct);
    }

    public async Task<bool> CanUserAccessStickerAsync(
        UserId callerId,
        StickerWithPackRecord record,
        bool forSending,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(record);

        if (record.Sticker.PurgedAtUtc is not null)
        {
            return false;
        }

        var stickerId = record.Sticker.Id;
        var sticker = record.Sticker;
        var pack = record.Pack;

        if (pack.IsSystemPack)
        {
            return !pack.IsDeleted && sticker.State == StickerState.Ready;
        }

        if (forSending)
        {
            if (sticker.State != StickerState.Ready
                || string.IsNullOrWhiteSpace(sticker.ObjectKey))
            {
                return false;
            }

            if (!pack.IsDeleted && sticker.UploaderId == callerId)
            {
                return true;
            }

            if (await _db.UserFavoriteStickers
                .AsNoTracking()
                .AnyAsync(f => f.UserId == callerId && f.StickerId == stickerId, ct))
            {
                return true;
            }

            if (!pack.IsDeleted || sticker.UploaderId != callerId)
            {
                return await _db.UserRecentStickers
                    .AsNoTracking()
                    .AnyAsync(r => r.UserId == callerId && r.StickerId == stickerId, ct);
            }

            return false;
        }

        if (sticker.State is not (StickerState.Ready or StickerState.Archived)
            || string.IsNullOrWhiteSpace(sticker.ObjectKey))
        {
            return false;
        }

        if (!pack.IsDeleted && sticker.State == StickerState.Ready && sticker.UploaderId == callerId)
        {
            return true;
        }

        var hasVisibleMessage = await (
            from m in _db.Messages.AsNoTracking()
            join c in _db.Conversations.AsNoTracking() on m.ConversationId equals c.Id
            where m.StickerId == stickerId
                  && !m.IsDeletedForEveryone
                  && (c.Participant1Id == callerId || c.Participant2Id == callerId)
                  && !_db.UserMessageDeletions.Any(d =>
                      d.ConversationId == m.ConversationId
                      && d.MessageId == m.Id
                      && d.UserId == callerId)
                  && !_db.UserConversationStates.Any(s =>
                      s.ConversationId == m.ConversationId
                      && s.UserId == callerId
                      && ((s.ClearedAtUtc != null && m.CreatedAtUtc < s.ClearedAtUtc)
                          || (s.DeletedAtUtc != null && m.CreatedAtUtc < s.DeletedAtUtc)))
            select m.Id
        ).AnyAsync(ct);

        if (hasVisibleMessage)
        {
            return true;
        }

        if (sticker.State == StickerState.Ready && (!pack.IsSystemPack || !pack.IsDeleted))
        {
            if (await _db.UserFavoriteStickers
                .AsNoTracking()
                .AnyAsync(f => f.UserId == callerId && f.StickerId == stickerId, ct))
            {
                return true;
            }

            if (await _db.UserRecentStickers
                .AsNoTracking()
                .AnyAsync(r => r.UserId == callerId && r.StickerId == stickerId, ct))
            {
                return true;
            }
        }

        return false;
    }

    public async Task<bool> IsStickerVisibleInMessageAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        StickerId stickerId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(callerId);
        ArgumentNullException.ThrowIfNull(conversationId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(stickerId);

        var conversation = await _db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);

        if (conversation is null || !conversation.HasParticipant(callerId))
        {
            return false;
        }

        var message = await _db.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.ConversationId == conversationId && m.Id == messageId, ct);

        if (message is null || message.IsDeletedForEveryone || message.StickerId != stickerId)
        {
            return false;
        }

        var isHidden = await _db.UserMessageDeletions
            .AsNoTracking()
            .AnyAsync(d => d.ConversationId == conversationId && d.MessageId == messageId && d.UserId == callerId, ct);
        if (isHidden)
        {
            return false;
        }

        var state = await _db.UserConversationStates
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ConversationId == conversationId && s.UserId == callerId, ct);

        DateTimeOffset? cutoff = state?.ClearedAtUtc;
        if (state?.DeletedAtUtc is not null && (!cutoff.HasValue || state.DeletedAtUtc.Value > cutoff.Value))
        {
            cutoff = state.DeletedAtUtc.Value;
        }

        if (cutoff.HasValue && message.CreatedAtUtc < cutoff.Value)
        {
            return false;
        }

        return true;
    }

    private async Task AcquireAppLockOrThrowAsync(
        string resourceName,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx,
        CancellationToken ct)
    {
        var rcParam = new SqlParameter("@rc", SqlDbType.Int)
        {
            Direction = ParameterDirection.Output
        };
        var resParam = new SqlParameter("@res", SqlDbType.NVarChar, 255)
        {
            Value = resourceName
        };

        await _db.Database.ExecuteSqlRawAsync(
            "EXEC @rc = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000",
            new object[] { rcParam, resParam },
            ct);

        if (rcParam.Value is not int lockResult || lockResult < 0)
        {
            await tx.RollbackAsync(ct);
            throw new ConflictException("Too many concurrent sticker requests. Please try again.");
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        for (Exception? current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is SqlException sqlEx && sqlEx.Number is 2601 or 2627)
            {
                return true;
            }
        }

        return false;
    }
}
