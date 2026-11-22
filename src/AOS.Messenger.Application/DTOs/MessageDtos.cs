using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.DTOs;

public sealed record SendMessageRequestDto(
    string Content,
    string IdempotencyKey,
    MessageId? ReplyToMessageId = null);

public sealed record MessageReplyDto(
    MessageId Id,
    UserId SenderId,
    string SenderName,
    string PreviewContent,
    bool IsDeletedForEveryone)
{
    public long MessageId => Id.Value;
    public string SenderDisplayName => SenderName;
    public string ContentPreview => PreviewContent;
}

public sealed record EditMessageRequestDto(
    string Content);

public sealed record MessageEditedDto(
    Guid ConversationId,
    long MessageId,
    string Content,
    DateTimeOffset? EditedAtUtc);

public sealed record MessageDeletedForMeDto(
    Guid ConversationId,
    long MessageId,
    long? NewLastMessageId,
    string? NewLastMessagePreview);

public sealed record MessageDeletedForEveryoneDto(
    Guid ConversationId,
    long MessageId,
    DateTimeOffset DeletedAtUtc);

public sealed record ToggleReactionRequestDto(
    string Emoji);

public sealed record ReactionSummaryDto(
    string Emoji,
    int Count,
    bool ReactedByCurrentUser);

public sealed record ReactionGroupDto(
    string Emoji,
    IReadOnlyList<Guid> UserIds);

public sealed record ReactionUpdatedDto(
    Guid ConversationId,
    long MessageId,
    Guid UserId,
    string? Emoji,
    IReadOnlyList<ReactionGroupDto>? Reactions = null);

public sealed record MessageDto(
    MessageId Id,
    ConversationId ConversationId,
    UserId SenderId,
    string SenderName,
    string SenderHandle,
    string Content,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? EditedAtUtc,
    bool IsDeletedForEveryone,
    MessageReplyDto? ReplyTo,
    bool IsOutgoing,
    IReadOnlyList<ReactionSummaryDto>? Reactions = null)
{
    public IReadOnlyList<ReactionSummaryDto> Reactions { get; init; } = Reactions ?? [];
}

public sealed record MessagesCursorPageDto(
    IReadOnlyList<MessageDto> Messages,
    string? NextCursor,
    bool HasMore);
