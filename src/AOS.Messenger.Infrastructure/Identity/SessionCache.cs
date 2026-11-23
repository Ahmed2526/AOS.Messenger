using Microsoft.Extensions.Caching.Memory;

namespace AOS.Messenger.Infrastructure.Identity;

/// <summary>
/// Thread-safe in-memory cache for user sessions backed by a bounded MemoryCache.
/// Enforces SizeLimit = 50,000, sliding and absolute expiration to prevent unbounded memory growth.
/// </summary>
public sealed class SessionCache : ISessionCache, IDisposable
{
    private readonly MemoryCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly object _lock = new();

    public SessionCache(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 50_000
        });
    }

    private sealed record CacheEntry(CachedSessionInfo Info, DateTimeOffset? LastActiveAtUtc);

    public bool TryGet(Guid sessionId, out CachedSessionInfo info)
    {
        if (_cache.TryGetValue(sessionId, out CacheEntry? entry) && entry is not null)
        {
            info = entry.Info;
            return true;
        }

        info = default;
        return false;
    }

    public void Set(Guid sessionId, CachedSessionInfo info)
    {
        var now = _timeProvider.GetUtcNow();
        DateTimeOffset? lastActive = null;

        lock (_lock)
        {
            if (_cache.TryGetValue(sessionId, out CacheEntry? existing) && existing is not null)
            {
                lastActive = existing.LastActiveAtUtc;
            }
            else if (!info.IsRevoked)
            {
                lastActive = now;
            }

            var entryOptions = new MemoryCacheEntryOptions
            {
                Size = 1,
                SlidingExpiration = TimeSpan.FromMinutes(10),
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            };

            // If session expires before the default 30-minute absolute window, respect the earlier expiration.
            if (info.AbsoluteExpiresAtUtc > now && info.AbsoluteExpiresAtUtc < now.AddMinutes(30))
            {
                entryOptions.AbsoluteExpirationRelativeToNow = info.AbsoluteExpiresAtUtc - now;
            }

            _cache.Set(sessionId, new CacheEntry(info, lastActive), entryOptions);
        }
    }

    public void Invalidate(Guid sessionId)
    {
        lock (_lock)
        {
            var tombstoneOptions = new MemoryCacheEntryOptions
            {
                Size = 1,
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            };

            var tombstone = new CacheEntry(
                new CachedSessionInfo(IsRevoked: true, AbsoluteExpiresAtUtc: DateTimeOffset.MinValue),
                null);

            _cache.Set(sessionId, tombstone, tombstoneOptions);
        }
    }

    public DateTimeOffset? GetLastActiveAtUtc(Guid sessionId)
    {
        if (_cache.TryGetValue(sessionId, out CacheEntry? entry) && entry is not null)
        {
            return entry.LastActiveAtUtc;
        }

        return null;
    }

    public void SetLastActiveAtUtc(Guid sessionId, DateTimeOffset lastActiveAtUtc)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(sessionId, out CacheEntry? existing) && existing is not null)
            {
                var entryOptions = new MemoryCacheEntryOptions
                {
                    Size = 1,
                    SlidingExpiration = TimeSpan.FromMinutes(10),
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
                };

                var now = _timeProvider.GetUtcNow();
                if (existing.Info.AbsoluteExpiresAtUtc > now && existing.Info.AbsoluteExpiresAtUtc < now.AddMinutes(30))
                {
                    entryOptions.AbsoluteExpirationRelativeToNow = existing.Info.AbsoluteExpiresAtUtc - now;
                }

                _cache.Set(sessionId, existing with { LastActiveAtUtc = lastActiveAtUtc }, entryOptions);
            }
        }
    }

    public void Clear()
    {
        _cache.Clear();
    }

    public void Dispose()
    {
        _cache.Dispose();
    }
}
