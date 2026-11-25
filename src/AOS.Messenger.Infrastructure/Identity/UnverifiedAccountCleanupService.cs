using AOS.Messenger.Application.Common.Interfaces;
using AOS.Messenger.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AOS.Messenger.Infrastructure.Identity;

public sealed class UnverifiedAccountCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMediaStorage? _mediaStorage;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UnverifiedAccountCleanupService> _logger;
    private readonly TimeSpan _period;

    [ActivatorUtilitiesConstructor]
    public UnverifiedAccountCleanupService(
        IServiceScopeFactory scopeFactory,
        IMediaStorage? mediaStorage = null,
        TimeProvider? timeProvider = null,
        ILogger<UnverifiedAccountCleanupService>? logger = null,
        TimeSpan? period = null)
    {
        _scopeFactory = scopeFactory;
        _mediaStorage = mediaStorage;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<UnverifiedAccountCleanupService>.Instance;
        _period = period ?? TimeSpan.FromHours(1);
    }

    public UnverifiedAccountCleanupService(
        IServiceScopeFactory scopeFactory,
        TimeProvider? timeProvider = null,
        ILogger<UnverifiedAccountCleanupService>? logger = null,
        TimeSpan? period = null)
        : this(scopeFactory, null, timeProvider, logger, period)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_period, _timeProvider);
        try
        {
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await PurgeExpiredUnverifiedAccountsAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred during unverified account cleanup.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful cancellation
        }
    }

    public async Task<int> PurgeExpiredUnverifiedAccountsAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var cutoff = _timeProvider.GetUtcNow().AddHours(-48);
        var totalPurged = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = await db.Users
                .Where(u => !u.EmailConfirmed && u.CreatedAtUtc < cutoff)
                .Take(500)
                .ToListAsync(ct);

            if (batch.Count == 0)
            {
                break;
            }

            var avatarKeysToDelete = batch
                .SelectMany(u => new[] { u.AvatarObjectKey, u.AvatarThumbnailObjectKey })
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            db.Users.RemoveRange(batch);
            await db.SaveChangesAsync(ct);

            if (_mediaStorage is not null && avatarKeysToDelete.Count > 0)
            {
                foreach (var key in avatarKeysToDelete)
                {
                    try
                    {
                        await _mediaStorage.DeleteObjectAsync(key, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                    {
                        _logger.LogWarning(ex, "Best-effort avatar storage delete failed during unverified account cleanup.");
                    }
                }
            }

            totalPurged += batch.Count;
        }

        if (totalPurged > 0)
        {
            _logger.LogInformation("Purged {Count} expired unverified accounts created before {Cutoff}.", totalPurged, cutoff);
        }

        return totalPurged;
    }
}
