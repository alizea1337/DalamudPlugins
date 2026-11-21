using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Provenance.Core.Appearance;
using Provenance.Core.Catalog;
using Provenance.Core.Planning;

namespace Provenance.UI.Theme;

/// <summary>
/// Colour tokens. One cool slate grey family, one accent (sea glass) spent on primary actions, the active
/// tab and progress. Crafters take the colour of their material; sources have their own pastel set.
/// Accent, background, cards and text can be picked (Settings, Interface); the other greys follow them
/// (<see cref="ThemeColors"/>).
/// </summary>
internal static class Palette
{
    public static Vector4 Window { get; private set; }
    public static Vector4 Surface { get; private set; }
    public static Vector4 Raised { get; private set; }
    public static Vector4 RaisedHover { get; private set; }
    public static Vector4 Line => RaisedHover;
    /// <summary>Darkens the window behind a dialog.</summary>
    public static Vector4 Scrim { get; private set; }

    public static Vector4 Text { get; private set; }
    public static Vector4 TextMuted { get; private set; }
    public static Vector4 TextFaint { get; private set; }

    public static Vector4 Accent { get; private set; }
    public static Vector4 AccentHover { get; private set; }
    /// <summary>Text drawn on top of the accent.</summary>
    public static Vector4 OnAccent { get; private set; }

    private static (uint, uint, uint, uint)? applied;

    static Palette() => Apply(ThemeColors.DefaultAccent, ThemeColors.DefaultBackground, ThemeColors.DefaultCards, ThemeColors.DefaultText);

    /// <summary>The colours picked in the settings; set by the window each frame, worked out again only when they change.</summary>
    public static void Apply(ColorSettings colors) => Apply(colors.Accent, colors.Background, colors.Cards, colors.Text);

    private static void Apply(uint accent, uint background, uint cards, uint text)
    {
        var key = (accent, background, cards, text);
        if (applied == key) return;
        applied = key;
        var t = ThemeColors.From(accent, background, cards, text);
        Window = Hex(t.Window);
        Surface = Hex(t.Surface);
        Raised = Hex(t.Raised);
        RaisedHover = Hex(t.RaisedHover);
        Scrim = Hex(t.Scrim);
        Text = Hex(t.Text);
        TextMuted = Hex(t.TextMuted);
        TextFaint = Hex(t.TextFaint);
        Accent = Hex(t.Accent);
        AccentHover = Hex(t.AccentHover);
        OnAccent = Hex(t.OnAccent);
    }

    public static readonly Vector4 Danger = Hex(0xEF8F8F);
    public static readonly Vector4 Warn = Hex(0xE8C77D);

    public static Vector4 JobColor(Job job) => job switch
    {
        Job.Carpenter => Hex(0xD8B48A),     // wood
        Job.Blacksmith => Hex(0xE0A27C),    // forge
        Job.Armorer => Hex(0xA9B8C9),       // steel
        Job.Goldsmith => Hex(0xE6C86E),     // gold
        Job.Leatherworker => Hex(0xC99B7A), // hide
        Job.Weaver => Hex(0xE3A9C8),        // cloth
        Job.Alchemist => Hex(0xA7D49B),     // herb
        Job.Culinarian => Hex(0xF2B880),    // spice
        _ => TextMuted,
    };

    /// <summary>Colour for any crafter or gatherer by ClassJob row.</summary>
    public static Vector4 ClassJobColor(uint classJob) => classJob switch
    {
        >= 8 and <= 15 => JobColor((Job)(classJob - 8)),
        16 => Hex(0xC9BBA6), // stone
        17 => Hex(0x9FD39A), // leaf
        18 => Hex(0x93C5F0), // water
        _ => Core.Character.ClassJobs.RoleOf(classJob) switch
        {
            Core.Character.ClassJobs.Role.Tank => Hex(0x8FB4EA),   // soft blue
            Core.Character.ClassJobs.Role.Healer => Hex(0x9ED8A8), // soft green
            Core.Character.ClassJobs.Role.Dps => Hex(0xE9A0A0),    // soft red
            _ => TextMuted,
        },
    };

    public static Vector4 SupplyColor(Supply supply) => supply switch
    {
        Supply.Gather => Hex(0x9FD39A),
        Supply.GatherTimed => Hex(0xB7A6F0),
        Supply.Fish => Hex(0x93C5F0),
        Supply.Vendor => Hex(0xE8CF8F),
        Supply.Drop => Hex(0xE9A3B8),
        Supply.Reduce => Hex(0x8FD6CF),
        Supply.Exchange => Hex(0xEDA97E),
        Supply.Manual => Danger,
        Supply.Learn => Warn,
        _ => TextMuted,
    };

    public static string SupplyLabel(Supply supply) => supply switch
    {
        Supply.Craft => "craft",
        Supply.Gather => "gather",
        Supply.GatherTimed => "timed node",
        Supply.Fish => "fish",
        Supply.Vendor => "vendor",
        Supply.Drop => "monster drop",
        Supply.Reduce => "aetherial reduction",
        Supply.Exchange => "scrip exchange",
        Supply.Manual => "get it yourself",
        Supply.Learn => "learn first",
        _ => supply.ToString(),
    };

    public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    /// <summary>0xRRGGBB of a colour.</summary>
    public static uint Rgb(Vector4 c)
        => ((uint)MathF.Round(Math.Clamp(c.X, 0f, 1f) * 255) << 16) | ((uint)MathF.Round(Math.Clamp(c.Y, 0f, 1f) * 255) << 8) | (uint)MathF.Round(Math.Clamp(c.Z, 0f, 1f) * 255);

    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, Math.Clamp(t, 0f, 1f));

    /// <summary>Packed colour, faded with the style's alpha (a list fading in, a disabled block).</summary>
    public static uint U32(Vector4 c) => ImGui.GetColorU32(c);

    public static Vector4 Hex(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
