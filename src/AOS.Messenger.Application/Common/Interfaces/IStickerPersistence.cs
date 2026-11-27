using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public sealed record StickerWithPackRecord(
    Sticker Sticker,
    StickerPack Pack);

/// <summary>
/// Persistence contract for sticker packs, custom R2 sticker uploads, shared upload quotas,
/// favorites/recents tracking, and strict IDOR access checks.
/// </summary>
public interface IStickerPersistence
{
    Task<IReadOnlyList<StickerPack>> GetSystemPacksAsync(CancellationToken ct = default);

    Task<IReadOnlyList<StickerPack>> GetUserCustomPacksAsync(UserId ownerUserId, CancellationToken ct = default);

    Task<StickerPack?> GetPackByIdAsync(StickerPackId packId, CancellationToken ct = default);

    Task<StickerPack> CreateCustomPackWithLimitCheckAsync(
        StickerPack pack,
        int maxCustomPacksPerUser,
        CancellationToken ct = default);

    Task UpdatePackAsync(StickerPack pack, CancellationToken ct = default);

    Task SoftDeletePackAndArchiveStickersAsync(
        StickerPackId packId,
        UserId ownerUserId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);

    Task<StickerPack> GetOrCreateDefaultCustomPackAsync(
        UserId ownerUserId,
        string authorName,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);

    Task<Sticker> TryCreatePendingStickerWithQuotaCheckAsync(
        Sticker sticker,
        int maxStickersPerPack,
        int maxTotalCustomStickersPerUser,
        int maxConcurrentPending,
        long dailyQuotaBytes,
        DateTimeOffset dailyQuotaWindowStartUtc,
        CancellationToken ct = default);

    Task<Sticker?> GetStickerByIdAsync(StickerId stickerId, CancellationToken ct = default);

    Task<Sticker?> GetStickerByIdNoTrackingAsync(StickerId stickerId, CancellationToken ct = default);

    Task<StickerWithPackRecord?> GetStickerWithPackAsync(StickerId stickerId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<StickerId, StickerWithPackRecord>> GetStickersWithPacksByIdsAsync(
        IEnumerable<StickerId> stickerIds,
        CancellationToken ct = default);

    Task<IReadOnlyList<Sticker>> GetReadyStickersByPackIdsAsync(
        IEnumerable<StickerPackId> packIds,
        CancellationToken ct = default);

    Task<int> GetNextDisplayOrderInPackAsync(StickerPackId packId, CancellationToken ct = default);

    Task UpdateStickerAsync(Sticker sticker, CancellationToken ct = default);

    Task CommitReadyStickerIfPackActiveAsync(Sticker sticker, DateTimeOffset nowUtc, CancellationToken ct = default);

    Task ArchiveStickerAndPruneReferencesAsync(
        StickerId stickerId,
        UserId ownerUserId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);

    Task<IReadOnlyList<StickerWithPackRecord>> GetUserFavoriteStickersAsync(UserId userId, CancellationToken ct = default);

    Task<IReadOnlyList<StickerWithPackRecord>> GetUserRecentStickersAsync(
        UserId userId,
        int maxCount,
        CancellationToken ct = default);

    Task<HashSet<StickerId>> GetUserFavoriteStickerIdsAsync(
        UserId userId,
        IEnumerable<StickerId> candidateIds,
        CancellationToken ct = default);

    Task<bool> IsStickerFavoritedByUserAsync(
        UserId userId,
        StickerId stickerId,
        CancellationToken ct = default);

    Task AddFavoriteWithLimitCheckAsync(
        UserId userId,
        StickerId stickerId,
        int maxFavoritesPerUser,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);

    Task RemoveFavoriteAsync(
        UserId userId,
        StickerId stickerId,
        CancellationToken ct = default);

    Task UpsertRecentStickerAsync(
        UserId userId,
        StickerId stickerId,
        DateTimeOffset nowUtc,
        int maxRecentCount,
        CancellationToken ct = default);

    Task<bool> CanUserAccessStickerAsync(
        UserId callerId,
        StickerId stickerId,
        bool forSending,
        CancellationToken ct = default);

    Task<bool> CanUserAccessStickerAsync(
        UserId callerId,
        StickerWithPackRecord record,
        bool forSending,
        CancellationToken ct = default);

    Task<bool> IsStickerVisibleInMessageAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        StickerId stickerId,
        CancellationToken ct = default);
}
