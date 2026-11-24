using AOS.Messenger.Application.Common;
using AOS.Messenger.Domain.Constants;
using Microsoft.Extensions.Options;

namespace AOS.Messenger.Infrastructure.Storage;

/// <summary>
/// Validates <see cref="MediaOptions"/> at startup so misconfigured quotas, TTLs, concurrency caps,
/// or retention windows fail fast before serving requests.
/// </summary>
public sealed class MediaOptionsValidator : IValidateOptions<MediaOptions>
{
    public ValidateOptionsResult Validate(string? name, MediaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<string>();

        if (options.MaxConcurrentPendingPerUser is < 1 or > 10)
        {
            errors.Add("Media:MaxConcurrentPendingPerUser must be between 1 and 10.");
        }

        if (options.DailyUploadQuotaBytes < 1024L * 1024L)
        {
            errors.Add("Media:DailyUploadQuotaBytes must be at least 1,048,576 bytes (1 MB).");
        }

        if (options.PresignedPutTtlMinutes is < 1 or > 5)
        {
            errors.Add("Media:PresignedPutTtlMinutes must be between 1 and 5.");
        }

        if (options.PresignedGetTtlMinutes is < 1 or > 15)
        {
            errors.Add("Media:PresignedGetTtlMinutes must be between 1 and 15.");
        }

        if (options.MaxImageProcessingConcurrency is < 1 or > 4)
        {
            errors.Add("Media:MaxImageProcessingConcurrency must be between 1 and 4.");
        }

        if (options.ImageProcessingTimeoutSeconds is < 1 or > 60)
        {
            errors.Add("Media:ImageProcessingTimeoutSeconds must be between 1 and 60.");
        }

        if (options.MaxAnimationFrames is < 1 or > 1000)
        {
            errors.Add("Media:MaxAnimationFrames must be between 1 and 1000.");
        }

        if (options.MaxTotalDecodedImagePixels < MediaConstraints.MaxImagePixels)
        {
            errors.Add($"Media:MaxTotalDecodedImagePixels must be at least {MediaConstraints.MaxImagePixels}.");
        }

        if (options.StalePendingMinutes < 1)
        {
            errors.Add("Media:StalePendingMinutes must be at least 1.");
        }

        if (options.UnattachedUploadedHours < 1)
        {
            errors.Add("Media:UnattachedUploadedHours must be at least 1.");
        }

        if (options.OrphanGracePeriodHours < 1)
        {
            errors.Add("Media:OrphanGracePeriodHours must be at least 1.");
        }

        if (options.PurgedMetadataRetentionHours < 24)
        {
            errors.Add("Media:PurgedMetadataRetentionHours must be at least 24 to preserve 24-hour daily quota accounting.");
        }

        if (options.CleanupIntervalMinutes < 1)
        {
            errors.Add("Media:CleanupIntervalMinutes must be at least 1.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
