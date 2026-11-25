using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.DTOs;

public sealed record ConversationSummaryDto(
    ConversationId Id,
    UserId OtherParticipantId,
    string OtherParticipantName,
    string OtherParticipantHandle,
    string? LastMessagePreview,
    DateTimeOffset LastActivityAtUtc,
    int UnreadCount,
    bool IsPinned,
    bool IsArchived,
    bool IsMuted,
    bool IsOnline = false,
    DateTimeOffset? LastSeenAtUtc = null,
    MessageId? PeerLastDeliveredMessageId = null,
    MessageId? PeerLastReadMessageId = null,
    MessageId? LastMessageId = null,
    string? OtherParticipantAvatarVersion = null);

public sealed record ConversationDetailDto(
    ConversationId Id,
    UserId OtherParticipantId,
    string OtherParticipantName,
    string OtherParticipantHandle,
    string? OtherParticipantBio,
    bool IsBlocked,
    bool IsPinned,
    bool IsArchived,
    bool IsMuted,
    DateTimeOffset CreatedAtUtc,
    bool IsOnline = false,
    DateTimeOffset? LastSeenAtUtc = null,
    MessageId? PeerLastDeliveredMessageId = null,
    MessageId? PeerLastReadMessageId = null,
    bool IsPeerDeleted = false,
    bool CanSend = true,
    string? OtherParticipantAvatarVersion = null);

public sealed record ConversationBundleDto(
    UserId ViewerUserId,
    ConversationDetailDto Conversation,
    IReadOnlyList<MessageDto> Messages,
    string? NextCursor,
    bool HasMore);

public sealed record CreateConversationRequestDto(
    UserId TargetUserId);
