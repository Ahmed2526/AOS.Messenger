using AOS.Messenger.Application.DTOs;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface IReceiptService
{
    Task<ReceiptUpdateDto?> MarkDeliveredAsync(Guid conversationId, long upToMessageId, CancellationToken ct = default);
    Task<ReceiptUpdateDto?> MarkReadAsync(Guid conversationId, long upToMessageId, CancellationToken ct = default);
    Task<ReceiptCommandResult?> MarkDeliveredWithRecipientAsync(Guid conversationId, long upToMessageId, CancellationToken ct = default);
    Task<ReceiptCommandResult?> MarkReadWithRecipientAsync(Guid conversationId, long upToMessageId, CancellationToken ct = default);
}
