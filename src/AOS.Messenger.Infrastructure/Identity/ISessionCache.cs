namespace AOS.Messenger.Infrastructure.Identity;

public readonly record struct CachedSessionInfo(bool IsRevoked, DateTimeOffset AbsoluteExpiresAtUtc);

/// <summary>
/// Thread-safe in-memory cache for active and revoked user session state.
/// </summary>
public interface ISessionCache
{
    bool TryGet(Guid sessionId, out CachedSessionInfo info);

    void Set(Guid sessionId, CachedSessionInfo info);

    void Invalidate(Guid sessionId);

    DateTimeOffset? GetLastActiveAtUtc(Guid sessionId);

    void SetLastActiveAtUtc(Guid sessionId, DateTimeOffset lastActiveAtUtc);

    void Clear();
}
