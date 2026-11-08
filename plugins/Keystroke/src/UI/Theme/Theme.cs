using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Keystroke.UI.Theme;

/// <summary>Selectable accent colours. Values are the bright "on" colour; darker shades are derived.</summary>
public enum Accent { Lime = 0, Cyan = 1, Magenta = 2, Gold = 3, Crimson = 4, Ice = 5 }

/// <summary>
/// Keystroke's own look: a bevelled charcoal frame, a three-colour signal stripe, an icon rail, group
/// boxes with titles cut into their borders, sharp corners and one acid accent. Drawn by hand through
/// the draw list; ImGui's style is only pushed for the few stock controls we still use (inputs, combos).
/// </summary>
internal static class Theme
{
    // Surfaces
    public static readonly Vector4 Frame = Rgb(0x07, 0x07, 0x08);     // outermost ring + window edge
    public static readonly Vector4 BevelLight = Rgb(0x3A, 0x3A, 0x3E);
    public static readonly Vector4 BevelMid = Rgb(0x23, 0x23, 0x26);
    public static readonly Vector4 Surface = Rgb(0x12, 0x12, 0x14);   // window body
    public static readonly Vector4 Rail = Rgb(0x0E, 0x0E, 0x10);      // icon rail
    public static readonly Vector4 Panel = Rgb(0x17, 0x17, 0x19);     // group box body
    public static readonly Vector4 Inset = Rgb(0x0F, 0x0F, 0x11);     // input / track interiors
    public static readonly Vector4 InsetLine = Rgb(0x32, 0x32, 0x36);
    public static readonly Vector4 InsetShadow = Rgb(0x08, 0x08, 0x0A);

    // Text
    public static readonly Vector4 Text = Rgb(0xC9, 0xC9, 0xCF);
    public static readonly Vector4 TextDim = Rgb(0x8A, 0x8A, 0x92);
    public static readonly Vector4 TextFaint = Rgb(0x5C, 0x5C, 0x64);
    public static readonly Vector4 TextBright = Rgb(0xFF, 0xFF, 0xFF);
    public static readonly Vector4 TextShadow = new(0, 0, 0, 0.85f);

    // Semantic
    public static readonly Vector4 Warn = Rgb(0xF2, 0xB1, 0x34);
    public static readonly Vector4 Danger = Rgb(0xE2, 0x4A, 0x4A);
    public static readonly Vector4 Ok = Rgb(0x6F, 0xD6, 0x5A);
    public static readonly Vector4 Info = Rgb(0x3D, 0xDC, 0xF5);

    // Signal stripe: cyan -> accent -> gold
    public static readonly Vector4 StripeA = Rgb(0x3D, 0xDC, 0xF5);
    public static readonly Vector4 StripeC = Rgb(0xF5, 0xD1, 0x3D);

    // Accent (configured)
    public static Vector4 AccentColor { get; private set; } = Rgb(0xA8, 0xE6, 0x3D);
    public static Vector4 AccentDark { get; private set; } = Rgb(0x6E, 0x9A, 0x1E);
    public static Vector4 AccentDeep { get; private set; } = Rgb(0x3F, 0x5A, 0x12);
    public static Vector4 AccentGlow { get; private set; } = new(0.66f, 0.9f, 0.24f, 0.35f);
    public static Vector4 AccentText { get; private set; } = Rgb(0xC4, 0xF0, 0x6A);
    public static Accent CurrentAccent { get; private set; } = Accent.Lime;

    public static bool Effects { get; private set; } = true;
    public static bool ReduceMotion { get; private set; }
    public static bool AnimatedStripe { get; private set; } = true;

    // Metrics (unscaled)
    public const float RailWidth = 56f;
    public const float StripeHeight = 3f;
    public const float FrameInset = 6f;          // outer ring stack thickness
    public const float GroupPadding = 10f;
    public const float GroupGap = 10f;
    public const float RowHeight = 22f;
    public const float CheckboxSize = 10f;
    public const float ControlHeight = 18f;

    public static Vector4 Rgb(byte r, byte g, byte b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);
    public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);
    public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);
    public static Vector4 Lerp(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, Math.Clamp(t, 0f, 1f));
    public static Vector4 Scale(Vector4 c, float k) => new(Math.Clamp(c.X * k, 0, 1), Math.Clamp(c.Y * k, 0, 1), Math.Clamp(c.Z * k, 0, 1), c.W);

    /// <summary>Applies the Appearance settings (accent, effects).</summary>
    public static void Configure(AppearanceSettings a)
    {
        CurrentAccent = a.Accent;
        AccentColor = a.Accent switch
        {
            Accent.Cyan => Rgb(0x3D, 0xDC, 0xF5),
            Accent.Magenta => Rgb(0xE0, 0x4C, 0xE6),
            Accent.Gold => Rgb(0xF5, 0xD1, 0x3D),
            Accent.Crimson => Rgb(0xF0, 0x4E, 0x5A),
            Accent.Ice => Rgb(0xB8, 0xE8, 0xFF),
            _ => Rgb(0xA8, 0xE6, 0x3D),
        };
        AccentDark = Scale(AccentColor, 0.66f);
        AccentDeep = Scale(AccentColor, 0.38f);
        AccentGlow = WithAlpha(AccentColor, 0.35f);
        AccentText = Lerp(AccentColor, Vector4.One, 0.25f);
        Effects = a.Effects;
        ReduceMotion = a.ReduceMotion;
        AnimatedStripe = a.AnimatedStripe && a.Effects;
    }

    /// <summary>Pushes the ImGui style for the stock controls we still use. Dispose to pop.</summary>
    public static Scope Push()
    {
        var colors = 0;
        void C(ImGuiCol col, Vector4 v) { ImGui.PushStyleColor(col, v); colors++; }
        C(ImGuiCol.WindowBg, Surface);
        C(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.PopupBg, Panel);
        C(ImGuiCol.Border, Frame);
        C(ImGuiCol.BorderShadow, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.Text, Text);
        C(ImGuiCol.TextDisabled, TextFaint);
        C(ImGuiCol.FrameBg, Inset);
        C(ImGuiCol.FrameBgHovered, Rgb(0x14, 0x14, 0x17));
        C(ImGuiCol.FrameBgActive, Rgb(0x18, 0x18, 0x1B));
        C(ImGuiCol.TitleBg, Surface);
        C(ImGuiCol.TitleBgActive, Surface);
        C(ImGuiCol.TitleBgCollapsed, Surface);
        C(ImGuiCol.ScrollbarBg, Inset);
        C(ImGuiCol.ScrollbarGrab, Rgb(0x2D, 0x2D, 0x31));
        C(ImGuiCol.ScrollbarGrabHovered, Rgb(0x41, 0x41, 0x46));
        C(ImGuiCol.ScrollbarGrabActive, Rgb(0x52, 0x52, 0x58));
        C(ImGuiCol.CheckMark, AccentColor);
        C(ImGuiCol.SliderGrab, TextBright);
        C(ImGuiCol.SliderGrabActive, TextBright);
        C(ImGuiCol.Button, BevelMid);
        C(ImGuiCol.ButtonHovered, Rgb(0x2C, 0x2C, 0x30));
        C(ImGuiCol.ButtonActive, Rgb(0x1C, 0x1C, 0x1F));
        C(ImGuiCol.Header, WithAlpha(AccentColor, 0.18f));
        C(ImGuiCol.HeaderHovered, new Vector4(1, 1, 1, 0.05f));
        C(ImGuiCol.HeaderActive, new Vector4(1, 1, 1, 0.09f));
        C(ImGuiCol.Separator, BevelMid);
        C(ImGuiCol.SeparatorHovered, BevelMid);
        C(ImGuiCol.SeparatorActive, BevelMid);
        C(ImGuiCol.ResizeGrip, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.ResizeGripHovered, new Vector4(1, 1, 1, 0.12f));
        C(ImGuiCol.ResizeGripActive, new Vector4(1, 1, 1, 0.2f));
        C(ImGuiCol.Tab, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.TabHovered, BevelMid);
        C(ImGuiCol.TabActive, Panel);
        C(ImGuiCol.TableHeaderBg, Inset);
        C(ImGuiCol.TableBorderStrong, BevelMid);
        C(ImGuiCol.TableBorderLight, BevelMid);
        C(ImGuiCol.TableRowBg, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.TableRowBgAlt, new Vector4(1, 1, 1, 0.02f));
        C(ImGuiCol.TextSelectedBg, WithAlpha(AccentColor, 0.3f));
        C(ImGuiCol.NavHighlight, new Vector4(0, 0, 0, 0));

        var vars = 0;
        void V(ImGuiStyleVar var, float v) { ImGui.PushStyleVar(var, v); vars++; }
        void V2(ImGuiStyleVar var, Vector2 v) { ImGui.PushStyleVar(var, v); vars++; }
        V(ImGuiStyleVar.WindowRounding, 0f);
        V(ImGuiStyleVar.ChildRounding, 0f);
        V(ImGuiStyleVar.PopupRounding, 0f);
        V(ImGuiStyleVar.FrameRounding, 0f);
        V(ImGuiStyleVar.GrabRounding, 0f);
        V(ImGuiStyleVar.ScrollbarRounding, 0f);
        V(ImGuiStyleVar.TabRounding, 0f);
        V(ImGuiStyleVar.WindowBorderSize, 0f);
        V(ImGuiStyleVar.ChildBorderSize, 0f);
        V(ImGuiStyleVar.PopupBorderSize, 1f);
        V(ImGuiStyleVar.FrameBorderSize, 0f);
        V(ImGuiStyleVar.ScrollbarSize, 6f);
        V(ImGuiStyleVar.GrabMinSize, 6f);
        V2(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        V2(ImGuiStyleVar.FramePadding, new Vector2(6, 3));
        V2(ImGuiStyleVar.ItemSpacing, new Vector2(6, 4));
        V2(ImGuiStyleVar.ItemInnerSpacing, new Vector2(4, 4));
        V2(ImGuiStyleVar.CellPadding, new Vector2(6, 3));
        return new Scope(colors, vars);
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly int colors;
        private readonly int vars;
        public Scope(int colors, int vars) { this.colors = colors; this.vars = vars; }
        public void Dispose() { ImGui.PopStyleVar(vars); ImGui.PopStyleColor(colors); }
    }

    // ------------------------------------------------------------------ drawing helpers

    /// <summary>The window frame: black edge, light ring, wide mid ring, light ring, black. Fills the body.</summary>
    public static void DrawFrame(ImDrawListPtr dl, Vector2 min, Vector2 max, float s)
    {
        dl.AddRectFilled(min, max, U32(Frame));
        Ring(dl, min, max, 1 * s, 1 * s, BevelLight);
        Ring(dl, min, max, 2 * s, 3 * s, BevelMid);
        Ring(dl, min, max, 5 * s, 1 * s, BevelLight);
        dl.AddRectFilled(min + new Vector2(FrameInset * s), max - new Vector2(FrameInset * s), U32(Surface));
    }

    private static void Ring(ImDrawListPtr dl, Vector2 min, Vector2 max, float inset, float width, Vector4 color)
    {
        var a = min + new Vector2(inset);
        var b = max - new Vector2(inset);
        dl.AddRectFilled(a, new Vector2(b.X, a.Y + width), U32(color));
        dl.AddRectFilled(new Vector2(a.X, b.Y - width), b, U32(color));
        dl.AddRectFilled(a, new Vector2(a.X + width, b.Y), U32(color));
        dl.AddRectFilled(new Vector2(b.X - width, a.Y), b, U32(color));
    }

    /// <summary>Inset well for inputs, tracks and combos: shadow line outside, light line inside, dark fill.</summary>
    public static void DrawInset(ImDrawListPtr dl, Vector2 min, Vector2 max, float s, Vector4? fill = null)
    {
        dl.AddRectFilled(min, max, U32(InsetShadow));
        dl.AddRect(min + new Vector2(1 * s), max - new Vector2(1 * s), U32(InsetLine));
        dl.AddRectFilled(min + new Vector2(2 * s), max - new Vector2(2 * s), U32(fill ?? Inset));
    }

    /// <summary>Raised bevel for buttons and keycaps: light top/left edge, dark bottom/right edge.</summary>
    public static void DrawRaised(ImDrawListPtr dl, Vector2 min, Vector2 max, float s, Vector4 fill, bool pressed = false)
    {
        var hi = pressed ? InsetShadow : BevelLight;
        var lo = pressed ? BevelLight : InsetShadow;
        dl.AddRectFilled(min, max, U32(Frame));
        dl.AddRectFilled(min + new Vector2(1 * s), max - new Vector2(1 * s), U32(fill));
        dl.AddLine(min + new Vector2(1 * s, 1 * s), new Vector2(max.X - 1 * s, min.Y + 1 * s), U32(hi), 1 * s);
        dl.AddLine(min + new Vector2(1 * s, 1 * s), new Vector2(min.X + 1 * s, max.Y - 1 * s), U32(hi), 1 * s);
        dl.AddLine(new Vector2(min.X + 1 * s, max.Y - 1 * s), max - new Vector2(1 * s, 1 * s), U32(lo), 1 * s);
        dl.AddLine(new Vector2(max.X - 1 * s, min.Y + 1 * s), max - new Vector2(1 * s, 1 * s), U32(lo), 1 * s);
    }

    /// <summary>Three-stop horizontal gradient stripe. With animation on, the stops drift along the bar.</summary>
    public static void DrawStripe(ImDrawListPtr dl, Vector2 min, float width, float height)
    {
        var t = AnimatedStripe && !ReduceMotion ? (float)(ImGui.GetTime() * 0.08 % 1.0) : 0f;
        const int segments = 24;
        var segW = width / segments;
        for (var i = 0; i < segments; i++)
        {
            var u0 = (i / (float)segments + t) % 1f;
            var u1 = ((i + 1) / (float)segments + t) % 1f;
            if (u1 < u0) u1 = 1f; // wrap boundary
            var c0 = StripeColor(u0);
            var c1 = StripeColor(u1);
            var a = min + new Vector2(segW * i, 0);
            var b = a + new Vector2(segW + 0.5f, height);
            dl.AddRectFilledMultiColor(a, b, U32(c0), U32(c1), U32(c1), U32(c0));
        }
    }

    /// <summary>Cyan → accent → gold → (back to) cyan across u ∈ [0,1], so the drift wraps seamlessly.</summary>
    private static Vector4 StripeColor(float u)
    {
        u = ((u % 1f) + 1f) % 1f;
        return u < 1f / 3f ? Lerp(StripeA, AccentColor, u * 3f)
             : u < 2f / 3f ? Lerp(AccentColor, StripeC, (u - 1f / 3f) * 3f)
             : Lerp(StripeC, StripeA, (u - 2f / 3f) * 3f);
    }

    /// <summary>Soft glow: a few expanding translucent rects behind an element.</summary>
    public static void DrawGlow(ImDrawListPtr dl, Vector2 min, Vector2 max, Vector4 color, float radius, float strength = 1f)
    {
        if (!Effects) return;
        const int layers = 4;
        for (var i = layers; i >= 1; i--)
        {
            var k = i / (float)layers;
            var a = color.W * strength * (1f - k) * 0.5f;
            dl.AddRectFilled(min - new Vector2(radius * k), max + new Vector2(radius * k), U32(WithAlpha(color, a)), radius * k);
        }
    }

    /// <summary>Text with the 1 px black drop shadow that gives small type its bite on dark panels.</summary>
    public static void ShadowText(ImDrawListPtr dl, Vector2 pos, Vector4 color, string text, float s)
    {
        dl.AddText(pos + new Vector2(1 * s, 1 * s), U32(TextShadow), text);
        dl.AddText(pos, U32(color), text);
    }
}
