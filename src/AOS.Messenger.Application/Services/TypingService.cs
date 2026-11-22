using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Services;

/// <summary>
/// Application service validating typing indicator eligibility between participants in a conversation.
/// Enforces conversation participation, bidirectional block rules, and peer active status.
/// </summary>
public sealed class TypingService : ITypingService
{
    private readonly IConversationPersistence _conversationPersistence;
    private readonly IBlockPersistence _blockPersistence;
    private readonly IUserReader _userReader;

    public TypingService(
        IConversationPersistence conversationPersistence,
        IBlockPersistence blockPersistence,
        IUserReader userReader)
    {
        _conversationPersistence = conversationPersistence;
        _blockPersistence = blockPersistence;
        _userReader = userReader;
    }

    public async Task<UserId?> ValidateAndGetTypingRecipientAsync(
        UserId senderId,
        Guid conversationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(senderId);

        var conv = await _conversationPersistence.GetByIdAsync(new ConversationId(conversationId), ct);
        if (conv is null)
        {
            return null;
        }

        if (!conv.HasParticipant(senderId))
        {
            return null;
        }

        var peerId = conv.GetOtherParticipant(senderId);
        if (await _blockPersistence.IsBlockedBidirectionalAsync(senderId, peerId, ct))
        {
            return null;
        }

        var peer = await _userReader.GetByIdAsync(peerId, ct);
        if (peer is null || peer.IsDeleted || !await _userReader.ExistsAsync(peerId, ct))
        {
            return null;
        }

        return peerId;
    }
}
