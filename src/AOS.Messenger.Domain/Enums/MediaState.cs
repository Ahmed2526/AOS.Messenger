namespace AOS.Messenger.Domain.Enums;

/// <summary>
/// Represents the lifecycle state of a media object across R2 staging, verification, attachment, and cleanup.
/// </summary>
public enum MediaState
{
    Pending = 0,
    Uploaded = 1,
    Attached = 2,
    Failed = 3,
    Deleted = 4
}
