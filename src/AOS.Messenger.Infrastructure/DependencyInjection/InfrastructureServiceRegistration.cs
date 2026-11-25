using System.Security.Claims;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Infrastructure.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

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
                    var sessionCache = context.HttpContext.RequestServices.GetRequiredService<ISessionCache>();
                    var timeProvider = context.HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;
                    var now = timeProvider.GetUtcNow();
                    var lastActive = sessionCache.GetLastActiveAtUtc(sessionId);
                    if (!lastActive.HasValue || now - lastActive.Value >= TimeSpan.FromMinutes(5))
                    {
                        await sessionService.UpdateSessionActivityAsync(sessionId, context.HttpContext.RequestAborted);
                        sessionCache.SetLastActiveAtUtc(sessionId, now);
                    }
                },
                OnRedirectToLogin = context =>
                {
                    if (IsApiOrHubRequest(context.Request))
                    {
                        context.Response.Headers.CacheControl = "no-store, no-cache";
                        context.Response.Headers.Pragma = "no-cache";
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                },
                OnRedirectToAccessDenied = context =>
                {
                    if (IsApiOrHubRequest(context.Request))
                    {
                        context.Response.Headers.CacheControl = "no-store, no-cache";
                        context.Response.Headers.Pragma = "no-cache";
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return Task.CompletedTask;
                    }

                    context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                }
            };
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ISessionConnectionTerminator, NullSessionConnectionTerminator>();
        services.AddSingleton<SessionCache>();
        services.AddSingleton<ISessionCache>(sp => sp.GetRequiredService<SessionCache>());
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
        services.AddScoped<IMediaPersistence, MediaPersistence>();
        services.AddScoped<IAvatarPersistence, AvatarPersistence>();
        services.AddScoped<IStickerPersistence, StickerPersistence>();

        services.AddOptions<MediaOptions>()
            .Bind(configuration.GetSection(MediaOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MediaOptions>, MediaOptionsValidator>();

        services.AddOptions<R2StorageOptions>()
            .Bind(configuration.GetSection(R2StorageOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<R2StorageOptions>>(sp =>
            new R2StorageOptionsValidator(sp.GetService<IHostEnvironment>()));

        services.AddSingleton<LocalMediaSigningService>();
        services.AddSingleton(sp =>
        {
            var signingService = sp.GetRequiredService<LocalMediaSigningService>();
            var environment = sp.GetService<IHostEnvironment>();
            var configuredRoot = configuration["Media:LocalStoragePath"];
            var rootDirectory = !string.IsNullOrWhiteSpace(configuredRoot)
                ? configuredRoot
                : Path.Combine(environment?.ContentRootPath ?? Directory.GetCurrentDirectory(), ".dev-media");
            return new LocalDiskMediaStorage(signingService, rootDirectory);
        });

        services.AddSingleton<IMediaStorage>(sp =>
        {
            var r2Options = sp.GetRequiredService<IOptions<R2StorageOptions>>().Value;
            if (r2Options.IsConfigured)
            {
                return ActivatorUtilities.CreateInstance<CloudflareR2MediaStorage>(sp);
            }

            var environment = sp.GetService<IHostEnvironment>();
            if (environment is not null && !environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "Cloudflare R2 storage configuration ('R2:ServiceUrl', 'R2:BucketName', 'R2:AccessKeyId', 'R2:SecretAccessKey') is required outside Development.");
            }

            return sp.GetRequiredService<LocalDiskMediaStorage>();
        });

        services.AddSingleton<ImageProcessingGate>();
        services.AddSingleton<IMediaVerifier, MediaVerifier>();
        services.AddSingleton<IAvatarProcessor, AvatarProcessor>();
        services.AddSingleton<IStickerProcessor, StickerProcessor>();

        services.AddTransient<IStartupFilter>(sp =>
            new DevMediaStartupFilter(
                sp.GetRequiredService<IHostEnvironment>(),
                sp.GetRequiredService<IOptions<R2StorageOptions>>(),
                sp.GetRequiredService<LocalMediaSigningService>(),
                sp.GetRequiredService<LocalDiskMediaStorage>(),
                sp.GetRequiredService<TimeProvider>()));

        services.AddSingleton<UnverifiedAccountCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<UnverifiedAccountCleanupService>());

        services.AddSingleton<MediaCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<MediaCleanupService>());

        return services;
    }

    private static bool IsApiOrHubRequest(HttpRequest request)
    {
        return request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || request.Path.StartsWithSegments("/hubs", StringComparison.OrdinalIgnoreCase)
            || request.Headers.Accept.Any(h => h?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            || string.Equals(request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    }
}
