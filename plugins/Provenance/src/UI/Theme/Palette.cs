using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Provenance.Core.Catalog;
using Provenance.Core.Planning;

namespace Provenance.UI.Theme;

/// <summary>
/// Colour tokens. One cool slate grey family, one accent (sea glass) spent on primary actions, the active
/// tab and progress. Crafters take the colour of their material; sources have their own pastel set.
/// </summary>
internal static class Palette
{
    public static readonly Vector4 Window = Hex(0x171A21);
    public static readonly Vector4 Surface = Hex(0x1F232C);
    public static readonly Vector4 Raised = Hex(0x282D38);
    public static readonly Vector4 RaisedHover = Hex(0x313744);
    public static readonly Vector4 Line = Hex(0x313744);

    public static readonly Vector4 Text = Hex(0xE7EAF0);
    public static readonly Vector4 TextMuted = Hex(0x9AA2B1);
    public static readonly Vector4 TextFaint = Hex(0x6B7383);

    public static readonly Vector4 Accent = Hex(0x86D0C2);
    public static readonly Vector4 AccentHover = Hex(0x9CDCD0);
    /// <summary>Text drawn on top of the accent.</summary>
    public static readonly Vector4 OnAccent = Hex(0x10241F);

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
        _ => TextMuted,
    };

    public static Vector4 SupplyColor(Supply supply) => supply switch
    {
        Supply.Gather => Hex(0x9FD39A),
        Supply.GatherTimed => Hex(0xB7A6F0),
        Supply.Fish => Hex(0x93C5F0),
        Supply.Vendor => Hex(0xE8CF8F),
        Supply.Drop => Hex(0xE9A3B8),
        Supply.Manual => Danger,
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
        Supply.Manual => "get it yourself",
        _ => supply.ToString(),
    };

    public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    public static Vector4 Mix(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, Math.Clamp(t, 0f, 1f));

    public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    private static Vector4 Hex(uint rgb) => new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);
}
