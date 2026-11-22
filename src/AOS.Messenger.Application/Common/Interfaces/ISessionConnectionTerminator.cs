using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

/// <summary>
/// Abstraction for disconnecting realtime SignalR connections associated with revoked sessions or deleted accounts.
/// </summary>
public interface ISessionConnectionTerminator
{
    Task DisconnectSessionAsync(Guid sessionId, CancellationToken ct = default);

    Task DisconnectUserSessionsAsync(UserId userId, CancellationToken ct = default);
}
