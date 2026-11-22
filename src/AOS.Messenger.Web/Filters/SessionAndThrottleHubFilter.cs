using System.Collections.Concurrent;
using AOS.Messenger.Application.Common.Interfaces;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace AOS.Messenger.Web.Filters;

/// <summary>
/// Hub filter running on every hub method invocation to enforce per-invocation session validity
/// (defending against revoked sessions continuing on open WebSockets) and throttling ephemeral
/// hub invocations to prevent WebSocket flooding.
/// </summary>
public sealed class SessionAndThrottleHubFilter : IHubFilter
{
    private readonly ISessionService? _sessionService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionAndThrottleHubFilter> _logger;
    private readonly ConcurrentDictionary<string, InvocationThrottleWindow> _throttleState = new(StringComparer.Ordinal);

    // Max 60 ephemeral invocations per 1-minute window per connection
    private const int MaxInvocationsPerWindow = 60;
    private static readonly TimeSpan WindowDuration = TimeSpan.FromMinutes(1);

    [ActivatorUtilitiesConstructor]
    public SessionAndThrottleHubFilter(
        TimeProvider? timeProvider = null,
        ILogger<SessionAndThrottleHubFilter>? logger = null)
        : this(null, timeProvider, logger)
    {
    }

    public SessionAndThrottleHubFilter(
        ISessionService? sessionService,
        TimeProvider? timeProvider = null,
        ILogger<SessionAndThrottleHubFilter>? logger = null)
    {
        _sessionService = sessionService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionAndThrottleHubFilter>.Instance;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var context = invocationContext.Context;
        var connectionId = context.ConnectionId;
        var hubMethod = invocationContext.HubMethodName;

        // 1. Per-invocation SessionId verification
        var user = context.User;
        var sessionClaim = user?.FindFirst("SessionId")?.Value;

        if (string.IsNullOrWhiteSpace(sessionClaim) || !Guid.TryParse(sessionClaim, out var sessionId))
        {
            _logger.LogWarning(
                "Hub invocation {HubMethod} rejected: connection {ConnectionId} has missing or invalid SessionId claim",
                hubMethod,
                connectionId);

            context.Abort();
            throw new HubException("Session has been revoked or expired.");
        }

        var sessionService = _sessionService
            ?? invocationContext.ServiceProvider?.GetService<ISessionService>();

        if (sessionService is not null)
        {
            var isActive = await sessionService.IsSessionActiveAsync(sessionId, context.ConnectionAborted);
            if (!isActive)
            {
                _logger.LogWarning(
                    "Hub invocation {HubMethod} rejected: session {SessionId} for connection {ConnectionId} is revoked or expired",
                    hubMethod,
                    sessionId,
                    connectionId);

                context.Abort();
                throw new HubException("Session has been revoked or expired.");
            }
        }

        // 2. Throttling of ephemeral hub invocations to prevent WebSocket flooding
        var now = _timeProvider.GetUtcNow();
        var window = _throttleState.GetOrAdd(connectionId, _ => new InvocationThrottleWindow(now));

        if (!window.TryAcquire(now, MaxInvocationsPerWindow, WindowDuration))
        {
            _logger.LogWarning(
                "Hub invocation {HubMethod} throttled for connection {ConnectionId}",
                hubMethod,
                connectionId);

            throw new HubException("Rate limit exceeded. Please wait before sending more requests.");
        }

        _logger.LogDebug(
            "Hub invocation {HubMethod} executing for connection {ConnectionId} session {SessionId}",
            hubMethod,
            connectionId,
            sessionId);

        return await next(invocationContext);
    }

    public Task OnDisconnectedAsync(
        HubLifetimeContext context,
        Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        _throttleState.TryRemove(context.Context.ConnectionId, out _);
        return next(context, exception);
    }

    private sealed class InvocationThrottleWindow
    {
        private readonly object _lock = new();
        private DateTimeOffset _windowStart;
        private int _count;

        public InvocationThrottleWindow(DateTimeOffset start)
        {
            _windowStart = start;
            _count = 0;
        }

        public bool TryAcquire(DateTimeOffset now, int maxLimit, TimeSpan duration)
        {
            lock (_lock)
            {
                if (now - _windowStart > duration)
                {
                    _windowStart = now;
                    _count = 0;
                }

                if (_count >= maxLimit)
                {
                    return false;
                }

                _count++;
                return true;
            }
        }
    }
}
