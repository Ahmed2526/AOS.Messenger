using AOS.Messenger.Application.Common;
using AOS.Messenger.Domain.Constants;
using ImageMagick;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Infrastructure.Storage;

/// <summary>
/// Shared bounded concurrency gate and ImageMagick resource-limit configurator shared across
/// chat media, profile avatar, and sticker Magick.NET pipelines.
/// </summary>
public sealed class ImageProcessingGate : IDisposable
{
    private static readonly Lock ResourceLimitsLock = new();

    public SemaphoreSlim Semaphore { get; }
    public TimeSpan Timeout { get; }

    static ImageProcessingGate()
    {
        lock (ResourceLimitsLock)
        {
            ResourceLimits.Width = (ulong)MediaConstraints.MaxImageWidth;
            ResourceLimits.Height = (ulong)MediaConstraints.MaxImageHeight;
            ResourceLimits.Area = 256UL * 1024 * 1024;
            ResourceLimits.Memory = 512UL * 1024 * 1024;
            ResourceLimits.Disk = 1024UL * 1024 * 1024;
            ResourceLimits.MaxMemoryRequest = 256UL * 1024 * 1024;
            ResourceLimits.Thread = 1UL;
            ResourceLimits.Time = 15UL;
        }
    }

    public ImageProcessingGate(IOptions<MediaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var opts = options.Value;
        var concurrency = Math.Max(1, opts.MaxImageProcessingConcurrency);
        Semaphore = new SemaphoreSlim(concurrency, concurrency);
        var timeoutSeconds = opts.ImageProcessingTimeoutSeconds is >= 1 and <= 60
            ? opts.ImageProcessingTimeoutSeconds
            : 15;
        Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        lock (ResourceLimitsLock)
        {
            ResourceLimits.Time = (ulong)timeoutSeconds;
        }
    }

    public void Dispose()
    {
        Semaphore.Dispose();
    }
}
