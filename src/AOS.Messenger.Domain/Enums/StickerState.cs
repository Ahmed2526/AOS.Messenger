namespace AOS.Messenger.Domain.Enums;

/// <summary>
/// Lifecycle state for a reusable system or custom R2 sticker asset.
/// </summary>
public enum StickerState
{
    Pending = 0,
    Ready = 1,
    Failed = 2,
    Archived = 3
}
