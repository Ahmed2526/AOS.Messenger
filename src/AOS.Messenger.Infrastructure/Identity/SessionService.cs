using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AOS.Messenger.Infrastructure.Identity;

public sealed class SessionService : ISessionService
{
    private readonly ApplicationDbContext _db;
    private readonly TimeProvider _timeProvider;
    private readonly ISessionConnectionTerminator _terminator;
    private readonly ISessionCache _sessionCache;

    public SessionService(
        ApplicationDbContext db,
        TimeProvider? timeProvider = null,
        ISessionConnectionTerminator? terminator = null,
        ISessionCache? sessionCache = null)
    {
        _db = db;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _terminator = terminator ?? NullSessionConnectionTerminator.Instance;
        _sessionCache = sessionCache ?? SessionCache.Instance;
    }

    public async Task<IReadOnlyList<UserSessionDto>> GetActiveSessionsAsync(
        UserId userId, Guid? currentSessionId = null, CancellationToken ct = default)
    {
        var userGuid = userId.Value;
        var now = _timeProvider.GetUtcNow();
        var sessions = await _db.UserSessions
            .AsNoTracking()
            .Where(s => s.UserId == userGuid && !s.IsRevoked && now < s.AbsoluteExpiresAtUtc)
            .OrderByDescending(s => s.LastActiveAtUtc)
            .Select(s => new UserSessionDto(
                s.Id,
                s.DeviceName,
                s.IpAddress,
                s.CreatedAtUtc,
                s.LastActiveAtUtc,
                currentSessionId.HasValue && s.Id == currentSessionId.Value))
            .ToListAsync(ct);

        return sessions;
    }

    public async Task<Guid> CreateSessionAsync(
        UserId userId, string deviceName, string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        var normalizedUserAgent = NormalizeUserAgent(userAgent);
        var effectiveDeviceName = !string.IsNullOrWhiteSpace(deviceName)
            ? (deviceName.Length > 150 ? deviceName[..150] : deviceName)
            : normalizedUserAgent;

        var session = new UserSession
        {
            UserId = userId.Value,
            DeviceName = effectiveDeviceName,
            IpAddress = ipAddress != null && ipAddress.Length > 45 ? ipAddress[..45] : ipAddress,
            UserAgent = normalizedUserAgent,
            CreatedAtUtc = now,
            LastActiveAtUtc = now,
            AbsoluteExpiresAtUtc = now.AddDays(30),
            IsRevoked = false
        };

        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        _sessionCache.Set(session.Id, new CachedSessionInfo(IsRevoked: false, AbsoluteExpiresAtUtc: session.AbsoluteExpiresAtUtc));
        if (_sessionCache is SessionCache concreteCache)
        {
            concreteCache.SetLastActiveAtUtc(session.Id, now);
        }

        return session.Id;
    }

    public async Task<bool> RevokeSessionAsync(UserId userId, Guid sessionId, CancellationToken ct = default)
    {
        var userGuid = userId.Value;
        var session = await _db.UserSessions
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userGuid, ct);

        if (session is null || session.IsRevoked)
        {
            return false;
        }

        session.IsRevoked = true;
        session.RevokedAtUtc = _timeProvider.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        _sessionCache.Invalidate(sessionId);

        await _terminator.DisconnectSessionAsync(sessionId, ct);

        return true;
    }

    public async Task RevokeOtherSessionsAsync(UserId userId, Guid currentSessionId, CancellationToken ct = default)
    {
        var userGuid = userId.Value;
        var otherSessions = await _db.UserSessions
            .Where(s => s.UserId == userGuid && s.Id != currentSessionId && !s.IsRevoked)
            .ToListAsync(ct);

        if (otherSessions.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var session in otherSessions)
        {
            session.IsRevoked = true;
            session.RevokedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);

        foreach (var session in otherSessions)
        {
            _sessionCache.Invalidate(session.Id);
            await _terminator.DisconnectSessionAsync(session.Id, ct);
        }
    }

    public async Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();

        if (_sessionCache.TryGet(sessionId, out var cached))
        {
            if (cached.IsRevoked || now >= cached.AbsoluteExpiresAtUtc)
            {
                return false;
            }

            return true;
        }

        var session = await _db.UserSessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new { s.IsRevoked, s.AbsoluteExpiresAtUtc, s.LastActiveAtUtc })
            .FirstOrDefaultAsync(ct);

        if (session is null)
        {
            _sessionCache.Set(sessionId, new CachedSessionInfo(IsRevoked: true, AbsoluteExpiresAtUtc: DateTimeOffset.MinValue));
            return false;
        }

        _sessionCache.Set(sessionId, new CachedSessionInfo(session.IsRevoked, session.AbsoluteExpiresAtUtc));
        if (!session.IsRevoked && _sessionCache is SessionCache concreteCache)
        {
            concreteCache.SetLastActiveAtUtc(sessionId, session.LastActiveAtUtc);
        }

        return !session.IsRevoked && now < session.AbsoluteExpiresAtUtc;
    }

    public async Task UpdateSessionActivityAsync(Guid sessionId, CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        await _db.UserSessions
            .Where(s => s.Id == sessionId && !s.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastActiveAtUtc, now), ct);

        if (_sessionCache is SessionCache concreteCache)
        {
            concreteCache.SetLastActiveAtUtc(sessionId, now);
        }
    }

    public async Task RevokeAllSessionsAsync(UserId userId, CancellationToken ct = default)
    {
        var userGuid = userId.Value;
        var sessions = await _db.UserSessions
            .Where(s => s.UserId == userGuid && !s.IsRevoked)
            .ToListAsync(ct);

        if (sessions.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var session in sessions)
        {
            session.IsRevoked = true;
            session.RevokedAtUtc = now;
        }

        await _db.SaveChangesAsync(ct);

        foreach (var session in sessions)
        {
            _sessionCache.Invalidate(session.Id);
        }

        await _terminator.DisconnectUserSessionsAsync(userId, ct);
    }

    public static string NormalizeUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return "Unknown Device";
        }

        string browser = string.Empty;
        if (userAgent.Contains("Edg/", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Edge", StringComparison.OrdinalIgnoreCase))
        {
            browser = "Edge";
        }
        else if (userAgent.Contains("Chrome", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("CriOS", StringComparison.OrdinalIgnoreCase))
        {
            browser = "Chrome";
        }
        else if (userAgent.Contains("Firefox", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("FxiOS", StringComparison.OrdinalIgnoreCase))
        {
            browser = "Firefox";
        }
        else if (userAgent.Contains("Safari", StringComparison.OrdinalIgnoreCase))
        {
            browser = "Safari";
        }
        else if (userAgent.Contains("Opera", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("OPR", StringComparison.OrdinalIgnoreCase))
        {
            browser = "Opera";
        }

        string os = string.Empty;
        if (userAgent.Contains("iPhone", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iPad", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("iOS", StringComparison.OrdinalIgnoreCase))
        {
            os = "iOS";
        }
        else if (userAgent.Contains("Android", StringComparison.OrdinalIgnoreCase))
        {
            os = "Android";
        }
        else if (userAgent.Contains("Windows", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Win64", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Win32", StringComparison.OrdinalIgnoreCase))
        {
            os = "Windows";
        }
        else if (userAgent.Contains("Macintosh", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("macOS", StringComparison.OrdinalIgnoreCase))
        {
            os = "macOS";
        }
        else if (userAgent.Contains("Linux", StringComparison.OrdinalIgnoreCase) || userAgent.Contains("X11", StringComparison.OrdinalIgnoreCase))
        {
            os = "Linux";
        }

        string label;
        if (!string.IsNullOrEmpty(browser) && !string.IsNullOrEmpty(os))
        {
            label = $"{browser} on {os}";
        }
        else if (!string.IsNullOrEmpty(browser))
        {
            label = browser;
        }
        else if (!string.IsNullOrEmpty(os))
        {
            label = $"{os} Device";
        }
        else
        {
            label = "Unknown Device";
        }

        return label.Length > 100 ? label[..100] : label;
    }
}
