using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Tests;

public sealed class TrackingSessionConnectionTerminator : ISessionConnectionTerminator
{
    public List<Guid> TerminatedSessionIds { get; } = new();
    public List<UserId> TerminatedUserIds { get; } = new();

    public Task DisconnectSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        lock (TerminatedSessionIds)
        {
            TerminatedSessionIds.Add(sessionId);
        }
        return Task.CompletedTask;
    }

    public Task DisconnectUserSessionsAsync(UserId userId, CancellationToken ct = default)
    {
        lock (TerminatedUserIds)
        {
            TerminatedUserIds.Add(userId);
        }
        return Task.CompletedTask;
    }
}
