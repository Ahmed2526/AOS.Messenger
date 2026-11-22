using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Infrastructure.Identity;

internal sealed class NullSessionConnectionTerminator : ISessionConnectionTerminator
{
    public static readonly NullSessionConnectionTerminator Instance = new();

    public Task DisconnectSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task DisconnectUserSessionsAsync(UserId userId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
