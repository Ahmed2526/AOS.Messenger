using System.Globalization;
using System.Text;

namespace AOS.Messenger.Web.Helpers;

public enum EmojiDisplayTier
{
    Normal = 0,
    Single = 1,
    Few = 2
}

/// <summary>
/// Presentation-layer classifier for WhatsApp-style tiered emoji-only message sizing.
/// Keeps SSR Razor markup strictly in sync with client-side rendering in emoji-utils.js.
/// </summary>
public static class EmojiPresentationHelper
{
    private const string NormalClasses = "message-text-content text-xs leading-relaxed whitespace-pre-wrap break-words";
    private const string SingleClasses = "message-text-content emoji-jumbo-single text-4xl leading-tight whitespace-pre-wrap break-words";
    private const string FewClasses = "message-text-content emoji-jumbo-few text-3xl leading-snug whitespace-pre-wrap break-words";

    public static EmojiDisplayTier ClassifyMessage(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return EmojiDisplayTier.Normal;
        }

        var emojiCount = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(content);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (string.IsNullOrWhiteSpace(element))
            {
                continue;
            }

            if (!IsEmojiGrapheme(element.AsSpan()))
            {
                return EmojiDisplayTier.Normal;
            }

            emojiCount++;
            if (emojiCount > 3)
            {
                return EmojiDisplayTier.Normal;
            }
        }

        return emojiCount switch
        {
            1 => EmojiDisplayTier.Single,
            2 or 3 => EmojiDisplayTier.Few,
            _ => EmojiDisplayTier.Normal
        };
    }

    public static string GetMessageContentCssClasses(string? content)
    {
        return ClassifyMessage(content) switch
        {
            EmojiDisplayTier.Single => SingleClasses,
            EmojiDisplayTier.Few => FewClasses,
            _ => NormalClasses
        };
    }

    public static bool IsEmojiGrapheme(ReadOnlySpan<char> grapheme)
    {
        if (grapheme.IsEmpty)
        {
            return false;
        }

        Span<int> runes = stackalloc int[16];
        var count = 0;

        foreach (var rune in grapheme.EnumerateRunes())
        {
            if (count >= runes.Length)
            {
                return false;
            }

            runes[count++] = rune.Value;
        }

        if (count == 0)
        {
            return false;
        }

        // 1. Keycap sequence: [#*0-9] + optional VS16 (U+FE0F) + Combining Enclosing Keycap (U+20E3)
        if (IsKeycapBase(runes[0]))
        {
            if (count == 2 && runes[1] == 0x20E3)
            {
                return true;
            }

            if (count == 3 && runes[1] == 0xFE0F && runes[2] == 0x20E3)
            {
                return true;
            }

            return false;
        }

        // 2. Regional indicator flag sequence (2 regional indicators)
        if (count == 2 && IsRegionalIndicator(runes[0]) && IsRegionalIndicator(runes[1]))
        {
            return true;
        }

        // 3. Subdivision flag tag sequence: U+1F3F4 + [U+E0020..U+E007E]+ + U+E007F
        if (count >= 3 && runes[0] == 0x1F3F4 && runes[count - 1] == 0xE007F)
        {
            for (var i = 1; i < count - 1; i++)
            {
                if (runes[i] is < 0xE0020 or > 0xE007E)
                {
                    return false;
                }
            }

            return true;
        }

        // 4. Reject single bare text-presentation symbols without VS16 (©, ®, ™, arrows, etc.)
        if (count == 1 && IsTextOnlyPictographic(runes[0]))
        {
            return false;
        }

        // 5. Pictographic emoji with optional VS16 / Fitzpatrick skin-tone modifier, optionally ZWJ-joined
        var idx = 0;
        while (idx < count)
        {
            if (!IsExtendedPictographic(runes[idx]))
            {
                return false;
            }

            idx++;

            if (idx < count && (runes[idx] == 0xFE0F || IsSkinToneModifier(runes[idx])))
            {
                idx++;
            }

            if (idx == count)
            {
                return true;
            }

            if (runes[idx] != 0x200D)
            {
                return false;
            }

            idx++;
            if (idx == count)
            {
                // Trailing ZWJ with no following pictograph
                return false;
            }
        }

        return false;
    }

    private static bool IsKeycapBase(int cp) => cp is '#' or '*' or (>= '0' and <= '9');

    private static bool IsRegionalIndicator(int cp) => cp is >= 0x1F1E6 and <= 0x1F1FF;

    private static bool IsSkinToneModifier(int cp) => cp is >= 0x1F3FB and <= 0x1F3FF;

    private static bool IsTextOnlyPictographic(int cp) => cp is
        0x00A9 or 0x00AE or 0x2122 or 0x203C or 0x2049 or 0x2139 or
        (>= 0x2194 and <= 0x2199) or (>= 0x21A9 and <= 0x21AA);

    private static bool IsExtendedPictographic(int cp) => cp is
        0xA9 or 0xAE or 0x203C or 0x2049 or 0x2122 or 0x2139 or
        (>= 0x2194 and <= 0x2199) or (>= 0x21A9 and <= 0x21AA) or
        (>= 0x231A and <= 0x231B) or 0x2328 or 0x23CF or
        (>= 0x23E9 and <= 0x23F3) or (>= 0x23F8 and <= 0x23FA) or
        0x24C2 or (>= 0x25AA and <= 0x25AB) or 0x25B6 or 0x25C0 or
        (>= 0x25FB and <= 0x25FE) or (>= 0x2600 and <= 0x2604) or
        0x260E or 0x2611 or (>= 0x2614 and <= 0x2615) or 0x2618 or 0x261D or 0x2620 or
        (>= 0x2622 and <= 0x2623) or 0x2626 or 0x262A or
        (>= 0x262E and <= 0x262F) or (>= 0x2638 and <= 0x263A) or
        0x2640 or 0x2642 or (>= 0x2648 and <= 0x2653) or
        (>= 0x265F and <= 0x2660) or 0x2663 or (>= 0x2665 and <= 0x2666) or 0x2668 or 0x267B or
        (>= 0x267E and <= 0x267F) or (>= 0x2692 and <= 0x2697) or 0x2699 or
        (>= 0x269B and <= 0x269C) or (>= 0x26A0 and <= 0x26A1) or 0x26A7 or
        (>= 0x26AA and <= 0x26AB) or (>= 0x26B0 and <= 0x26B1) or
        (>= 0x26BD and <= 0x26BE) or (>= 0x26C4 and <= 0x26C5) or 0x26C8 or
        (>= 0x26CE and <= 0x26CF) or 0x26D1 or (>= 0x26D3 and <= 0x26D4) or
        (>= 0x26E9 and <= 0x26EA) or (>= 0x26F0 and <= 0x26F5) or
        (>= 0x26F7 and <= 0x26FA) or 0x26FD or 0x2702 or 0x2705 or
        (>= 0x2708 and <= 0x270D) or 0x270F or 0x2712 or 0x2714 or 0x2716 or 0x271D or 0x2721 or 0x2728 or
        (>= 0x2733 and <= 0x2734) or 0x2744 or 0x2747 or 0x274C or 0x274E or
        (>= 0x2753 and <= 0x2755) or 0x2757 or (>= 0x2763 and <= 0x2764) or
        (>= 0x2795 and <= 0x2797) or 0x27A1 or 0x27B0 or 0x27BF or
        (>= 0x2934 and <= 0x2935) or (>= 0x2B05 and <= 0x2B07) or
        (>= 0x2B1B and <= 0x2B1C) or 0x2B50 or 0x2B55 or
        0x3030 or 0x303D or 0x3297 or 0x3299 or
        0x1F004 or (>= 0x1F02C and <= 0x1F02F) or (>= 0x1F094 and <= 0x1F09F) or
        (>= 0x1F0AF and <= 0x1F0B0) or 0x1F0C0 or (>= 0x1F0CF and <= 0x1F0D0) or
        (>= 0x1F0F6 and <= 0x1F0FF) or (>= 0x1F170 and <= 0x1F171) or
        (>= 0x1F17E and <= 0x1F17F) or 0x1F18E or (>= 0x1F191 and <= 0x1F19A) or
        (>= 0x1F1AE and <= 0x1F1E5) or (>= 0x1F201 and <= 0x1F20F) or
        0x1F21A or 0x1F22F or (>= 0x1F232 and <= 0x1F23A) or
        (>= 0x1F23C and <= 0x1F23F) or (>= 0x1F249 and <= 0x1F25F) or
        (>= 0x1F266 and <= 0x1F321) or (>= 0x1F324 and <= 0x1F393) or
        (>= 0x1F396 and <= 0x1F397) or (>= 0x1F399 and <= 0x1F39B) or
        (>= 0x1F39E and <= 0x1F3F0) or (>= 0x1F3F3 and <= 0x1F3F5) or
        (>= 0x1F3F7 and <= 0x1F3FA) or (>= 0x1F400 and <= 0x1F4FD) or
        (>= 0x1F4FF and <= 0x1F53D) or (>= 0x1F549 and <= 0x1F54E) or
        (>= 0x1F550 and <= 0x1F567) or (>= 0x1F56F and <= 0x1F570) or
        (>= 0x1F573 and <= 0x1F57A) or 0x1F587 or (>= 0x1F58A and <= 0x1F58D) or
        0x1F590 or (>= 0x1F595 and <= 0x1F596) or (>= 0x1F5A4 and <= 0x1F5A5) or
        0x1F5A8 or (>= 0x1F5B1 and <= 0x1F5B2) or 0x1F5BC or
        (>= 0x1F5C2 and <= 0x1F5C4) or (>= 0x1F5D1 and <= 0x1F5D3) or
        (>= 0x1F5DC and <= 0x1F5DE) or 0x1F5E1 or 0x1F5E3 or 0x1F5E8 or 0x1F5EF or 0x1F5F3 or
        (>= 0x1F5FA and <= 0x1F64F) or (>= 0x1F680 and <= 0x1F6C5) or
        (>= 0x1F6CB and <= 0x1F6D2) or (>= 0x1F6D5 and <= 0x1F6E5) or
        0x1F6E9 or (>= 0x1F6EB and <= 0x1F6F0) or (>= 0x1F6F3 and <= 0x1F6FF) or
        (>= 0x1F7DA and <= 0x1F7FF) or (>= 0x1F80C and <= 0x1F80F) or
        (>= 0x1F848 and <= 0x1F84F) or (>= 0x1F85A and <= 0x1F85F) or
        (>= 0x1F888 and <= 0x1F88F) or (>= 0x1F8AE and <= 0x1F8AF) or
        (>= 0x1F8BC and <= 0x1F8BF) or (>= 0x1F8C2 and <= 0x1F8CF) or
        (>= 0x1F8D9 and <= 0x1F8FF) or (>= 0x1F90C and <= 0x1F93A) or
        (>= 0x1F93C and <= 0x1F945) or (>= 0x1F947 and <= 0x1F9FF) or
        (>= 0x1FA58 and <= 0x1FA5F) or (>= 0x1FA6E and <= 0x1FAFF) or
        (>= 0x1FC00 and <= 0x1FFFD);
}
