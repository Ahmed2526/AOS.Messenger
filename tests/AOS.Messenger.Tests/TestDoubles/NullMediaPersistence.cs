using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Tests.TestDoubles;

internal sealed class NullMediaPersistence : IMediaPersistence
{
    public static readonly NullMediaPersistence Instance = new();

    private NullMediaPersistence()
    {
    }

    public Task<Media> TryCreatePendingWithQuotaCheckAsync(
        Media media,
        int maxConcurrentPending,
        long dailyQuotaBytes,
        DateTimeOffset dailyQuotaWindowStartUtc,
        CancellationToken ct = default)
    {
        return Task.FromResult(media);
    }

    public Task<Media?> GetByIdAsync(MediaId id, CancellationToken ct = default)
    {
        return Task.FromResult<Media?>(null);
    }

    public Task<Media?> GetByIdNoTrackingAsync(MediaId id, CancellationToken ct = default)
    {
        return Task.FromResult<Media?>(null);
    }

    public Task<AuthorizedMediaDownloadContext> GetAuthorizedDownloadContextAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        MediaId mediaId,
        CancellationToken ct = default)
    {
        return Task.FromResult(new AuthorizedMediaDownloadContext(
            ConversationExists: false,
            IsParticipant: false,
            Media: null));
    }

    public Task<IReadOnlyDictionary<MessageId, IReadOnlyList<Media>>> GetByMessageIdsAsync(
        ConversationId conversationId,
        IEnumerable<MessageId> messageIds,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<MessageId, IReadOnlyList<Media>> empty =
            new Dictionary<MessageId, IReadOnlyList<Media>>();
        return Task.FromResult(empty);
    }

    public Task<IReadOnlyDictionary<(ConversationId ConversationId, MessageId MessageId), IReadOnlyList<Media>>> GetByMessageIdsAcrossConversationsAsync(
        IEnumerable<(ConversationId ConversationId, MessageId MessageId)> keys,
        CancellationToken ct = default)
    {
        IReadOnlyDictionary<(ConversationId ConversationId, MessageId MessageId), IReadOnlyList<Media>> empty =
            new Dictionary<(ConversationId, MessageId), IReadOnlyList<Media>>();
        return Task.FromResult(empty);
    }

    public Task UpdateAsync(Media media, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<MarkVoicePlayedResult> TryMarkVoicePlayedAsync(
        UserId callerId,
        ConversationId conversationId,
        MessageId messageId,
        MediaId mediaId,
        DateTimeOffset nowUtc,
        CancellationToken ct = default)
    {
        return Task.FromResult(new MarkVoicePlayedResult(MarkVoicePlayedStatus.NotFound));
    }
}
