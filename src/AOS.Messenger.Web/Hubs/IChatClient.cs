using AOS.Messenger.Application.DTOs;

namespace AOS.Messenger.Web.Hubs;

/// <summary>
/// Strongly-typed SignalR client callback contract for chat realtime events.
/// </summary>
public interface IChatClient
{
    /// <summary>
    /// Delivers a newly created or replayed message to the connected conversation participants.
    /// </summary>
    Task ReceiveMessage(MessageDto message);

    Task ReceiveReceiptUpdated(ReceiptUpdateDto receipt);
    Task ReceiveTypingIndicator(TypingIndicatorDto typing);
    Task ReceivePresenceChanged(UserPresenceDto presence);
    Task ReceiveMessageEdited(MessageEditedDto message);
    Task ReceiveMessageDeletedForEveryone(MessageDeletedForEveryoneDto message);
    Task ReceiveMessageDeletedForMe(MessageDeletedForMeDto message);
    Task ReceiveReactionUpdated(ReactionUpdatedDto reaction);
    Task ReceiveUserProfileUpdated(UserProfileUpdatedDto profile);
}
