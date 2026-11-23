using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class SessionServiceTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public SessionServiceTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private ApplicationDbContext CreateDbContext()
    {
        return _fixture.CreateDbContext();
    }

    [Fact]
    public async Task CreateSessionAsync_PersistsNewActiveSession()
    {
        using var db = CreateDbContext();
        var service = new SessionService(db);
        var userId = UserId.New();

        var sessionId = await service.CreateSessionAsync(userId, "Chrome Windows", "127.0.0.1", "Mozilla/5.0");

        Assert.NotEqual(Guid.Empty, sessionId);
        var session = await db.UserSessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(userId.Value, session.UserId);
        Assert.Equal("Chrome Windows", session.DeviceName);
        Assert.False(session.IsRevoked);
    }

    [Fact]
    public async Task GetActiveSessionsAsync_ReturnsOnlyNonRevokedSessions()
    {
        using var db = CreateDbContext();
        var service = new SessionService(db);
        var userId = UserId.New();

        var session1Id = await service.CreateSessionAsync(userId, "Device 1", "127.0.0.1", "agent1");
        var session2Id = await service.CreateSessionAsync(userId, "Device 2", "127.0.0.2", "agent2");
        await service.RevokeSessionAsync(userId, session2Id);

        var activeSessions = await service.GetActiveSessionsAsync(userId, session1Id);

        Assert.Single(activeSessions);
        Assert.Equal(session1Id, activeSessions[0].Id);
        Assert.True(activeSessions[0].IsCurrent);
    }

    [Fact]
    public async Task RevokeOtherSessionsAsync_RevokesAllExceptCurrent()
    {
        using var db = CreateDbContext();
        var service = new SessionService(db);
        var userId = UserId.New();

        var session1Id = await service.CreateSessionAsync(userId, "Current Device", "127.0.0.1", "agent1");
        var session2Id = await service.CreateSessionAsync(userId, "Other Device 1", "127.0.0.2", "agent2");
        var session3Id = await service.CreateSessionAsync(userId, "Other Device 2", "127.0.0.3", "agent3");

        await service.RevokeOtherSessionsAsync(userId, session1Id);

        var active = await service.GetActiveSessionsAsync(userId);
        Assert.Single(active);
        Assert.Equal(session1Id, active[0].Id);

        var s2 = await db.UserSessions.FindAsync(session2Id);
        var s3 = await db.UserSessions.FindAsync(session3Id);
        Assert.True(s2?.IsRevoked);
        Assert.True(s3?.IsRevoked);
    }

    [Fact]
    public async Task IsSessionActiveAsync_ReturnsTrueOnlyIfSessionExistsAndNotRevoked()
    {
        using var db = CreateDbContext();
        var service = new SessionService(db);
        var userId = UserId.New();

        var sessionId = await service.CreateSessionAsync(userId, "Device", "127.0.0.1", "agent");
        Assert.True(await service.IsSessionActiveAsync(sessionId));

        await service.RevokeSessionAsync(userId, sessionId);
        Assert.False(await service.IsSessionActiveAsync(sessionId));
        Assert.False(await service.IsSessionActiveAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task CreateSessionAsync_PopulatesAbsoluteExpiresAtUtc_30DaysFromCreation()
    {
        using var db = CreateDbContext();
        var startTime = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(startTime);
        var service = new SessionService(db, timeProvider);
        var userId = UserId.New();

        var sessionId = await service.CreateSessionAsync(userId, "Laptop", "10.0.0.1", "Mozilla/5.0");

        var session = await db.UserSessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(startTime, session.CreatedAtUtc);
        Assert.Equal(startTime.AddDays(30), session.AbsoluteExpiresAtUtc);
    }

    [Fact]
    public async Task IsSessionActiveAsync_RejectsExpiredSession_AndFiltersFromActiveList()
    {
        using var db = CreateDbContext();
        var startTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(startTime);
        var service = new SessionService(db, timeProvider);
        var userId = UserId.New();

        var sessionId = await service.CreateSessionAsync(userId, "Device", "127.0.0.1", "Agent");
        Assert.True(await service.IsSessionActiveAsync(sessionId));

        // Advance beyond 30 days
        timeProvider.Advance(TimeSpan.FromDays(30).Add(TimeSpan.FromSeconds(1)));

        // Cached or DB lookup should report inactive
        Assert.False(await service.IsSessionActiveAsync(sessionId));

        var activeSessions = await service.GetActiveSessionsAsync(userId);
        Assert.Empty(activeSessions);
    }

    [Fact]
    public async Task RevokeSessionAsync_CallsTerminator_WithSessionId()
    {
        using var db = CreateDbContext();
        var terminator = new TrackingSessionConnectionTerminator();
        var service = new SessionService(db, terminator: terminator);
        var userId = UserId.New();

        var sessionId = await service.CreateSessionAsync(userId, "Device", "127.0.0.1", "Agent");
        var result = await service.RevokeSessionAsync(userId, sessionId);

        Assert.True(result);
        Assert.Contains(sessionId, terminator.TerminatedSessionIds);
    }

    [Fact]
    public async Task RevokeOtherSessionsAsync_CallsTerminator_ForEachOtherSession()
    {
        using var db = CreateDbContext();
        var terminator = new TrackingSessionConnectionTerminator();
        var service = new SessionService(db, terminator: terminator);
        var userId = UserId.New();

        var currentId = await service.CreateSessionAsync(userId, "Current", "127.0.0.1", "Agent");
        var other1 = await service.CreateSessionAsync(userId, "Other 1", "127.0.0.2", "Agent");
        var other2 = await service.CreateSessionAsync(userId, "Other 2", "127.0.0.3", "Agent");

        await service.RevokeOtherSessionsAsync(userId, currentId);

        Assert.DoesNotContain(currentId, terminator.TerminatedSessionIds);
        Assert.Contains(other1, terminator.TerminatedSessionIds);
        Assert.Contains(other2, terminator.TerminatedSessionIds);
    }

    [Fact]
    public async Task RevokeAllSessionsAsync_CallsTerminator_WithUserId()
    {
        using var db = CreateDbContext();
        var terminator = new TrackingSessionConnectionTerminator();
        var service = new SessionService(db, terminator: terminator);
        var userId = UserId.New();

        await service.CreateSessionAsync(userId, "Device 1", "127.0.0.1", "Agent");
        await service.CreateSessionAsync(userId, "Device 2", "127.0.0.2", "Agent");

        await service.RevokeAllSessionsAsync(userId);

        Assert.Contains(userId, terminator.TerminatedUserIds);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36", "Chrome on Windows")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Safari/605.1.15", "Safari on macOS")]
    [InlineData("Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:109.0) Gecko/20100101 Firefox/119.0", "Firefox on Linux")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36 Edg/120.0.0.0", "Edge on Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1", "Safari on iOS")]
    [InlineData("", "Unknown Device")]
    [InlineData(null, "Unknown Device")]
    public void NormalizeUserAgent_SanitizesAndIdentifiesDevice(string? rawUserAgent, string expectedLabel)
    {
        var label = SessionService.NormalizeUserAgent(rawUserAgent);
        Assert.Equal(expectedLabel, label);
        Assert.True(label.Length <= 100);
    }

    [Fact]
    public async Task CreateSessionAsync_WithExtremelyLongUserAgent_TruncatesAndPersistsSafely()
    {
        using var db = CreateDbContext();
        var service = new SessionService(db);
        var userId = UserId.New();

        // 1000 characters of arbitrary junk user-agent
        var longUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/120.0.0.0 " + new string('A', 1000);

        var sessionId = await service.CreateSessionAsync(userId, "", "127.0.0.1", longUa);
        var session = await db.UserSessions.FindAsync(sessionId);

        Assert.NotNull(session);
        Assert.NotNull(session.UserAgent);
        Assert.True(session.UserAgent.Length <= 100);
        Assert.Equal("Chrome on Windows", session.UserAgent);
    }

    [Fact]
    public void SessionCache_SetLastActiveAtUtc_OnCacheMiss_DoesNotFabricateActiveEntry_AndPreservesExistingExpiration()
    {
        var startTime = new DateTimeOffset(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new TestTimeProvider(startTime);
        using var cache = new SessionCache(timeProvider);

        // 1. Cache miss on SetLastActiveAtUtc must NOT fabricate a +30-day active session entry
        var missingSessionId = Guid.NewGuid();
        cache.SetLastActiveAtUtc(missingSessionId, startTime);

        Assert.False(cache.TryGet(missingSessionId, out _));
        Assert.Null(cache.GetLastActiveAtUtc(missingSessionId));

        // 2. Existing entry with a short AbsoluteExpiresAtUtc (+2 min) preserves its exact expiration
        var activeSessionId = Guid.NewGuid();
        var originalExpiresAt = startTime.AddMinutes(2);
        cache.Set(activeSessionId, new CachedSessionInfo(IsRevoked: false, AbsoluteExpiresAtUtc: originalExpiresAt));
        Assert.Equal(startTime, cache.GetLastActiveAtUtc(activeSessionId));

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var updatedLastActive = timeProvider.GetUtcNow();
        cache.SetLastActiveAtUtc(activeSessionId, updatedLastActive);

        Assert.True(cache.TryGet(activeSessionId, out var cachedInfo));
        Assert.False(cachedInfo.IsRevoked);
        Assert.Equal(originalExpiresAt, cachedInfo.AbsoluteExpiresAtUtc);
        Assert.Equal(updatedLastActive, cache.GetLastActiveAtUtc(activeSessionId));
    }

    [Fact]
    public void SessionCache_IsIsolatedPerServiceProvider_WithNoStaticSingleton()
    {
        var provider1 = _fixture.CreateServiceProvider();
        var provider2 = _fixture.CreateServiceProvider();

        var cache1 = provider1.GetService(typeof(ISessionCache)) as ISessionCache;
        var cache2 = provider2.GetService(typeof(ISessionCache)) as ISessionCache;

        Assert.NotNull(cache1);
        Assert.NotNull(cache2);
        Assert.False(ReferenceEquals(cache1, cache2));

        var sessionId = Guid.NewGuid();
        cache1!.Set(sessionId, new CachedSessionInfo(IsRevoked: false, AbsoluteExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(10)));

        Assert.True(cache1.TryGet(sessionId, out _));
        Assert.False(cache2!.TryGet(sessionId, out _));
    }
}
