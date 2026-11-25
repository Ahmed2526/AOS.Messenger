using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.DTOs;

public sealed record MarkReceiptRequestDto(long UpToMessageId);
public sealed record ReceiptUpdateDto(Guid ConversationId, Guid UserId, long UpToMessageId, DateTimeOffset AtUtc, string ReceiptType);
public sealed record ReceiptCommandResult(ReceiptUpdateDto Receipt, UserId RecipientUserId);
public sealed record TypingIndicatorDto(Guid ConversationId, Guid UserId, bool IsTyping);
public sealed record UserPresenceDto(Guid UserId, bool IsOnline, DateTimeOffset? LastSeenAtUtc);
