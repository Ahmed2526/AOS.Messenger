using System.Collections.Frozen;

namespace AOS.Messenger.Domain.Constants;

/// <summary>
/// Authoritative server-side allowlist for message reactions.
/// Only the predefined set of 8 standard emojis are accepted.
/// </summary>
public static class ReactionAllowlist
{
    public const string ThumbsUp = "\uD83D\uDC4D";   // 👍
    public const string Heart = "\u2764\uFE0F";      // ❤️
    public const string HeartAlt = "\u2764";         // ❤
    public const string Joy = "\uD83D\uDE02";        // 😂
    public const string OpenMouth = "\uD83D\uDE2E";  // 😮
    public const string Cry = "\uD83D\uDE22";        // 😢
    public const string Pray = "\uD83D\uDE4F";       // 🙏
    public const string Fire = "\uD83D\uDD25";       // 🔥
    public const string Party = "\uD83C\uDF89";      // 🎉

    public static readonly IReadOnlyList<string> AllowedList =
    [
        ThumbsUp,
        Heart,
        Joy,
        OpenMouth,
        Cry,
        Pray,
        Fire,
        Party
    ];

    private static readonly FrozenSet<string> AllowedSet = new[]
    {
        ThumbsUp,
        Heart,
        HeartAlt,
        Joy,
        OpenMouth,
        Cry,
        Pray,
        Fire,
        Party
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Validates whether the given emoji string belongs to the strict server allowlist.
    /// </summary>
    public static bool IsAllowed(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji))
        {
            return false;
        }

        var trimmed = emoji.Trim();
        return AllowedSet.Contains(trimmed);
    }

    /// <summary>
    /// Normalizes the emoji to its standard representation (e.g. standardizing heart with variation selector).
    /// </summary>
    public static string Normalize(string emoji)
    {
        var trimmed = emoji.Trim();
        if (trimmed == HeartAlt)
        {
            return Heart;
        }

        return trimmed;
    }
}
