using System.Security.Claims;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Application.DTOs;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AOS.Messenger.Tests;

public sealed class AuthCookieAndPrincipalVerificationTests : IClassFixture<SqlTestDatabaseFixture>
{
    private readonly SqlTestDatabaseFixture _fixture;

    public AuthCookieAndPrincipalVerificationTests(SqlTestDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private IServiceProvider CreateTestServiceProvider(IConfiguration? config = null)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        var configuration = config ?? new ConfigurationBuilder()
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

    [Fact]
    public void CookieConfiguration_HasStrictSecuritySettings()
    {
        var provider = CreateTestServiceProvider();
        var optionsMonitor = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var options = optionsMonitor.Get(IdentityConstants.ApplicationScheme);

        Assert.Equal("__Host-AOS.Messenger.Auth", options.Cookie.Name);
        Assert.Equal("/", options.Cookie.Path);
        Assert.True(options.Cookie.HttpOnly);
        Assert.Equal(SameSiteMode.Lax, options.Cookie.SameSite);
        Assert.Equal(CookieSecurePolicy.Always, options.Cookie.SecurePolicy);
        Assert.Equal(TimeSpan.FromDays(30), options.ExpireTimeSpan);
        Assert.True(options.SlidingExpiration);
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

    [Fact]
    public async Task OnValidatePrincipal_WhenSessionIdClaimMissing_RejectsPrincipal()
    {
        var provider = CreateTestServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Name, "testuser"));
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = httpContext;

        var context = CreateValidateContext(httpContext, principal, options);

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task OnValidatePrincipal_WhenSessionIdMalformed_RejectsPrincipal()
    {
        var provider = CreateTestServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        var identity = new ClaimsIdentity(IdentityConstants.ApplicationScheme);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()));
        identity.AddClaim(new Claim("SessionId", "not-a-valid-guid"));
        var principal = new ClaimsPrincipal(identity);

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = httpContext;

        var context = CreateValidateContext(httpContext, principal, options);

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task OnValidatePrincipal_WhenSessionRevoked_RejectsPrincipal()
    {
        var provider = CreateTestServiceProvider();
        var identityService = provider.GetRequiredService<IIdentityService>();
        var sessionService = provider.GetRequiredService<ISessionService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var claimsFactory = provider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        var reg = await identityService.RegisterAsync(new RegisterUserDto("val_user1", "val1@example.com", "Val 1", "Password123!"));
        var userId = reg.UserId!;
        var user = await userManager.FindByIdAsync(userId.Value.ToString());

        var sessionId = await sessionService.CreateSessionAsync(userId, "Browser", "127.0.0.1", "Mozilla/5.0");
        await sessionService.RevokeSessionAsync(userId, sessionId);

        var principal = await claimsFactory.CreateAsync(user!);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim("SessionId", sessionId.ToString()));

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = httpContext;

        var context = CreateValidateContext(httpContext, principal, options);

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task OnValidatePrincipal_WhenSecurityStampRotated_RejectsPrincipal()
    {
        var provider = CreateTestServiceProvider();
        var identityService = provider.GetRequiredService<IIdentityService>();
        var sessionService = provider.GetRequiredService<ISessionService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var claimsFactory = provider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        var reg = await identityService.RegisterAsync(new RegisterUserDto("val_user2", "val2@example.com", "Val 2", "Password123!"));
        var userId = reg.UserId!;
        var user = await userManager.FindByIdAsync(userId.Value.ToString());

        var sessionId = await sessionService.CreateSessionAsync(userId, "Browser", "127.0.0.1", "Mozilla/5.0");

        // Build principal with current security stamp
        var principal = await claimsFactory.CreateAsync(user!);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim("SessionId", sessionId.ToString()));

        // Rotate security stamp on server afterwards
        await userManager.UpdateSecurityStampAsync(user!);

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = httpContext;

        // Force validation interval to be exceeded so SecurityStampValidator queries the database
        var scheme = new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler));
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow.AddMinutes(-35) };
        var ticket = new AuthenticationTicket(principal, properties, IdentityConstants.ApplicationScheme);
        var context = new CookieValidatePrincipalContext(httpContext, scheme, options, ticket);

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task OnValidatePrincipal_WhenSessionActiveAndValid_PreservesPrincipalAndUsesCache()
    {
        var provider = CreateTestServiceProvider();
        var identityService = provider.GetRequiredService<IIdentityService>();
        var sessionService = provider.GetRequiredService<ISessionService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var claimsFactory = provider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        var reg = await identityService.RegisterAsync(new RegisterUserDto("val_user3", "val3@example.com", "Val 3", "Password123!"));
        var userId = reg.UserId!;
        var user = await userManager.FindByIdAsync(userId.Value.ToString());

        var sessionId = await sessionService.CreateSessionAsync(userId, "Browser", "127.0.0.1", "Mozilla/5.0");

        var principal = await claimsFactory.CreateAsync(user!);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim("SessionId", sessionId.ToString()));

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = httpContext;

        var context = CreateValidateContext(httpContext, principal, options);

        await options.Events.ValidatePrincipal(context);

        Assert.NotNull(context.Principal);

        // Verify session was placed in cache
        var sessionCache = provider.GetRequiredService<ISessionCache>();
        Assert.True(sessionCache.TryGet(sessionId, out var cached));
        Assert.False(cached.IsRevoked);
        Assert.True(cached.AbsoluteExpiresAtUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OnValidatePrincipal_WhenSessionExpired_RejectsPrincipal()
    {
        var provider = CreateTestServiceProvider();
        var identityService = provider.GetRequiredService<IIdentityService>();
        var userManager = provider.GetRequiredService<UserManager<ApplicationUser>>();
        var claimsFactory = provider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        using var db = provider.GetRequiredService<ApplicationDbContext>();
        var options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);

        var reg = await identityService.RegisterAsync(new RegisterUserDto("val_user4", "val4@example.com", "Val 4", "Password123!"));
        var userId = reg.UserId!;
        var user = await userManager.FindByIdAsync(userId.Value.ToString());

        var expiredSession = new UserSession
        {
            UserId = userId.Value,
            DeviceName = "Old Device",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-40),
            LastActiveAtUtc = DateTimeOffset.UtcNow.AddDays(-40),
            AbsoluteExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(-10),
            IsRevoked = false
        };
        db.UserSessions.Add(expiredSession);
        await db.SaveChangesAsync();

        var principal = await claimsFactory.CreateAsync(user!);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim("SessionId", expiredSession.Id.ToString()));

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        var accessor = provider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = httpContext;

        var context = CreateValidateContext(httpContext, principal, options);

        await options.Events.ValidatePrincipal(context);

        Assert.Null(context.Principal);
    }
}
