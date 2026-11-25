using System.Security.Claims;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Exceptions;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AOS.Messenger.Web.Hubs;

/// <summary>
/// Thin SignalR hub managing realtime chat connections, per-invocation session verification,
/// connection limits, active conversation presence tracking, typing indicators, and presence updates.
/// Contains NO business logic or EF Core queries.
/// </summary>
[Authorize]
public sealed class ChatHub : Hub<IChatClient>
{
    private readonly IConversationService _conversationService;
    private readonly IConnectionTracker _connectionTracker;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<ChatHub> _logger;
    private readonly IPresenceService? _presenceService;
    private readonly ITypingService? _typingService;
    private readonly IRealtimeNotifier? _realtimeNotifier;
    private readonly TimeProvider _timeProvider;
    private readonly ISessionService? _sessionService;

    [ActivatorUtilitiesConstructor]
    public ChatHub(
        IConversationService conversationService,
        IConnectionTracker connectionTracker,
        ICurrentUser currentUser,
        ILogger<ChatHub> logger,
        IPresenceService presenceService,
        ITypingService typingService,
        IRealtimeNotifier realtimeNotifier,
        TimeProvider? timeProvider = null,
        ISessionService? sessionService = null)
    {
        _conversationService = conversationService;
        _connectionTracker = connectionTracker;
        _currentUser = currentUser;
        _logger = logger;
        _presenceService = presenceService;
        _typingService = typingService;
        _realtimeNotifier = realtimeNotifier;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sessionService = sessionService;
    }

    public ChatHub(
        IConversationService conversationService,
        IConnectionTracker connectionTracker,
        ICurrentUser currentUser,
        ILogger<ChatHub> logger,
        ISessionService? sessionService = null)
        : this(
            conversationService,
            connectionTracker,
            currentUser,
            logger,
            null!,
            null!,
            null!,
            null,
            sessionService)
    {
    }

    public override async Task OnConnectedAsync()
    {
        var userId = _currentUser.Id;
        if (userId is null && Context.User is not null)
        {
            var idClaim = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(idClaim) && UserId.TryParse(idClaim, out var parsedId))
            {
                userId = parsedId;
            }
        }

        var sessionClaim = Context.User?.FindFirst("SessionId")?.Value;
        Guid sessionId = Guid.Empty;

        if (Context.User is not null)
        {
            if (string.IsNullOrWhiteSpace(sessionClaim) || !Guid.TryParse(sessionClaim, out sessionId))
            {
                _logger.LogWarning("Connection {ConnectionId} rejected: missing or invalid SessionId claim", Context.ConnectionId);
                Context.Abort();
                return;
            }
        }

        if (userId is null)
        {
            _logger.LogWarning("Connection {ConnectionId} rejected: unauthenticated caller", Context.ConnectionId);
            Context.Abort();
            return;
        }

        // 1 & 2. Register connection in tracker with abort callback and enforce 10-connection cap
        var callerContext = Context;
        bool becameOnline = false;
        bool registered;
        if (_connectionTracker is InMemoryConnectionTracker memoryTracker)
        {
            registered = memoryTracker.TryAddConnection(
                userId,
                sessionId,
                callerContext.ConnectionId,
                () => callerContext.Abort(),
                out becameOnline);
        }
        else
        {
            registered = _connectionTracker.TryAddConnection(
                userId,
                sessionId,
                callerContext.ConnectionId,
                () => callerContext.Abort());
        }

        if (!registered)
        {
            _logger.LogWarning("Connection {ConnectionId} rejected for user {UserId}: connection cap (10) reached",
                Context.ConnectionId, userId.Value);
            Context.Abort();
            return;
        }

        // 3. Re-verify SessionId is active and unexpired against SQL Server (Register-Then-Verify defense against connect-vs-revoke race)
        if (_sessionService is not null && sessionId != Guid.Empty)
        {
            var isActive = await _sessionService.IsSessionActiveAsync(sessionId, Context.ConnectionAborted);
            if (!isActive)
            {
                _logger.LogWarning("Connection {ConnectionId} rejected for user {UserId}: session {SessionId} is revoked or expired",
                    Context.ConnectionId, userId.Value, sessionId);
                _connectionTracker.RemoveConnection(userId, Context.ConnectionId);
                Context.Abort();
                return;
            }
        }

        if (becameOnline && _presenceService is not null && _realtimeNotifier is not null)
        {
            var peerIds = await _presenceService.GetEligiblePresencePeerIdsAsync(userId, Context.ConnectionAborted);
            if (peerIds.Count > 0)
            {
                await _realtimeNotifier.NotifyPresenceChangedAsync(
                    peerIds,
                    new UserPresenceDto(userId.Value, true, null),
                    Context.ConnectionAborted);
            }
        }

        _logger.LogDebug("User {UserId} connected with connection {ConnectionId} on session {SessionId}",
            userId.Value, Context.ConnectionId, sessionId);

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = _currentUser.Id;
        if (userId is null && Context.User is not null)
        {
            var idClaim = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(idClaim) && UserId.TryParse(idClaim, out var parsedId))
            {
                userId = parsedId;
            }
        }

        if (userId is not null)
        {
            bool wentOffline = false;
            if (_connectionTracker is InMemoryConnectionTracker memoryTracker)
            {
                memoryTracker.RemoveConnection(userId, Context.ConnectionId, out wentOffline);
            }
            else
            {
                _connectionTracker.RemoveConnection(userId, Context.ConnectionId);
            }

            _logger.LogDebug("User {UserId} disconnected from connection {ConnectionId}", userId.Value, Context.ConnectionId);

            if (wentOffline && _presenceService is not null)
            {
                var now = _timeProvider.GetUtcNow();
                await _presenceService.RecordLastSeenAsync(userId, now, CancellationToken.None);
                var peerIds = await _presenceService.GetEligiblePresencePeerIdsAsync(userId, CancellationToken.None);
                if (peerIds.Count > 0 && _realtimeNotifier is not null)
                {
                    await _realtimeNotifier.NotifyPresenceChangedAsync(
                        peerIds,
                        new UserPresenceDto(userId.Value, false, now),
                        CancellationToken.None);
                }
            }
        }
        else
        {
            _connectionTracker.ClearActiveConversation(Context.ConnectionId);
        }

        await base.OnDisconnectedAsync(exception);
    }

    public async Task SendTypingIndicator(Guid conversationId, bool isTyping)
    {
        var userId = _currentUser.Id;
        if (userId is null && Context.User is not null)
        {
            var idClaim = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(idClaim) && UserId.TryParse(idClaim, out var parsedId))
            {
                userId = parsedId;
            }
        }

        if (userId is null)
        {
            throw new HubException("User is not authenticated.");
        }

        if (_typingService is not null && _realtimeNotifier is not null)
        {
            var recipient = await _typingService.ValidateAndGetTypingRecipientAsync(userId, conversationId, Context.ConnectionAborted);
            if (recipient is not null)
            {
                await _realtimeNotifier.NotifyTypingAsync(
                    recipient,
                    new TypingIndicatorDto(conversationId, userId.Value, isTyping),
                    Context.ConnectionAborted);
            }
        }
    }

    public async Task JoinConversation(Guid conversationId)
    {
        var userId = _currentUser.Id;
        if (userId is null && Context.User is not null)
        {
            var idClaim = Context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrEmpty(idClaim) && UserId.TryParse(idClaim, out var parsedId))
            {
                userId = parsedId;
            }
        }

        if (userId is null)
        {
            throw new HubException("User is not authenticated.");
        }

        try
        {
            await _conversationService.GetConversationAsync(
                userId,
                new ConversationId(conversationId),
                Context.ConnectionAborted);
        }
        catch (ForbiddenException ex)
        {
            _logger.LogWarning(ex, "User {UserId} forbidden from joining conversation {ConversationId}", userId.Value, conversationId);
            throw new HubException("User is not a participant in this conversation.");
        }
        catch (NotFoundException ex)
        {
            _logger.LogWarning(ex, "Conversation {ConversationId} not found when user {UserId} tried to join", conversationId, userId.Value);
            throw new HubException("Conversation not found.");
        }
        catch (DomainException ex)
        {
            _logger.LogWarning(ex, "Invalid conversation ID {ConversationId}", conversationId);
            throw new HubException(ex.Message);
        }

        _connectionTracker.SetActiveConversation(Context.ConnectionId, conversationId);
        _logger.LogDebug("Connection {ConnectionId} joined conversation {ConversationId}", Context.ConnectionId, conversationId);
    }

    public Task LeaveConversation(Guid conversationId)
    {
        _connectionTracker.ClearActiveConversation(Context.ConnectionId);
        _logger.LogDebug("Connection {ConnectionId} left conversation {ConversationId}", Context.ConnectionId, conversationId);
        return Task.CompletedTask;
    }
}
