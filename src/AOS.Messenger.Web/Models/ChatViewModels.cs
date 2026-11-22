using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Web.Models;

public sealed class ChatIndexViewModel
{
    public IReadOnlyList<ConversationSummaryDto> Conversations { get; set; } = [];
    public ConversationId? SelectedConversationId { get; set; }
}

public sealed class ChatConversationViewModel
{
    public ConversationDetailDto Conversation { get; set; } = null!;
    public IReadOnlyList<ConversationSummaryDto> Conversations { get; set; } = [];
    public IReadOnlyList<MessageDto> Messages { get; set; } = [];
    public IReadOnlyList<MessageDto> InitialMessages
    {
        get => Messages;
        set => Messages = value;
    }
    public string? NextCursor { get; set; }
}

public sealed class ArchivedChatsViewModel
{
    public IReadOnlyList<ConversationSummaryDto> ArchivedConversations { get; set; } = [];
}
