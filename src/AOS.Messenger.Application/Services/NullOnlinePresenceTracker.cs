using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Services;

/// <summary>
/// Default in-memory null object implementation of <see cref="IOnlinePresenceTracker"/>
/// used when realtime presence tracking is unavailable or in isolated tests.
/// </summary>
internal sealed class NullOnlinePresenceTracker : IOnlinePresenceTracker
{
    public static readonly NullOnlinePresenceTracker Instance = new();

    private NullOnlinePresenceTracker()
    {
    }

    public bool IsUserOnline(UserId userId) => false;
}
