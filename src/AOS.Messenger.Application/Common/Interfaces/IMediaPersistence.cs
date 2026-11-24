using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Persistence contract for media quota enforcement, metadata lookups, and lifecycle updates.
/// </summary>
public interface IMediaPersistence
{
    Task<Media> TryCreatePendingWithQuotaCheckAsync(
        Media media,
        int maxConcurrentPending,
        long dailyQuotaBytes,
        DateTimeOffset dailyQuotaWindowStartUtc,
        CancellationToken ct = default);

    Task<Media?> GetByIdAsync(MediaId id, CancellationToken ct = default);

    Task<Media?> GetByIdNoTrackingAsync(MediaId id, CancellationToken ct = default);

    Task<IReadOnlyDictionary<MessageId, IReadOnlyList<Media>>> GetByMessageIdsAsync(
        ConversationId conversationId,
        IEnumerable<MessageId> messageIds,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<(ConversationId ConversationId, MessageId MessageId), IReadOnlyList<Media>>> GetByMessageIdsAcrossConversationsAsync(
        IEnumerable<(ConversationId ConversationId, MessageId MessageId)> keys,
        CancellationToken ct = default);

    Task UpdateAsync(Media media, CancellationToken ct = default);
}
