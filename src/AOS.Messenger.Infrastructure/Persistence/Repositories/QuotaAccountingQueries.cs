using AOS.Messenger.Domain.Enums;
using AOS.Messenger.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Persistence.Repositories;

internal static class QuotaAccountingQueries
{
    public static async Task<(long UsedBytes, long InitiatedBytes)> GetDailyQuotaUsageAsync(
        ApplicationDbContext db,
        UserId userId,
        DateTimeOffset dailyQuotaWindowStartUtc,
        CancellationToken ct)
    {
        var mediaTotals = await db.Media
            .Where(m => m.UploaderId == userId && m.CreatedAtUtc >= dailyQuotaWindowStartUtc)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Used = g.Sum(m => m.State == MediaState.Failed
                    ? (long?)(m.VerifiedSizeBytes ?? 0L)
                    : (long?)(m.VerifiedSizeBytes ?? m.DeclaredSizeBytes)) ?? 0L,
                Initiated = g.Sum(m => (long?)m.DeclaredSizeBytes) ?? 0L
            })
            .FirstOrDefaultAsync(ct);

        var avatarTotals = await db.PendingAvatarUploads
            .Where(p => p.UserId == userId && p.CreatedAtUtc >= dailyQuotaWindowStartUtc)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Used = g.Sum(p => p.State == MediaState.Failed
                    ? (long?)(p.VerifiedSizeBytes ?? 0L)
                    : (long?)(p.VerifiedSizeBytes ?? p.DeclaredSizeBytes)) ?? 0L,
                Initiated = g.Sum(p => (long?)p.DeclaredSizeBytes) ?? 0L
            })
            .FirstOrDefaultAsync(ct);

        var stickerTotals = await db.Stickers
            .Where(s => s.UploaderId == userId && s.CreatedAtUtc >= dailyQuotaWindowStartUtc)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Used = g.Sum(s => s.State == StickerState.Failed
                    ? (long?)(s.VerifiedSizeBytes ?? 0L)
                    : s.State == StickerState.Ready || s.State == StickerState.Archived
                        ? (long?)s.DeclaredSizeBytes
                        : (long?)(s.VerifiedSizeBytes ?? s.DeclaredSizeBytes)) ?? 0L,
                Initiated = g.Sum(s => (long?)s.DeclaredSizeBytes) ?? 0L
            })
            .FirstOrDefaultAsync(ct);

        var totalUsed = (mediaTotals?.Used ?? 0L) + (avatarTotals?.Used ?? 0L) + (stickerTotals?.Used ?? 0L);
        var totalInitiated = (mediaTotals?.Initiated ?? 0L) + (avatarTotals?.Initiated ?? 0L) + (stickerTotals?.Initiated ?? 0L);

        return (totalUsed, totalInitiated);
    }
}
