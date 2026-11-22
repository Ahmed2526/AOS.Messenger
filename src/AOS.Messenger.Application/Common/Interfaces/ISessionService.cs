using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;

namespace AOS.Messenger.Application.Common.Interfaces;

public interface ISessionService
{
    Task<IReadOnlyList<UserSessionDto>> GetActiveSessionsAsync(
        UserId userId, Guid? currentSessionId = null, CancellationToken ct = default);

    Task<Guid> CreateSessionAsync(
        UserId userId, string deviceName, string? ipAddress, string? userAgent, CancellationToken ct = default);

    Task<bool> RevokeSessionAsync(
        UserId userId, Guid sessionId, CancellationToken ct = default);

    Task RevokeOtherSessionsAsync(
        UserId userId, Guid currentSessionId, CancellationToken ct = default);

    Task<bool> IsSessionActiveAsync(
        Guid sessionId, CancellationToken ct = default);

    Task UpdateSessionActivityAsync(
        Guid sessionId, CancellationToken ct = default);

    Task RevokeAllSessionsAsync(
        UserId userId, CancellationToken ct = default);
}
