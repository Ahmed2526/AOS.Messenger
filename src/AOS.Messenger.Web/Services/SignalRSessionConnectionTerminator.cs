using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
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
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly TimeProvider _timeProvider;

    [ActivatorUtilitiesConstructor]
    public SignalRSessionConnectionTerminator(
        IConnectionTracker connectionTracker,
        ILogger<SignalRSessionConnectionTerminator> logger,
        IServiceScopeFactory? scopeFactory = null,
        TimeProvider? timeProvider = null)
    {
        _connectionTracker = connectionTracker;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public SignalRSessionConnectionTerminator(
        IConnectionTracker connectionTracker,
        ILogger<SignalRSessionConnectionTerminator> logger)
        : this(connectionTracker, logger, null, null)
    {
    }

    public async Task DisconnectSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        _logger.LogDebug("Disconnecting realtime connections for session {SessionId}", sessionId);
        if (_connectionTracker is InMemoryConnectionTracker memoryTracker)
        {
            memoryTracker.AbortSession(sessionId, out var wentOfflineUsers);
            await NotifyUsersWentOfflineAsync(wentOfflineUsers);
            return;
        }

        _connectionTracker.AbortSession(sessionId);
    }

    public async Task DisconnectUserSessionsAsync(UserId userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(userId);
        _logger.LogDebug("Disconnecting all realtime connections for user {UserId}", userId.Value);
        if (_connectionTracker is InMemoryConnectionTracker memoryTracker)
        {
            memoryTracker.AbortUser(userId, out bool wentOffline);
            if (wentOffline)
            {
                await NotifyUsersWentOfflineAsync([userId]);
            }

            return;
        }

        _connectionTracker.AbortUser(userId);
    }

    private async Task NotifyUsersWentOfflineAsync(IReadOnlyCollection<UserId> offlineUserIds)
    {
        if (offlineUserIds.Count == 0 || _scopeFactory is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var presenceService = scope.ServiceProvider.GetService<IPresenceService>();
            if (presenceService is null)
            {
                return;
            }

            var realtimeNotifier = scope.ServiceProvider.GetService<IRealtimeNotifier>();

            foreach (var userId in offlineUserIds)
            {
                if (_connectionTracker.IsUserOnline(userId))
                {
                    continue;
                }

                await presenceService.RecordLastSeenAsync(userId, now, CancellationToken.None);

                if (realtimeNotifier is not null)
                {
                    var peerIds = await presenceService.GetEligiblePresencePeerIdsAsync(userId, CancellationToken.None);
                    if (peerIds.Count > 0)
                    {
                        await realtimeNotifier.NotifyPresenceChangedAsync(
                            peerIds,
                            new UserPresenceDto(userId.Value, false, now),
                            CancellationToken.None);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update last seen or broadcast offline presence after terminating connections");
        }
    }
}
