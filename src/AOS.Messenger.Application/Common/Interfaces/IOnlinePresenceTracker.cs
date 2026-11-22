using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface IOnlinePresenceTracker
{
    bool IsUserOnline(UserId userId);
}
