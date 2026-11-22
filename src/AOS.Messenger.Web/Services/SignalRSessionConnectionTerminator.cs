using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Web.Services;

/// <summary>
/// SignalR session connection terminator that disconnects active realtime connections
/// associated with revoked sessions or accounts via <see cref="IConnectionTracker"/>.
/// </summary>
public sealed class SignalRSessionConnectionTerminator : ISessionConnectionTerminator
{
    private readonly IConnectionTracker _connectionTracker;
    private readonly ILogger<SignalRSessionConnectionTerminator> _logger;

    public SignalRSessionConnectionTerminator(
        IConnectionTracker connectionTracker,
        ILogger<SignalRSessionConnectionTerminator> logger)
    {
        _connectionTracker = connectionTracker;
        _logger = logger;
    }

    public Task DisconnectSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        _logger.LogDebug("Disconnecting realtime connections for session {SessionId}", sessionId);
        _connectionTracker.AbortSession(sessionId);
        return Task.CompletedTask;
    }

    public Task DisconnectUserSessionsAsync(UserId userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        _logger.LogDebug("Disconnecting all realtime connections for user {UserId}", userId.Value);
        _connectionTracker.AbortUser(userId);
        return Task.CompletedTask;
    }
}
