namespace AOS.Messenger.Application.Common;

/// <summary>
/// Configurable limits, presigned URL lifetimes, processing bounds, and cleanup windows for media attachments.
/// Per-message attachment count is fixed as a domain invariant in <see cref="Domain.Constants.MediaConstraints.MaxAttachmentsPerMessage"/>.
/// </summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    public int MaxConcurrentPendingPerUser { get; set; } = 10;
    public long DailyUploadQuotaBytes { get; set; } = 1_073_741_824L;
    public int PresignedPutTtlMinutes { get; set; } = 5;
    public int PresignedGetTtlMinutes { get; set; } = 10;
    public int MaxImageProcessingConcurrency { get; set; } = 3;
    public int ImageProcessingTimeoutSeconds { get; set; } = 15;
    public int MaxAnimationFrames { get; set; } = 300;
    public long MaxTotalDecodedImagePixels { get; set; } = 40_000_000L;
    public int StalePendingMinutes { get; set; } = 30;
    public int UnattachedUploadedHours { get; set; } = 2;
    public int OrphanGracePeriodHours { get; set; } = 1;
    public int PurgedMetadataRetentionHours { get; set; } = 48;
    public int CleanupIntervalMinutes { get; set; } = 15;
    public int PermanentOrphanSweepIntervalHours { get; set; } = 24;
    public int MaxOrphanSweepObjectsPerPrefixPerPass { get; set; } = 10_000;
}
