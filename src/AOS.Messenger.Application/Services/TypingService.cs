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

        if (conversationId == Guid.Empty)
        {
            return null;
        }

        return await _conversationPersistence.GetVerifiedTypingRecipientAsync(
            senderId,
            new ConversationId(conversationId),
            ct);
    }
}
