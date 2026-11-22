using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Application service contract for direct conversation management and per-user state operations.
/// </summary>
public interface IConversationService
{
    Task<ConversationDetailDto> GetOrCreateConversationAsync(UserId currentUserId, UserId targetUserId, CancellationToken ct = default);
    Task<ConversationDetailDto> GetConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationSummaryDto>> GetConversationListAsync(UserId currentUserId, bool includeArchived = false, CancellationToken ct = default);
    Task<bool> PinConversationAsync(UserId currentUserId, ConversationId conversationId, bool isPinned, CancellationToken ct = default);
    Task<bool> ArchiveConversationAsync(UserId currentUserId, ConversationId conversationId, bool isArchived, CancellationToken ct = default);
    Task<bool> MuteConversationAsync(UserId currentUserId, ConversationId conversationId, bool isMuted, CancellationToken ct = default);
    Task<bool> ClearChatAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default);
    Task<bool> DeleteConversationAsync(UserId currentUserId, ConversationId conversationId, CancellationToken ct = default);
}
