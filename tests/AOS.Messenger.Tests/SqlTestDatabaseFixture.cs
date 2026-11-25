using AOS.Messenger.Application;
using AOS.Messenger.Application.Common;
using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Domain.ValueObjects;
using AOS.Messenger.Infrastructure;
using AOS.Messenger.Infrastructure.Identity;
using AOS.Messenger.Infrastructure.Persistence;
using AOS.Messenger.Infrastructure.Persistence.Repositories;
using AOS.Messenger.Infrastructure.Storage;
using AOS.Messenger.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AOS.Messenger.Tests;

/// <summary>
/// Reusable SQL Server LocalDB test database fixture.
/// Provides an isolated unique database per test class, runs EF Core migrations via MigrateAsync(),
/// and tears down the database cleanly on disposal.
/// </summary>
public sealed class SqlTestDatabaseFixture : IAsyncLifetime
{
    public string DatabaseName { get; }
    public string ConnectionString { get; }
    public string MediaRootDirectory { get; }

    public SqlTestDatabaseFixture()
    {
        DatabaseName = $"AOS_Test_{Guid.NewGuid():N}";
        ConnectionString = $"Server=(localdb)\\mssqllocaldb;Database={DatabaseName};Trusted_Connection=True;MultipleActiveResultSets=true;Connect Timeout=30;Encrypt=False;";
        MediaRootDirectory = Path.Combine(Path.GetTempPath(), $"AOS_TestMedia_{Guid.NewGuid():N}");
    }

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(MediaRootDirectory);
        await using var context = CreateDbContext();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        try
        {
            SqlConnection.ClearAllPools();
            await using var context = CreateDbContext();
            await context.Database.EnsureDeletedAsync();
        }
        catch
        {
            // Best effort database cleanup
        }

        try
        {
            if (Directory.Exists(MediaRootDirectory))
            {
                Directory.Delete(MediaRootDirectory, recursive: true);
            }
        }
        catch
        {
            // Best effort temp media directory cleanup
        }
    }

    public DbContextOptions<ApplicationDbContext> CreateOptions()
    {
        return new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
    }

    public ApplicationDbContext CreateDbContext()
    {
        return new ApplicationDbContext(CreateOptions());
    }

    public IServiceCollection CreateServiceCollection()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddDebug());
        services.AddSingleton(TimeProvider.System);

        services.AddDbContext<ApplicationDbContext>(opts =>
            opts.UseSqlServer(ConnectionString));

        services.AddIdentity<ApplicationUser, IdentityRole<Guid>>(opts =>
        {
            opts.User.RequireUniqueEmail = true;
            opts.Password.RequireDigit = true;
            opts.Password.RequiredLength = 8;
            opts.Password.RequireNonAlphanumeric = false;
            opts.Password.RequireUppercase = false;
            opts.Password.RequireLowercase = false;
        })
        .AddEntityFrameworkStores<ApplicationDbContext>()
        .AddDefaultTokenProviders();

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddSingleton<ISessionConnectionTerminator, TestSessionConnectionTerminator>();
        services.AddSingleton<SessionCache>();
        services.AddSingleton<ISessionCache>(sp => sp.GetRequiredService<SessionCache>());
        services.AddSingleton<IConnectionTracker, InMemoryConnectionTracker>();
        services.AddSingleton<IOnlinePresenceTracker>(sp => (InMemoryConnectionTracker)sp.GetRequiredService<IConnectionTracker>());

        // Infrastructure repositories / services
        services.AddScoped<IBlockPersistence, BlockPersistence>();
        services.AddScoped<IUserReader, UserReader>();
        services.AddScoped<IUserWriter, UserWriter>();
        services.AddScoped<IConversationPersistence, ConversationPersistence>();
        services.AddScoped<IUserPresencePersistence, UserPresencePersistence>();
        services.AddScoped<IMessagePersistence, MessagePersistence>();
        services.AddScoped<IReactionPersistence, ReactionPersistence>();
        services.AddScoped<IMediaPersistence, MediaPersistence>();
        services.AddDataProtection();
        services.Configure<MediaOptions>(_ => { });
        services.Configure<R2StorageOptions>(_ => { });
        services.AddSingleton<LocalMediaSigningService>();
        services.AddSingleton(sp => new LocalDiskMediaStorage(sp.GetRequiredService<LocalMediaSigningService>(), MediaRootDirectory));
        services.AddSingleton<IMediaStorage>(sp => sp.GetRequiredService<LocalDiskMediaStorage>());
        services.AddSingleton<IMediaVerifier, MediaVerifier>();
        services.AddSingleton<MediaCleanupService>();
        services.TryAddSingleton<IRealtimeNotifier, TestNoOpRealtimeNotifier>();
        services.AddScoped<ISessionService, SessionService>();
        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IEmailSender, DevelopmentEmailSender>();

        // Application use case services
        services.AddApplicationServices();

        return services;
    }

    public IServiceProvider CreateServiceProvider()
    {
        return CreateServiceCollection().BuildServiceProvider();
    }

    public IServiceScope CreateScope()
    {
        return CreateServiceProvider().CreateScope();
    }
}

public sealed class TestSessionConnectionTerminator : ISessionConnectionTerminator
{
    public Task DisconnectSessionAsync(Guid sessionId, CancellationToken ct = default) => Task.CompletedTask;
    public Task DisconnectUserSessionsAsync(UserId userId, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class TestNoOpRealtimeNotifier : IRealtimeNotifier
{
    public Task NotifyNewMessageAsync(ConversationId conversationId, Application.DTOs.MessageDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyReceiptUpdatedAsync(UserId recipientUserId, Application.DTOs.ReceiptUpdateDto receipt, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyTypingAsync(UserId recipientUserId, Application.DTOs.TypingIndicatorDto typing, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyPresenceChangedAsync(IReadOnlyList<UserId> recipientUserIds, Application.DTOs.UserPresenceDto presence, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyMessageEditedAsync(ConversationId conversationId, Application.DTOs.MessageEditedDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyMessageDeletedForEveryoneAsync(ConversationId conversationId, Application.DTOs.MessageDeletedForEveryoneDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyMessageDeletedForMeAsync(UserId callerUserId, Application.DTOs.MessageDeletedForMeDto message, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyReactionUpdatedAsync(ConversationId conversationId, Application.DTOs.ReactionUpdatedDto reaction, CancellationToken ct = default) => Task.CompletedTask;
    public Task NotifyUserProfileUpdatedAsync(UserId userId, Application.DTOs.UserProfileUpdatedDto profile, CancellationToken ct = default) => Task.CompletedTask;
}
