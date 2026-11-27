using System.Collections.Concurrent;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Web.Services;

/// <summary>
/// Thread-safe in-memory connection and presence tracker for single-server SignalR deployments.
/// Enforces per-user connection limits (max 10) and tracks session-bound abort handles.
/// Implements <see cref="IConnectionTracker"/> and <see cref="IOnlinePresenceTracker"/>.
/// </summary>
public sealed class InMemoryConnectionTracker : IConnectionTracker, IOnlinePresenceTracker
{
    private const int MaxConnectionsPerUser = 10;
    private readonly object _syncRoot = new();

    private sealed class TrackedConnection
    {
        public required UserId UserId { get; init; }
        public required Guid SessionId { get; init; }
        public required string ConnectionId { get; init; }
        public Action? AbortAction { get; init; }
    }

    private readonly Dictionary<UserId, Dictionary<string, TrackedConnection>> _userConnections = new();
    private readonly Dictionary<string, TrackedConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Guid> _activeConversations = new(StringComparer.Ordinal);

    public bool AddConnection(UserId userId, string connectionId)
    {
        return TryAddConnection(userId, Guid.Empty, connectionId, null);
    }

    public bool AddConnection(UserId userId, Guid sessionId, string connectionId, Action? abortAction = null)
    {
        return TryAddConnection(userId, sessionId, connectionId, abortAction);
    }

    public bool TryAddConnection(UserId userId, Guid sessionId, string connectionId, Action? abortAction = null)
    {
        return TryAddConnection(userId, sessionId, connectionId, abortAction, out _);
    }

    public bool TryAddConnection(UserId userId, Guid sessionId, string connectionId, Action? abortAction, out bool becameOnline)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_syncRoot)
        {
            becameOnline = false;
            bool wasOnline = _userConnections.TryGetValue(userId, out var userConns) && userConns.Count > 0;

            if (userConns is null)
            {
                userConns = new Dictionary<string, TrackedConnection>(StringComparer.Ordinal);
                _userConnections[userId] = userConns;
            }

            // If connection already exists for this user, update it without consuming an additional slot
            if (userConns.ContainsKey(connectionId))
            {
                var existing = new TrackedConnection
                {
                    UserId = userId,
                    SessionId = sessionId,
                    ConnectionId = connectionId,
                    AbortAction = abortAction
                };
                userConns[connectionId] = existing;
                _connections[connectionId] = existing;
                becameOnline = !wasOnline;
                return true;
            }

            // Enforce maximum 10 concurrent connections per user
            if (userConns.Count >= MaxConnectionsPerUser)
            {
                return false;
            }

            var tracked = new TrackedConnection
            {
                UserId = userId,
                SessionId = sessionId,
                ConnectionId = connectionId,
                AbortAction = abortAction
            };

            userConns[connectionId] = tracked;
            _connections[connectionId] = tracked;
            becameOnline = !wasOnline;
            return true;
        }
    }

    public void RemoveConnection(UserId userId, string connectionId)
    {
        RemoveConnection(userId, connectionId, out _);
    }

    public void RemoveConnection(UserId userId, string connectionId, out bool wentOffline)
    {
        ArgumentNullException.ThrowIfNull(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_syncRoot)
        {
            wentOffline = false;
            bool wasOnline = _userConnections.TryGetValue(userId, out var userConns) && userConns.Count > 0;

            if (userConns is not null)
            {
                if (userConns.Remove(connectionId))
                {
                    if (userConns.Count == 0)
                    {
                        _userConnections.Remove(userId);
                        if (wasOnline)
                        {
                            wentOffline = true;
                        }
                    }
                }
            }

            _connections.Remove(connectionId);
        }

        _activeConversations.TryRemove(connectionId, out _);
    }

    public void SetActiveConversation(string connectionId, Guid conversationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        lock (_syncRoot)
        {
            if (_connections.ContainsKey(connectionId))
            {
                _activeConversations[connectionId] = conversationId;
            }
        }
    }

    public void ClearActiveConversation(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        _activeConversations.TryRemove(connectionId, out _);
    }

    public IReadOnlyCollection<string> GetConnections(UserId userId)
    {
        ArgumentNullException.ThrowIfNull(userId);

        lock (_syncRoot)
        {
            if (_userConnections.TryGetValue(userId, out var connections))
            {
                return connections.Keys.ToArray();
            }

            return Array.Empty<string>();
        }
    }

    public Guid? GetActiveConversation(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        return _activeConversations.TryGetValue(connectionId, out var convId) ? convId : null;
    }

    public bool IsUserOnline(UserId userId)
    {
        ArgumentNullException.ThrowIfNull(userId);

        lock (_syncRoot)
        {
            return _userConnections.TryGetValue(userId, out var connections) && connections.Count > 0;
        }
    }

    public IReadOnlyCollection<UserId> GetOnlineUsers()
    {
        lock (_syncRoot)
        {
            return _userConnections.Keys.ToArray();
        }
    }

    public void AbortSession(Guid sessionId)
    {
        AbortSession(sessionId, out _);
    }

    public void AbortSession(Guid sessionId, out IReadOnlyCollection<UserId> wentOfflineUsers)
    {
        List<Action> abortHandles = new();
        HashSet<UserId>? offlineUsers = null;

        lock (_syncRoot)
        {
            var matching = _connections.Values
                .Where(c => c.SessionId == sessionId)
                .ToList();

            foreach (var conn in matching)
            {
                _connections.Remove(conn.ConnectionId);

                if (_userConnections.TryGetValue(conn.UserId, out var userConns))
                {
                    bool wasOnline = userConns.Count > 0;
                    if (userConns.Remove(conn.ConnectionId) && wasOnline && userConns.Count == 0)
                    {
                        _userConnections.Remove(conn.UserId);
                        offlineUsers ??= new HashSet<UserId>();
                        offlineUsers.Add(conn.UserId);
                    }
                }

                _activeConversations.TryRemove(conn.ConnectionId, out _);

                if (conn.AbortAction is not null)
                {
                    abortHandles.Add(conn.AbortAction);
                }
            }
        }

        wentOfflineUsers = offlineUsers is { Count: > 0 }
            ? offlineUsers.ToArray()
            : Array.Empty<UserId>();

        // Execute abort handles outside the lock to prevent deadlock
        foreach (var abort in abortHandles)
        {
            try
            {
                abort();
            }
            catch
            {
                // Suppress exceptions from individual abort callbacks
            }
        }
    }

    public void AbortUser(UserId userId)
    {
        AbortUser(userId, out _);
    }

    public void AbortUser(UserId userId, out bool wentOffline)
    {
        ArgumentNullException.ThrowIfNull(userId);

        List<Action> abortHandles = new();

        lock (_syncRoot)
        {
            wentOffline = false;
            if (_userConnections.TryGetValue(userId, out var userConns))
            {
                bool wasOnline = userConns.Count > 0;
                foreach (var conn in userConns.Values)
                {
                    _connections.Remove(conn.ConnectionId);
                    _activeConversations.TryRemove(conn.ConnectionId, out _);

                    if (conn.AbortAction is not null)
                    {
                        abortHandles.Add(conn.AbortAction);
                    }
                }

                _userConnections.Remove(userId);
                wentOffline = wasOnline;
            }
        }

        // Execute abort handles outside the lock to prevent deadlock
        foreach (var abort in abortHandles)
        {
            try
            {
                abort();
            }
            catch
            {
                // Suppress exceptions from individual abort callbacks
            }
        }
    }
}
