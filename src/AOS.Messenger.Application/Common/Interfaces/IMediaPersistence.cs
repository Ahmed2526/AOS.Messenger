using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Single-query authorization and metadata projection for media download requests.
/// </summary>
public sealed record AuthorizedMediaDownloadContext(
    bool ConversationExists,
    bool IsParticipant,
    Media? Media);

public enum MarkVoicePlayedStatus
{
    NotFound = 0,
    ForbiddenNotParticipant = 1,
    ForbiddenOwnVoiceMessage = 2,
    InvalidNotVoiceMedia = 3,
    AlreadyPlayed = 4,
    Marked = 5,
    ForbiddenBlocked = 6
}

public sealed record MarkVoicePlayedResult(
    MarkVoicePlayedStatus Status,
    UserId? UploaderId = null,
    DateTimeOffset? PlayedAtUtc = null);

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

    Task<AuthorizedMediaDownloadContext> GetAuthorizedDownloadContextAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        MediaId mediaId,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<MessageId, IReadOnlyList<Media>>> GetByMessageIdsAsync(
        ConversationId conversationId,
        IEnumerable<MessageId> messageIds,
        CancellationToken ct = default);

    Task<IReadOnlyDictionary<(ConversationId ConversationId, MessageId MessageId), IReadOnlyList<Media>>> GetByMessageIdsAcrossConversationsAsync(
        IEnumerable<(ConversationId ConversationId, MessageId MessageId)> keys,
        CancellationToken ct = default);

    Task UpdateAsync(Media media, CancellationToken ct = default);

    Task<MarkVoicePlayedResult> TryMarkVoicePlayedAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        MediaId mediaId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default);
}
