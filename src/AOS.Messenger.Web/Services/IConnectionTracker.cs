using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Web.Services;

/// <summary>
/// Thread-safe in-memory connection and presence tracker for SignalR connections.
/// Supports multiple simultaneous connections per user, connection capping, and active conversation focus tracking.
/// </summary>
public interface IConnectionTracker
{
    /// <summary>
    /// Registers an active SignalR connection for a user.
    /// Returns true if registered successfully, or false if rejected due to connection cap (max 10).
    /// </summary>
    bool AddConnection(UserId userId, string connectionId);

    /// <summary>
    /// Registers an active SignalR connection for a user with session tracking and abort callback.
    /// Returns true if registered successfully, or false if rejected due to connection cap (max 10).
    /// </summary>
    bool AddConnection(UserId userId, Guid sessionId, string connectionId, Action? abortAction = null);

    /// <summary>
    /// Attempts to register an active SignalR connection with connection cap enforcement.
    /// Returns true if registered successfully, or false if rejected due to connection cap (max 10).
    /// </summary>
    bool TryAddConnection(UserId userId, Guid sessionId, string connectionId, Action? abortAction = null);

    /// <summary>
    /// Unregisters an active SignalR connection for a user.
    /// </summary>
    void RemoveConnection(UserId userId, string connectionId);

    /// <summary>
    /// Tracks which conversation the specific connection is currently viewing.
    /// </summary>
    void SetActiveConversation(string connectionId, Guid conversationId);

    /// <summary>
    /// Clears active conversation focus for the given connection.
    /// </summary>
    void ClearActiveConversation(string connectionId);

    /// <summary>
    /// Gets all active SignalR connection IDs for the specified user.
    /// </summary>
    IReadOnlyCollection<string> GetConnections(UserId userId);

    /// <summary>
    /// Gets the conversation currently active on the given connection, if any.
    /// </summary>
    Guid? GetActiveConversation(string connectionId);

    /// <summary>
    /// Checks whether the specified user currently has at least one active connection.
    /// </summary>
    bool IsUserOnline(UserId userId);

    /// <summary>
    /// Gets all users who currently have at least one active connection.
    /// </summary>
    IReadOnlyCollection<UserId> GetOnlineUsers();

    /// <summary>
    /// Aborts all active SignalR connections associated with the specified session.
    /// </summary>
    void AbortSession(Guid sessionId);

    /// <summary>
    /// Aborts all active SignalR connections associated with the specified user.
    /// </summary>
    void AbortUser(UserId userId);
}
