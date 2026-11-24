namespace AOS.Messenger.Domain.Enums;

/// <summary>
/// Categorizes a media attachment by content family and enforced processing pipeline.
/// </summary>
public enum MediaKind
{
    Image = 0,
    Video = 1,
    Document = 2,
    Voice = 3
}

public static class MediaKindExtensions
{
    public static string ToApiString(this MediaKind kind) => kind switch
    {
        MediaKind.Image => "image",
        MediaKind.Video => "video",
        MediaKind.Document => "document",
        MediaKind.Voice => "voice",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported media kind.")
    };
}
