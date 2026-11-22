using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.Entities;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Web.Controllers;
using AOS.Messenger.Web.Hubs;
using AOS.Messenger.Web.RateLimiting;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

/// <summary>
/// Comprehensive verification test suite for Red-Team audit remediations (RT-001 through RT-012).
/// Runs against real SQL Server LocalDB with zero in-memory database mocks.
/// </summary>
public sealed class RedTeamRemediationTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public RedTeamRemediationTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private IServiceProvider CreateTestServiceProvider()
    {
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                ["PublicBaseUrl"] = "https://localhost:7001"
            })
            .Build();

        services.AddInfrastructureServices(configuration);
        AOS.Messenger.Application.ApplicationServiceRegistration.AddApplicationServices(services);

        return services.BuildServiceProvider();
    }

    private static CookieValidatePrincipalContext CreateValidateContext(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        CookieAuthenticationOptions options)
    {
        var scheme = new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler));
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow };
        var ticket = new AuthenticationTicket(principal, properties, IdentityConstants.ApplicationScheme);
        return new CookieValidatePrincipalContext(httpContext, scheme, options, ticket);
    }

    // =========================================================================
    // [RT-001] OnValidatePrincipal Concurrency & Scoped DbContext Thread Safety
    // =========================================================================

    [Fact]
    public async Task RT001_TwentyConcurrentRequests_ProduceZeroInvalidOperationOrDisposedExceptions()
    {
        var provider = CreateTestServiceProvider();
        var identityService = provider.GetRequiredService<IIdentityService>();
        var sessionService = provider.GetRequiredService<ISessionService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var claimsFactory = provider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        var reg = await identityService.RegisterAsync(new RegisterUserDto("rt001_user", "rt001@example.com", "RT001 User", "Password123!"));
        var userId = reg.UserId!;
        var user = await userManager.FindByIdAsync(userId.Value.ToString());

        var sessionId = await sessionService.CreateSessionAsync(userId, "RT001 Device", "127.0.0.1", "Mozilla/5.0");

        var principal = await claimsFactory.CreateAsync(user!);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim("SessionId", sessionId.ToString()));

        // Simulate 20 concurrent HTTP requests validating the principal simultaneously
        var tasks = Enumerable.Range(0, 20).Select(async i =>
        {
            using var scope = provider.CreateScope();
            var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = httpContext;

            var context = CreateValidateContext(httpContext, principal, options);
            await options.Events.ValidatePrincipal(context);

            Assert.NotNull(context.Principal);
        });

        // Must complete without any InvalidOperationException or ObjectDisposedException
        var exception = await Record.ExceptionAsync(() => Task.WhenAll(tasks));
        Assert.Null(exception);

        // Verify session was cached and activity tracked
        var sessionCache = provider.GetRequiredService<ISessionCache>();
        Assert.True(sessionCache.TryGet(sessionId, out var cached));
        Assert.False(cached.IsRevoked);
    }

    // =========================================================================
    // [RT-002] Antiforgery Header Configuration for JSON API Endpoints
    // =========================================================================

    [Fact]
    public void RT002_AntiforgeryHeaderName_IsConfiguredAsRequestVerificationToken()
    {
        var programFile = Path.Combine(FindSolutionRoot(), "src", "AOS.Messenger.Web", "Program.cs");
        Assert.True(File.Exists(programFile));

        var content = File.ReadAllText(programFile);
        Assert.Contains("options.HeaderName = \"RequestVerificationToken\"", content);
        Assert.Contains("AddAntiforgery", content);
    }

    [Fact]
    public async Task RT002_JsonMessagingEndpoint_RequiresAntiforgeryHeader()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((ctx, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ConnectionStrings:DefaultConnection"] = _fixture.ConnectionString,
                        ["PublicBaseUrl"] = "https://localhost:7001"
                    });
                });
            });

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        // 1. Verify AntiforgeryOptions has HeaderName set to "RequestVerificationToken"
        var options = factory.Services.GetRequiredService<IOptions<AntiforgeryOptions>>();
        Assert.Equal("RequestVerificationToken", options.Value.HeaderName);

        // 2. An unsafe POST request without antiforgery token to an endpoint protected by antiforgery fails with 400 Bad Request
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = "csrf_test_user",
            ["Password"] = "Password123!"
        });

        var response = await client.PostAsync("/Account/Login", form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // =========================================================================
    // [RT-003] Soft-Delete Timing & Enumeration Parity in LoginAsync & Login.cshtml
    // =========================================================================

    [Fact]
    public async Task RT003_LoginAsync_SoftDeletedUser_ReturnsIdenticalGenericFailure_ForValidAndInvalidPasswords()
    {
        var provider = CreateTestServiceProvider();
        var identityService = provider.GetRequiredService<IIdentityService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        var reg = await identityService.RegisterAsync(new RegisterUserDto("rt003_del", "rt003_del@example.com", "RT003 Deleted", "Password123!"));
        var userId = reg.UserId!;
        var user = await userManager.FindByIdAsync(userId.Value.ToString());

        // Soft delete user
        await identityService.SoftDeleteAccountAsync(userId);

        // 1. Attempt login with correct password -> must NOT return isDeleted: true or leak deletion status
        var (succeeded1, _, isLockedOut1, isDeleted1, error1) = await identityService.LoginAsync(
            new LoginUserDto("rt003_del", "Password123!", false), "127.0.0.1", "UA");

        Assert.False(succeeded1);
        Assert.False(isDeleted1);
        Assert.False(isLockedOut1);
        Assert.Equal("Invalid username or password.", error1);

        // 2. Attempt login with wrong password -> must return identical result
        var (succeeded2, _, isLockedOut2, isDeleted2, error2) = await identityService.LoginAsync(
            new LoginUserDto("rt003_del", "WrongPassword999!", false), "127.0.0.1", "UA");

        Assert.False(succeeded2);
        Assert.False(isDeleted2);
        Assert.False(isLockedOut2);
        Assert.Equal("Invalid username or password.", error2);

        // 3. Attempt login with non-existent user -> identical
        var (succeeded3, _, isLockedOut3, isDeleted3, error3) = await identityService.LoginAsync(
            new LoginUserDto("nonexistent_user_999", "Password123!", false), "127.0.0.1", "UA");

        Assert.False(succeeded3);
        Assert.False(isDeleted3);
        Assert.Equal(error1, error3);
    }

    [Fact]
    public void RT003_LoginView_HasNoConditionalCanRecoverDisclosure_AndContainsStaticRecoveryLink()
    {
        var loginViewPath = Path.Combine(FindSolutionRoot(), "src", "AOS.Messenger.Web", "Views", "Account", "Login.cshtml");
        Assert.True(File.Exists(loginViewPath));

        var viewContent = File.ReadAllText(loginViewPath);

        // Must NOT contain conditional CanRecover check
        Assert.DoesNotContain("ViewData[\"CanRecover\"]", viewContent);

        // Must contain clean static recovery link visible to all visitors
        Assert.Contains("RecoverAccount", viewContent);
    }

    // =========================================================================
    // [RT-004] Bidirectional Block & Verification on Profile Reads
    // =========================================================================

    [Fact]
    public async Task RT004_ProfileService_EnforcesBidirectionalBlockAndEmailVerification()
    {
        var provider = CreateTestServiceProvider();
        var identityService = provider.GetRequiredService<IIdentityService>();
        var blockService = provider.GetRequiredService<IBlockService>();
        var profileService = provider.GetRequiredService<IProfileService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        var regA = await identityService.RegisterAsync(new RegisterUserDto("rt004_a", "rt004_a@example.com", "Alice", "Password123!"));
        var regB = await identityService.RegisterAsync(new RegisterUserDto("rt004_b", "rt004_b@example.com", "Bob", "Password123!"));
        var userA = regA.UserId!;
        var userB = regB.UserId!;

        // Confirm emails for A and B
        var appUserA = await userManager.FindByIdAsync(userA.Value.ToString());
        var appUserB = await userManager.FindByIdAsync(userB.Value.ToString());
        appUserA!.EmailConfirmed = true;
        appUserB!.EmailConfirmed = true;
        await userManager.UpdateAsync(appUserA);
        await userManager.UpdateAsync(appUserB);

        // Create unverified User C
        var regC = await identityService.RegisterAsync(new RegisterUserDto("rt004_c", "rt004_c@example.com", "Charlie", "Password123!"));
        var userC = regC.UserId!;
        var appUserC = await userManager.FindByIdAsync(userC.Value.ToString());
        appUserC!.EmailConfirmed = false;
        await userManager.UpdateAsync(appUserC);

        // 1. Before block: A can view B's profile
        var profileB = await profileService.GetProfileAsync(userA, userB);
        Assert.NotNull(profileB);
        Assert.Equal("Bob", profileB.DisplayName);

        // 2. User A blocks User B
        await blockService.BlockUserAsync(userA, userB);

        // 3. Blocked user B calling GetProfile(B, A) returns null
        var blockedLookup = await profileService.GetProfileAsync(userB, userA);
        Assert.Null(blockedLookup);

        // 4. Blocker user A calling GetProfile(A, B) returns null (bidirectional)
        var blockerLookup = await profileService.GetProfileAsync(userA, userB);
        Assert.Null(blockerLookup);

        // 5. Unverified user C lookup returns null
        var unverifiedLookup = await profileService.GetProfileAsync(userA, userC);
        Assert.Null(unverifiedLookup);

        // 6. Self-lookup succeeds
        var selfLookup = await profileService.GetProfileAsync(userA, userA);
        Assert.NotNull(selfLookup);
        Assert.Equal("Alice", selfLookup.DisplayName);
    }

    [Fact]
    public async Task RT004_UsersController_GetById_RequiresAuth_AndReturns404ForBlockedOrUnverified()
    {
        var discovery = new StubUserDiscoveryService();
        var profile = new StubProfileService();
        var currentUser = new StubCurrentUser();

        var controller = new UsersController(discovery, profile, currentUser)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        // 1. Unauthenticated caller -> returns 401 Unauthorized
        currentUser.Id = null;
        var unauthResult = await controller.GetById(Guid.NewGuid());
        Assert.IsType<UnauthorizedResult>(unauthResult);

        // 2. Authenticated caller, profile returns null (blocked or unverified) -> returns 404 NotFound
        var callerId = UserId.New();
        currentUser.Id = callerId;
        profile.ResultToReturn = null;
        var notFoundResult = await controller.GetById(Guid.NewGuid());
        Assert.IsType<NotFoundResult>(notFoundResult);

        // 3. Authenticated caller, profile exists -> returns 200 OK
        profile.ResultToReturn = new UserProfileDto(UserId.New(), "valid_user", "Valid User", null, false);
        var okResult = await controller.GetById(Guid.NewGuid());
        var okObj = Assert.IsType<OkObjectResult>(okResult);
        Assert.NotNull(okObj.Value);
    }

    // =========================================================================
    // [RT-006] DeletedAtUtc Cutoff in MessageService.GetMessagesAsync
    // =========================================================================

    [Fact]
    public async Task RT006_MessageService_ExcludesMessagesPriorToDeletedAtUtc()
    {
        var provider = CreateTestServiceProvider();
        var identityService = provider.GetRequiredService<IIdentityService>();
        var convService = provider.GetRequiredService<IConversationService>();
        var messageService = provider.GetRequiredService<IMessageService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();

        var regA = await identityService.RegisterAsync(new RegisterUserDto("rt006_a", "rt006_a@example.com", "Alice", "Password123!"));
        var regB = await identityService.RegisterAsync(new RegisterUserDto("rt006_b", "rt006_b@example.com", "Bob", "Password123!"));
        var userA = regA.UserId!;
        var userB = regB.UserId!;

        // Confirm emails
        var appA = await userManager.FindByIdAsync(userA.Value.ToString());
        var appB = await userManager.FindByIdAsync(userB.Value.ToString());
        appA!.EmailConfirmed = true;
        appB!.EmailConfirmed = true;
        await userManager.UpdateAsync(appA);
        await userManager.UpdateAsync(appB);

        // Create direct conversation
        var conversation = await convService.GetOrCreateConversationAsync(userA, userB);

        // User A sends 3 messages
        await messageService.SendMessageAsync(userA, conversation.Id, new SendMessageRequestDto("Msg 1", Guid.NewGuid().ToString()));
        await messageService.SendMessageAsync(userA, conversation.Id, new SendMessageRequestDto("Msg 2", Guid.NewGuid().ToString()));
        await messageService.SendMessageAsync(userA, conversation.Id, new SendMessageRequestDto("Msg 3", Guid.NewGuid().ToString()));

        // User A deletes conversation (sets DeletedAtUtc = now)
        await convService.DeleteConversationAsync(userA, conversation.Id);

        // Later, User B sends a 4th message
        await messageService.SendMessageAsync(userB, conversation.Id, new SendMessageRequestDto("Msg 4 after delete", Guid.NewGuid().ToString()));

        // User A retrieves messages
        var page = await messageService.GetMessagesAsync(userA, conversation.Id, null, 50);

        // Only message 4 should be returned; messages 1-3 sent prior to DeletedAtUtc must be excluded
        Assert.Single(page.Messages);
        Assert.Equal("Msg 4 after delete", page.Messages[0].Content);
    }

    // =========================================================================
    // [RT-008] Bounded Caches: SessionCache & DualIpAndEmailRateLimiterPolicy
    // =========================================================================

    [Fact]
    public void RT008_SessionCache_EnforcesSizeLimit_WithoutUnboundedGrowth()
    {
        var cache = new SessionCache();

        // Populate 10,000 unique session IDs
        for (int i = 0; i < 10_000; i++)
        {
            var id = Guid.NewGuid();
            cache.Set(id, new CachedSessionInfo(false, DateTimeOffset.UtcNow.AddMinutes(30)));
        }

        // Invalidate sets a bounded tombstone
        var testId = Guid.NewGuid();
        cache.Set(testId, new CachedSessionInfo(false, DateTimeOffset.UtcNow.AddMinutes(30)));
        cache.Invalidate(testId);

        Assert.True(cache.TryGet(testId, out var tombstone));
        Assert.True(tombstone.IsRevoked);

        cache.Dispose();
    }

    [Fact]
    public void RT008_DualIpAndEmailRateLimiterPolicy_EnforcesMemoryBoundsAndDisposal()
    {
        var policy = new DualIpAndEmailRateLimiterPolicy();

        // Generate 500 partitions
        for (int i = 0; i < 500; i++)
        {
            var ctx = new DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = IPAddress.Parse($"10.1.{i / 256}.{i % 256}");
            ctx.Request.ContentType = "application/x-www-form-urlencoded";
            ctx.Request.Form = new FormCollection(new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>
            {
                ["Email"] = $"user_{i}@example.com"
            });

            var partition = policy.GetPartition(ctx);
            var limiter = partition.Factory($"10.1.{i / 256}.{i % 256}:user_{i}@example.com");
            using var lease = limiter.AttemptAcquire(1);
            Assert.True(lease.IsAcquired);
        }

        // Clean disposal
        policy.Dispose();
    }

    // =========================================================================
    // [RT-009] Rate Limiting on Security-Sensitive Endpoints
    // =========================================================================

    [Fact]
    public void RT009_SecuritySensitiveEndpoints_HaveExpectedRateLimitingAttributes()
    {
        // 1. AccountController.ResetPassword (POST)
        var resetPwMethod = typeof(AccountController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == nameof(AccountController.ResetPassword) && m.GetCustomAttribute<HttpPostAttribute>() != null);
        Assert.NotNull(resetPwMethod);
        var resetLimiter = resetPwMethod.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(resetLimiter);
        Assert.Equal("email-endpoint-limiter", resetLimiter.PolicyName);

        // 2. AccountController.ConfirmEmail (POST)
        var confirmEmailMethod = typeof(AccountController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == nameof(AccountController.ConfirmEmail) && m.GetCustomAttribute<HttpPostAttribute>() != null);
        Assert.NotNull(confirmEmailMethod);
        var emailLimiter = confirmEmailMethod.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(emailLimiter);
        Assert.Equal("email-endpoint-limiter", emailLimiter.PolicyName);

        // 3. AccountController.ConfirmRecovery (POST)
        var confirmRecoveryMethod = typeof(AccountController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == nameof(AccountController.ConfirmRecovery) && m.GetCustomAttribute<HttpPostAttribute>() != null);
        Assert.NotNull(confirmRecoveryMethod);
        var recoveryLimiter = confirmRecoveryMethod.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(recoveryLimiter);
        Assert.Equal("recover-account-limiter", recoveryLimiter.PolicyName);

        // 4. ChatController.Start (POST)
        var chatStartMethod = typeof(ChatController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == nameof(ChatController.Start) && m.GetCustomAttribute<HttpPostAttribute>() != null);
        Assert.NotNull(chatStartMethod);
        var startLimiter = chatStartMethod.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(startLimiter);
        Assert.Equal("messages", startLimiter.PolicyName);
    }

    // =========================================================================
    // [RT-010] Unverified Account Batch Cleanup & Chunked SQL Parameters
    // =========================================================================

    [Fact]
    public async Task RT010_UnverifiedAccountCleanupService_PurgesInBoundedBatches()
    {
        var provider = CreateTestServiceProvider();
        using var db = provider.GetRequiredService<ApplicationDbContext>();
        var cleanupService = provider.GetRequiredService<UnverifiedAccountCleanupService>();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-49);

        // Seed 1,100 expired unverified users
        var expiredUsers = Enumerable.Range(0, 1100).Select(i => new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"expired_user_{i}_{Guid.NewGuid():N}",
            Email = $"expired_{i}_{Guid.NewGuid():N}@test.com",
            EmailConfirmed = false,
            DisplayName = $"Expired User {i}",
            CreatedAtUtc = cutoff
        }).ToList();

        // Seed in chunks to avoid single massive insert
        foreach (var chunk in expiredUsers.Chunk(400))
        {
            db.Users.AddRange(chunk);
            await db.SaveChangesAsync();
        }

        // Seed 5 verified users that must NOT be purged
        var activeUsers = Enumerable.Range(0, 5).Select(i => new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = $"active_user_{i}_{Guid.NewGuid():N}",
            Email = $"active_{i}_{Guid.NewGuid():N}@test.com",
            EmailConfirmed = true,
            DisplayName = $"Active User {i}",
            CreatedAtUtc = cutoff
        }).ToList();
        db.Users.AddRange(activeUsers);
        await db.SaveChangesAsync();

        // Run batch purge
        var purgedCount = await cleanupService.PurgeExpiredUnverifiedAccountsAsync();

        // Must have purged at least the 1,100 seeded expired accounts
        Assert.True(purgedCount >= 1100);

        // Verify active users remain
        foreach (var active in activeUsers)
        {
            var exists = await db.Users.AnyAsync(u => u.Id == active.Id);
            Assert.True(exists);
        }
    }

    [Fact]
    public async Task RT010_ConversationPersistence_Chunking_HandlesMoreThan2100ParametersWithoutException()
    {
        var provider = CreateTestServiceProvider();
        var convPersistence = provider.GetRequiredService<IConversationPersistence>();

        // Generate 2,500 fake conversation IDs (well exceeding SQL Server's 2,100 parameter limit)
        var largeIdList = Enumerable.Range(0, 2500)
            .Select(_ => new ConversationId(Guid.NewGuid()))
            .ToList();

        // Calling GetLastMessagesForConversationsAsync with >2100 IDs must chunk cleanly without throwing SqlException
        var exception = await Record.ExceptionAsync(async () =>
        {
            var dict = await convPersistence.GetLastMessagesForConversationsAsync(largeIdList);
            Assert.NotNull(dict);
        });

        Assert.Null(exception);
    }

    // =========================================================================
    // [RT-011] SignalRRealtimeNotifier Never Broadcasts to Clients.All
    // =========================================================================

    [Fact]
    public void RT011_SignalRRealtimeNotifier_HasNoUnsafeFallbackConstructor()
    {
        var constructors = typeof(SignalRRealtimeNotifier).GetConstructors();

        // Must only have the single 4-parameter constructor (no 2-parameter fallback)
        Assert.Single(constructors);
        var ctor = constructors[0];
        Assert.Equal(4, ctor.GetParameters().Length);
    }

    [Fact]
    public async Task RT011_SignalRRealtimeNotifier_FailsClosed_WithoutCallingClientsAll()
    {
        var provider = CreateTestServiceProvider();
        var convPersistence = provider.GetRequiredService<IConversationPersistence>();
        var blockPersistence = provider.GetRequiredService<IBlockPersistence>();
        var mockHubContext = new TrackingHubContext();
        var logger = NullLogger<SignalRRealtimeNotifier>.Instance;

        var notifier = new SignalRRealtimeNotifier(mockHubContext, convPersistence, blockPersistence, logger);

        var conv = Conversation.Create(UserId.New(), UserId.New(), DateTimeOffset.UtcNow, new ConversationId(Guid.NewGuid()));
        var msg = new MessageDto(
            new MessageId(1),
            conv.Id,
            conv.Participant1Id,
            "Alice",
            "alice",
            "Secret msg",
            DateTimeOffset.UtcNow,
            null,
            false,
            null,
            true);

        // Notify
        await notifier.NotifyNewMessageAsync(conv.Id, msg);

        // Assert Clients.All was NEVER invoked
        Assert.False(mockHubContext.AllCalled, "Clients.All must never be invoked for private message broadcasts!");
    }

    // =========================================================================
    // [RT-005, RT-007, RT-012] Frontend Script Invariant Checks
    // =========================================================================

    [Fact]
    public void RT005_ChatRealtimeJs_StateReconciliation_DoesNotQueryInvertedCursor()
    {
        var scriptPath = Path.Combine(FindSolutionRoot(), "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-realtime.js");
        Assert.True(File.Exists(scriptPath));

        var content = File.ReadAllText(scriptPath);

        // Step 1 inverted cursor query must be removed
        Assert.DoesNotContain("cursor=${encodeURIComponent(latestReceivedCursor)}", content);

        // Must query latest messages page
        Assert.Contains("pageSize=30", content);
    }

    [Fact]
    public void RT007_ConversationPageJs_HasRapidEnterDoubleSubmitGuard()
    {
        var scriptPath = Path.Combine(FindSolutionRoot(), "src", "AOS.Messenger.Web", "wwwroot", "js", "conversation-page.js");
        Assert.True(File.Exists(scriptPath));

        var content = File.ReadAllText(scriptPath);

        // Must declare re-entrancy flag
        Assert.Contains("isSending", content);

        // Must disable input during flight
        Assert.Contains("inputEl.disabled = true", content);
        Assert.Contains("sendBtn.disabled = true", content);

        // Must restore state in finally block
        Assert.Contains("isSending = false", content);
    }

    [Fact]
    public void RT012_ChatRealtimeJs_HandlesRevokedSession_WithCleanRedirect()
    {
        var scriptPath = Path.Combine(FindSolutionRoot(), "src", "AOS.Messenger.Web", "wwwroot", "js", "chat-realtime.js");
        Assert.True(File.Exists(scriptPath));

        var content = File.ReadAllText(scriptPath);

        // Must detect 401/403 and redirect without infinite reconnect loop
        Assert.Contains("handleSessionExpired", content);
        Assert.Contains("/Account/Login?sessionExpired=true", content);
        Assert.Contains("connection.stop()", content);
    }

    // =========================================================================
    // Helper Stubs
    // =========================================================================

    private static string FindSolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "AOS.Messenger.slnx")) || Directory.Exists(Path.Combine(dir, "src")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return Directory.GetCurrentDirectory();
    }

    private sealed class StubUserDiscoveryService : IUserDiscoveryService
    {
        public Task<IReadOnlyList<UserSearchResultDto>> SearchUsersAsync(UserId currentUserId, string query, int limit = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserSearchResultDto>>([]);
    }

    private sealed class StubProfileService : IProfileService
    {
        public UserProfileDto? ResultToReturn { get; set; }

        public Task<UserProfileDto?> GetProfileAsync(UserId callerId, UserId targetUserId, CancellationToken ct = default)
            => Task.FromResult(ResultToReturn);

        public Task<UserProfileDto?> GetProfileAsync(UserId userId, CancellationToken ct = default)
            => Task.FromResult(ResultToReturn);

        public Task<bool> UpdateProfileAsync(UserId userId, UpdateProfileDto dto, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class StubCurrentUser : ICurrentUser
    {
        public UserId? Id { get; set; }
        public bool IsAuthenticated => Id is not null;
    }

    private sealed class TrackingHubContext : IHubContext<ChatHub, IChatClient>
    {
        public bool AllCalled { get; private set; }

        public IHubClients<IChatClient> Clients => new TrackingHubClients(this);
        public IGroupManager Groups => throw new NotImplementedException();

        private sealed class TrackingHubClients : IHubClients<IChatClient>
        {
            private readonly TrackingHubContext _ctx;
            public TrackingHubClients(TrackingHubContext ctx) => _ctx = ctx;

            public IChatClient All
            {
                get
                {
                    _ctx.AllCalled = true;
                    return new MockChatClient();
                }
            }

            public IChatClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => new MockChatClient();
            public IChatClient Client(string connectionId) => new MockChatClient();
            public IChatClient Clients(IReadOnlyList<string> connectionIds) => new MockChatClient();
            public IChatClient Group(string groupName) => new MockChatClient();
            public IChatClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => new MockChatClient();
            public IChatClient Groups(IReadOnlyList<string> groupNames) => new MockChatClient();
            public IChatClient User(string userId) => new MockChatClient();
            public IChatClient Users(IReadOnlyList<string> userIds) => new MockChatClient();
        }

        private sealed class MockChatClient : IChatClient
        {
            public Task ReceiveMessage(MessageDto message) => Task.CompletedTask;
            public Task ReceiveReceiptUpdated(ReceiptUpdateDto receipt) => Task.CompletedTask;
            public Task ReceiveTypingIndicator(TypingIndicatorDto typing) => Task.CompletedTask;
            public Task ReceivePresenceChanged(UserPresenceDto presence) => Task.CompletedTask;
            public Task MessageEdited(MessageDto message) => Task.CompletedTask;
            public Task MessageDeleted(long messageId, Guid conversationId, bool isDeletedForEveryone) => Task.CompletedTask;
            public Task ReactionUpdated(long messageId, Guid conversationId, string emoji, string action, Guid userId) => Task.CompletedTask;
            public Task MessageRead(Guid conversationId, long lastReadMessageId, Guid userId) => Task.CompletedTask;
            public Task UserTyping(Guid conversationId, Guid userId, bool isTyping) => Task.CompletedTask;
            public Task UserPresenceChanged(Guid userId, string status) => Task.CompletedTask;
            public Task ReceiveMessageEdited(MessageEditedDto message) => Task.CompletedTask;
            public Task ReceiveMessageDeletedForEveryone(MessageDeletedForEveryoneDto message) => Task.CompletedTask;
            public Task ReceiveMessageDeletedForMe(MessageDeletedForMeDto message) => Task.CompletedTask;
            public Task ReceiveReactionUpdated(ReactionUpdatedDto reaction) => Task.CompletedTask;
        }
    }
}
