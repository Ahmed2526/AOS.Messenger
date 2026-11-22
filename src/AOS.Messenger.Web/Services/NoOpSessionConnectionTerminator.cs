using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Web.Services;

/// <summary>
/// No-op implementation of ISessionConnectionTerminator for the Web project.
/// Active connection termination will be wired to realtime hubs in Ticket 4.
/// </summary>
public sealed class NoOpSessionConnectionTerminator : ISessionConnectionTerminator
{
    public Task DisconnectSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task DisconnectUserSessionsAsync(UserId userId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
