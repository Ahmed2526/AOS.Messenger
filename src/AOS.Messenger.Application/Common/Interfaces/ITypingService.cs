using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface ITypingService
{
    Task<UserId?> ValidateAndGetTypingRecipientAsync(UserId senderId, Guid conversationId, CancellationToken ct = default);
}
