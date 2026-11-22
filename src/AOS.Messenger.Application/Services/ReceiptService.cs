using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Services;

/// <summary>
/// Application service responsible for recording and advancing delivery and read watermarks.
/// Validates caller authorization, bidirectional blocking, conversation membership, and message cutoff bounds.
/// </summary>
public sealed class ReceiptService : IReceiptService
{
    private readonly ICurrentUser _currentUser;
    private readonly IConversationPersistence _conversationPersistence;
    private readonly IMessagePersistence _messagePersistence;
    private readonly IBlockPersistence _blockPersistence;
    private readonly TimeProvider _timeProvider;

    public ReceiptService(
        ICurrentUser currentUser,
        IConversationPersistence conversationPersistence,
        IMessagePersistence messagePersistence,
        IBlockPersistence blockPersistence,
        TimeProvider? timeProvider = null)
    {
        _currentUser = currentUser;
        _conversationPersistence = conversationPersistence;
        _messagePersistence = messagePersistence;
        _blockPersistence = blockPersistence;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReceiptUpdateDto?> MarkDeliveredAsync(Guid conversationId, long upToMessageId, CancellationToken ct = default)
    {
        var (conversation, callerId, message) = await ValidateReceiptTargetAsync(conversationId, upToMessageId, ct);

        var changed = await _conversationPersistence.AdvanceDeliveredWatermarkAsync(
            conversation.Id,
            callerId,
            message.Id,
            message.CreatedAtUtc,
            ct);

        return changed
            ? new ReceiptUpdateDto(conversationId, callerId.Value, upToMessageId, _timeProvider.GetUtcNow(), "delivered")
            : null;
    }

    public async Task<ReceiptUpdateDto?> MarkReadAsync(Guid conversationId, long upToMessageId, CancellationToken ct = default)
    {
        var (conversation, callerId, message) = await ValidateReceiptTargetAsync(conversationId, upToMessageId, ct);

        var changed = await _conversationPersistence.AdvanceReadWatermarkAsync(
            conversation.Id,
            callerId,
            message.Id,
            message.CreatedAtUtc,
            ct);

        return changed
            ? new ReceiptUpdateDto(conversationId, callerId.Value, upToMessageId, _timeProvider.GetUtcNow(), "read")
            : null;
    }

    private async Task<(Conversation Conversation, UserId CallerId, Message Message)> ValidateReceiptTargetAsync(
        Guid conversationId,
        long upToMessageId,
        CancellationToken ct)
    {
        var callerId = _currentUser.Id ?? throw new ForbiddenException("Unauthenticated user.");

        var conversation = await _conversationPersistence.GetByIdAsync(new ConversationId(conversationId), ct);
        if (conversation is null)
        {
            throw new NotFoundException("Conversation not found.");
        }

        if (!conversation.HasParticipant(callerId))
        {
            throw new ForbiddenException("Caller is not a participant in this conversation.");
        }

        var peerId = conversation.GetOtherParticipant(callerId);
        if (await _blockPersistence.IsBlockedBidirectionalAsync(callerId, peerId, ct))
        {
            throw new ForbiddenException("Cannot update receipts when blocked.");
        }

        var state = await _conversationPersistence.GetUserStateAsync(conversation.Id, callerId, ct);
        DateTimeOffset? cutoff = null;
        if (state is not null)
        {
            cutoff = state.ClearedAtUtc > state.DeletedAtUtc
                ? state.ClearedAtUtc
                : (state.DeletedAtUtc ?? state.ClearedAtUtc);
        }

        var message = await _messagePersistence.GetByIdAsync(conversation.Id, new MessageId(upToMessageId), ct);
        if (message is null)
        {
            throw new NotFoundException("Message not found in conversation.");
        }

        if (cutoff.HasValue && message.CreatedAtUtc < cutoff.Value)
        {
            throw new NotFoundException("Message is cleared or deleted.");
        }

        if (await _messagePersistence.IsMessageDeletedForUserAsync(conversation.Id, message.Id, callerId, ct))
        {
            throw new NotFoundException("Message is cleared or deleted.");
        }

        if (message.SenderId == callerId)
        {
            throw new ForbiddenException("Cannot mark receipts for own outgoing messages.");
        }

        return (conversation, callerId, message);
    }
}
