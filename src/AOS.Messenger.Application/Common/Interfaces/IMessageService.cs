using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface IMessageService
{
    Task<MessageDto> SendMessageAsync(UserId senderId, ConversationId conversationId, SendMessageRequestDto dto, CancellationToken ct = default);
    Task<MessagesCursorPageDto> GetMessagesAsync(UserId callerId, ConversationId conversationId, string? cursor, int pageSize = 30, CancellationToken ct = default);
    Task<MessageEditedDto> EditMessageAsync(UserId callerId, ConversationId conversationId, MessageId messageId, EditMessageRequestDto dto, CancellationToken ct = default);
    Task DeleteForMeAsync(UserId callerId, ConversationId conversationId, MessageId messageId, CancellationToken ct = default);
    Task<MessageDeletedForEveryoneDto> DeleteForEveryoneAsync(UserId callerId, ConversationId conversationId, MessageId messageId, CancellationToken ct = default);
    Task<ReactionUpdatedDto> ToggleReactionAsync(UserId callerId, ConversationId conversationId, MessageId messageId, ToggleReactionRequestDto dto, CancellationToken ct = default);
    Task<ReactionUpdatedDto> RemoveReactionAsync(UserId callerId, ConversationId conversationId, MessageId messageId, CancellationToken ct = default);
}
