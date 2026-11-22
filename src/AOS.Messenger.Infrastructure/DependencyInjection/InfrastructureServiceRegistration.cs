using System.Security.Claims;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AOS.Messenger.Infrastructure;

/// <summary>
/// DI registration entry point for the Infrastructure layer.
/// Called from Web/Program.cs to register EF Core, Identity, and external service adapters.
/// </summary>
public static class InfrastructureServiceRegistration
{
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        var keysDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AOS.Messenger",
            "keys");
        Directory.CreateDirectory(keysDirectory);

        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
            .SetApplicationName("AOS.Messenger");

        services.Configure<EmailOptions>(options =>
        {
            var baseUrl = configuration["PublicBaseUrl"]
                ?? configuration["Email:PublicBaseUrl"]
                ?? "https://localhost:7001";
            options.PublicBaseUrl = baseUrl.TrimEnd('/');
        });

        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Server=(localdb)\\mssqllocaldb;Database=AOS.Messenger;Trusted_Connection=True;MultipleActiveResultSets=true";

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer(connectionString));

        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.SignIn.RequireConfirmedEmail = true;
            options.Password.RequireDigit = true;
            options.Password.RequiredLength = 8;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireLowercase = false;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "__Host-AOS.Messenger.Auth";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
            options.LoginPath = "/Account/Login";
            options.LogoutPath = "/Account/Logout";
            options.AccessDeniedPath = "/Account/AccessDenied";

            options.Events = new CookieAuthenticationEvents
            {
                OnValidatePrincipal = async context =>
                {
                    // 1. Call await SecurityStampValidator.ValidatePrincipalAsync(context) first. If principal rejected, return immediately.
                    await SecurityStampValidator.ValidatePrincipalAsync(context);
                    if (context.Principal is null)
                    {
                        return;
                    }

                    // 2. Require SessionId claim. If missing, malformed, revoked, or past AbsoluteExpiresAtUtc, call context.RejectPrincipal() and await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme).
                    var sessionClaim = context.Principal.FindFirstValue("SessionId");
                    if (string.IsNullOrWhiteSpace(sessionClaim) || !Guid.TryParse(sessionClaim, out var sessionId))
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                        return;
                    }

                    // 3. Use the in-memory session cache for fast lookup while invalidating on revocation.
                    var sessionService = context.HttpContext.RequestServices.GetRequiredService<ISessionService>();
                    var isActive = await sessionService.IsSessionActiveAsync(sessionId, context.HttpContext.RequestAborted);
                    if (!isActive)
                    {
                        context.RejectPrincipal();
                        await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                        return;
                    }

                    // 4. Update activity periodically (throttled to once every 5 minutes, directly awaited without fire-and-forget background tasks)
                    var sessionCache = context.HttpContext.RequestServices.GetService<ISessionCache>() as SessionCache ?? SessionCache.Instance;
                    var timeProvider = context.HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
                    var now = timeProvider.GetUtcNow();
                    var lastActive = sessionCache.GetLastActiveAtUtc(sessionId);
                    if (!lastActive.HasValue || now - lastActive.Value >= TimeSpan.FromMinutes(5))
                    {
                        await sessionService.UpdateSessionActivityAsync(sessionId, context.HttpContext.RequestAborted);
                        sessionCache.SetLastActiveAtUtc(sessionId, now);
                    }
                }
            };
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISessionConnectionTerminator, NullSessionConnectionTerminator>();
        services.AddSingleton<ISessionCache>(SessionCache.Instance);
        services.AddSingleton(SessionCache.Instance);
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IEmailSender, DevelopmentEmailSender>();
        services.AddScoped<IBlockPersistence, BlockPersistence>();
        services.AddScoped<IUserReader, UserReader>();
        services.AddScoped<IUserWriter, UserWriter>();
        services.AddScoped<IConversationPersistence, ConversationPersistence>();
        services.AddScoped<IUserPresencePersistence, UserPresencePersistence>();
        services.AddScoped<IMessagePersistence, MessagePersistence>();
        services.AddScoped<IReactionPersistence, ReactionPersistence>();

        services.AddSingleton<UnverifiedAccountCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<UnverifiedAccountCleanupService>());

        return services;
    }
}
