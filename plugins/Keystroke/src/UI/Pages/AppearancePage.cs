using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class AppearancePage : Page
{
    private static readonly (Accent accent, string name, Vector4 color)[] Accents =
    {
        (Accent.Lime, "lime", Theme.Theme.Rgb(0xA8, 0xE6, 0x3D)),
        (Accent.Cyan, "cyan", Theme.Theme.Rgb(0x3D, 0xDC, 0xF5)),
        (Accent.Magenta, "magenta", Theme.Theme.Rgb(0xE0, 0x4C, 0xE6)),
        (Accent.Gold, "gold", Theme.Theme.Rgb(0xF5, 0xD1, 0x3D)),
        (Accent.Crimson, "crimson", Theme.Theme.Rgb(0xF0, 0x4E, 0x5A)),
        (Accent.Ice, "ice", Theme.Theme.Rgb(0xB8, 0xE8, 0xFF)),
    };

    public AppearancePage(Plugin plugin) : base(plugin) { }

    public override string Title => "look";
    public override char Icon => Theme.Icon.Sparkles;

    public override void Draw()
    {
        var a = Config.Appearance;
        var changed = false;
        var s = Widgets.S;

        Widgets.BeginColumns(2);

        Widgets.BeginGroup("accent");
        var dl = ImGui.GetWindowDrawList();
        var swatch = 22 * s;
        var pos = ImGui.GetCursorScreenPos();
        for (var i = 0; i < Accents.Length; i++)
        {
            var (acc, name, col) = Accents[i];
            var p = pos + new Vector2(i * (swatch + 8 * s), 0);
            ImGui.SetCursorScreenPos(p);
            if (ImGui.InvisibleButton("##acc" + i, new Vector2(swatch))) { a.Accent = acc; changed = true; }
            var hovered = ImGui.IsItemHovered();
            var sel = a.Accent == acc;
            Theme.Theme.DrawRaised(dl, p, p + new Vector2(swatch), s, Theme.Theme.Scale(col, sel ? 1f : 0.55f), sel);
            if (sel && Theme.Theme.Effects) Theme.Theme.DrawGlow(dl, p, p + new Vector2(swatch), col, 6 * s, 0.8f);
            if (hovered) Widgets.Tooltip(name);
        }
        ImGui.SetCursorScreenPos(pos + new Vector2(0, swatch + 6 * s));
        Widgets.Small(Accents[Array.FindIndex(Accents, x => x.accent == a.Accent)].name);
        Widgets.EndGroup();

        Widgets.BeginGroup("window");
        var w = Config.Window;
        var v = w.CompactInDuty;
        if (Widgets.Check("compact view inside the duty", ref v, "Switches to the compact strip when the duty starts and restores your layout when it ends.")) { w.CompactInDuty = v; changed = true; }
        v = a.Keycaps;
        if (Widgets.Check("show the word as keycaps", ref v, "On the dashboard, the compact strip and the overlay.")) { a.Keycaps = v; changed = true; }
        Widgets.EndGroup();

        Widgets.Column(1);

        Widgets.BeginGroup("effects");
        v = a.Effects;
        if (Widgets.Check("effects", ref v, "Glows, keycap presses, hover fades.")) { a.Effects = v; changed = true; }
        if (a.Effects)
        {
            v = a.AnimatedStripe;
            if (Widgets.Check("drifting stripe", ref v)) { a.AnimatedStripe = v; changed = true; }
            var st = (int)a.Stripe;
            Widgets.Row("stripe", 150 * s, () => { if (Widgets.Combo("##stripe", ref st, new[] { "rgb", "signal (cyan / accent / gold)" }, 150 * s)) { a.Stripe = (StripeStyle)st; changed = true; } });
            v = a.ReduceMotion;
            if (Widgets.Check("reduce motion", ref v, "Keeps state changes instant; no drifting or breathing.")) { a.ReduceMotion = v; changed = true; }
        }
        Widgets.EndGroup();

        Widgets.EndColumns();

        if (changed)
        {
            Theme.Theme.Configure(a);
            Save();
        }
    }
}
