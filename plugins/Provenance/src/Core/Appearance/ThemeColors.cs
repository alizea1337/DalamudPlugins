using System;
using System.Globalization;

namespace Provenance.Core.Appearance;

/// <summary>
/// The window's colours as 0xRRGGBB: the four a player picks (Settings, Interface) and the shades worked out from
/// them. The shades keep the steps the first colours had, so those come out exactly as they always were.
/// </summary>
public sealed record ThemeColors(
    uint Accent, uint AccentHover, uint OnAccent,
    uint Window, uint Surface, uint Raised, uint RaisedHover, uint Scrim,
    uint Text, uint TextMuted, uint TextFaint)
{
    public const uint DefaultAccent = 0x86D0C2;
    public const uint DefaultBackground = 0x171A21;
    public const uint DefaultCards = 0x1F232C;
    public const uint DefaultText = 0xE7EAF0;

    /// <summary>Contrast the window's text needs to read well (WCAG AA for body text).</summary>
    public const double ReadableContrast = 4.5;
    /// <summary>Contrast an accent needs to stand out as icons and highlights (WCAG AA for large text and shapes).</summary>
    public const double VisibleContrast = 3.0;

    public static ThemeColors Default { get; } = From(DefaultAccent, DefaultBackground, DefaultCards, DefaultText);

    /// <param name="background">Behind the cards.</param>
    /// <param name="cards">Cards, the sidebar and the bottom bar.</param>
    public static ThemeColors From(uint accent, uint background, uint cards, uint text)
    {
        accent &= 0xFFFFFF;
        background &= 0xFFFFFF;
        cards &= 0xFFFFFF;
        text &= 0xFFFFFF;
        // Fields and their hover sit a step from the cards toward the text; muted and faint text a step from the
        // text toward the cards. On light cards with dark text both go the other way.
        var toText = Luminance(text) >= Luminance(cards) ? 1 : -1;
        var accentLight = Luminance(accent);
        return new ThemeColors(
            accent,
            // A bright accent can't get brighter on hover, so it darkens instead.
            Step(accent, 0x160C0E, accentLight > 0.75 ? -1 : 1),
            OnAccentFor(accent),
            background,
            cards,
            Step(cards, 0x090A0C, toText),
            Step(cards, 0x121418, toText),
            Mix(background, 0x000000, 0.55),
            text,
            Step(text, 0x4D483F, -toText),
            Step(text, 0x7C776D, -toText));
    }

    /// <summary>Text drawn on the accent (primary buttons, a ticked box): a deep shade of it, or a pale one on a dark accent.</summary>
    public static uint OnAccentFor(uint accent)
    {
        var dark = Mix(accent, 0x000000, 0.86);
        var light = Mix(accent, 0xFFFFFF, 0.9);
        return Contrast(dark, accent) >= Contrast(light, accent) ? dark : light;
    }

    /// <summary>"#86D0C2".</summary>
    public static string ToHex(uint rgb) => "#" + (rgb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);

    /// <summary>Reads a hex colour: six digits or the three-digit short form, with or without "#", any case, spaces around.</summary>
    public static bool TryParseHex(string? text, out uint rgb)
    {
        rgb = 0;
        var t = (text ?? "").Trim();
        if (t.StartsWith('#')) t = t[1..];
        if (t.Length == 3) t = string.Concat(t[0], t[0], t[1], t[1], t[2], t[2]);
        if (t.Length != 6) return false;
        return uint.TryParse(t, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out rgb);
    }

    /// <summary>Relative luminance (WCAG), 0 for black to 1 for white.</summary>
    public static double Luminance(uint rgb)
    {
        static double Channel(uint c)
        {
            var v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel((rgb >> 16) & 0xFF) + 0.7152 * Channel((rgb >> 8) & 0xFF) + 0.0722 * Channel(rgb & 0xFF);
    }

    /// <summary>Contrast ratio (WCAG) between two colours, from 1 (the same) to 21 (black on white).</summary>
    public static double Contrast(uint a, uint b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>Each channel moved by the delta's channel, up (1) or down (-1), kept within 0 to 255.</summary>
    public static uint Step(uint rgb, uint delta, int direction)
    {
        uint Channel(int shift)
        {
            var v = (int)((rgb >> shift) & 0xFF) + direction * (int)((delta >> shift) & 0xFF);
            return (uint)Math.Clamp(v, 0, 255) << shift;
        }
        return Channel(16) | Channel(8) | Channel(0);
    }

    /// <summary>The colour <paramref name="t"/> of the way from <paramref name="a"/> to <paramref name="b"/>.</summary>
    public static uint Mix(uint a, uint b, double t)
    {
        uint Channel(int shift)
        {
            var x = (int)((a >> shift) & 0xFF);
            var y = (int)((b >> shift) & 0xFF);
            return (uint)Math.Clamp((int)Math.Round(x + (y - x) * t, MidpointRounding.AwayFromZero), 0, 255) << shift;
        }
        return Channel(16) | Channel(8) | Channel(0);
    }
}
