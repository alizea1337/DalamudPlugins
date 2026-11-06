using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Keystroke.UI.Theme;

/// <summary>
/// Dark macOS-style palette and metrics. Pushed only while Keystroke windows draw so other
/// Dalamud windows keep their own look.
/// </summary>
internal static class Theme
{
    // Surfaces (opaque base values; Configure() applies the glass transparency)
    private static readonly Vector4 WindowBgBase = Rgb(0x1C, 0x1C, 0x1E);
    private static readonly Vector4 SidebarBgBase = Rgb(0x22, 0x22, 0x25);
    private static readonly Vector4 CardBgBase = Rgb(0x2C, 0x2C, 0x2E);

    public static Vector4 WindowBg { get; private set; } = WindowBgBase;
    public static Vector4 SidebarBg { get; private set; } = SidebarBgBase;
    public static Vector4 CardBg { get; private set; } = CardBgBase;
    public static readonly Vector4 CardBgHover = Rgb(0x33, 0x33, 0x36);

    /// <summary>
    /// Applies the appearance settings. Transparency lowers the alpha of the window and sidebar so
    /// the game shows through; cards keep more opacity so rows stay legible on busy backgrounds.
    /// ImGui cannot blur what is behind a window, so this is translucency, not a backdrop blur.
    /// </summary>
    public static void Configure(AppearanceSettings a)
    {
        var t = a.Glass ? Math.Clamp(a.Transparency, 0f, 1f) : 0f;
        var windowAlpha = 1f - t * 0.55f;      // never below 0.45 so text stays readable
        var sidebarAlpha = 1f - t * 0.45f;
        var cardAlpha = a.ContrastCards ? 1f - t * 0.25f : 1f - t * 0.55f;
        WindowBg = WithAlpha(WindowBgBase, windowAlpha);
        SidebarBg = WithAlpha(SidebarBgBase, sidebarAlpha);
        CardBg = WithAlpha(CardBgBase, cardAlpha);
    }
    public static readonly Vector4 ControlBg = Rgb(0x3A, 0x3A, 0x3D);
    public static readonly Vector4 ControlBgHover = Rgb(0x48, 0x48, 0x4C);
    public static readonly Vector4 ControlBgActive = Rgb(0x55, 0x55, 0x5A);
    public static readonly Vector4 Separator = new(1f, 1f, 1f, 0.08f);
    public static readonly Vector4 SidebarSelection = new(1f, 1f, 1f, 0.10f);

    // Labels
    public static readonly Vector4 Label = Rgb(0xF5, 0xF5, 0xF7);
    public static readonly Vector4 SecondaryLabel = new(0.92f, 0.92f, 0.96f, 0.60f);
    public static readonly Vector4 TertiaryLabel = new(0.92f, 0.92f, 0.96f, 0.30f);

    // System colours (macOS dark)
    public static readonly Vector4 Accent = Rgb(0x0A, 0x84, 0xFF);
    public static readonly Vector4 AccentHover = Rgb(0x2B, 0x95, 0xFF);
    public static readonly Vector4 AccentActive = Rgb(0x00, 0x6E, 0xE0);
    public static readonly Vector4 Green = Rgb(0x30, 0xD1, 0x58);
    public static readonly Vector4 Red = Rgb(0xFF, 0x45, 0x3A);
    public static readonly Vector4 Orange = Rgb(0xFF, 0x9F, 0x0A);
    public static readonly Vector4 Yellow = Rgb(0xFF, 0xD6, 0x0A);
    public static readonly Vector4 Purple = Rgb(0xBF, 0x5A, 0xF2);
    public static readonly Vector4 Gray = Rgb(0x8E, 0x8E, 0x93);

    // Metrics (unscaled; multiply by ImGuiHelpers.GlobalScale where needed)
    public const float WindowRounding = 12f;
    public const float CardRounding = 10f;
    public const float ControlRounding = 6f;
    public const float SidebarWidth = 210f;
    public const float ContentPadding = 24f;
    public const float CardPadding = 14f;
    public const float RowHeight = 40f;

    public static Vector4 Rgb(byte r, byte g, byte b, float a = 1f) => new(r / 255f, g / 255f, b / 255f, a);

    public static uint U32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    public static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    public static Vector4 Lerp(Vector4 a, Vector4 b, float t) => Vector4.Lerp(a, b, Math.Clamp(t, 0f, 1f));

    /// <summary>Pushes the full Keystroke style. Dispose the returned scope to pop it.</summary>
    public static Scope Push()
    {
        var colors = 0;
        void C(ImGuiCol col, Vector4 v) { ImGui.PushStyleColor(col, v); colors++; }

        C(ImGuiCol.WindowBg, WindowBg);
        C(ImGuiCol.ChildBg, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.PopupBg, CardBg);
        C(ImGuiCol.Border, Separator);
        C(ImGuiCol.BorderShadow, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.Text, Label);
        C(ImGuiCol.TextDisabled, TertiaryLabel);
        C(ImGuiCol.FrameBg, ControlBg);
        C(ImGuiCol.FrameBgHovered, ControlBgHover);
        C(ImGuiCol.FrameBgActive, ControlBgActive);
        C(ImGuiCol.TitleBg, WindowBg);
        C(ImGuiCol.TitleBgActive, WindowBg);
        C(ImGuiCol.TitleBgCollapsed, WindowBg);
        C(ImGuiCol.ScrollbarBg, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.ScrollbarGrab, new Vector4(1, 1, 1, 0.18f));
        C(ImGuiCol.ScrollbarGrabHovered, new Vector4(1, 1, 1, 0.28f));
        C(ImGuiCol.ScrollbarGrabActive, new Vector4(1, 1, 1, 0.36f));
        C(ImGuiCol.CheckMark, Accent);
        C(ImGuiCol.SliderGrab, Rgb(0xE5, 0xE5, 0xEA));
        C(ImGuiCol.SliderGrabActive, Rgb(0xFF, 0xFF, 0xFF));
        C(ImGuiCol.Button, ControlBg);
        C(ImGuiCol.ButtonHovered, ControlBgHover);
        C(ImGuiCol.ButtonActive, ControlBgActive);
        C(ImGuiCol.Header, SidebarSelection);
        C(ImGuiCol.HeaderHovered, new Vector4(1, 1, 1, 0.06f));
        C(ImGuiCol.HeaderActive, new Vector4(1, 1, 1, 0.12f));
        C(ImGuiCol.Separator, Separator);
        C(ImGuiCol.SeparatorHovered, Separator);
        C(ImGuiCol.SeparatorActive, Separator);
        C(ImGuiCol.ResizeGrip, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.ResizeGripHovered, new Vector4(1, 1, 1, 0.15f));
        C(ImGuiCol.ResizeGripActive, new Vector4(1, 1, 1, 0.25f));
        C(ImGuiCol.Tab, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.TabHovered, ControlBgHover);
        C(ImGuiCol.TabActive, ControlBg);
        C(ImGuiCol.TableHeaderBg, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.TableBorderStrong, Separator);
        C(ImGuiCol.TableBorderLight, Separator);
        C(ImGuiCol.TableRowBg, new Vector4(0, 0, 0, 0));
        C(ImGuiCol.TableRowBgAlt, new Vector4(1, 1, 1, 0.02f));
        C(ImGuiCol.TextSelectedBg, WithAlpha(Accent, 0.35f));
        C(ImGuiCol.NavHighlight, new Vector4(0, 0, 0, 0));

        var vars = 0;
        void V(ImGuiStyleVar var, float v) { ImGui.PushStyleVar(var, v); vars++; }
        void V2(ImGuiStyleVar var, Vector2 v) { ImGui.PushStyleVar(var, v); vars++; }

        V(ImGuiStyleVar.WindowRounding, WindowRounding);
        V(ImGuiStyleVar.ChildRounding, CardRounding);
        V(ImGuiStyleVar.PopupRounding, CardRounding);
        V(ImGuiStyleVar.FrameRounding, ControlRounding);
        V(ImGuiStyleVar.GrabRounding, 99f);
        V(ImGuiStyleVar.ScrollbarRounding, 99f);
        V(ImGuiStyleVar.TabRounding, ControlRounding);
        V(ImGuiStyleVar.WindowBorderSize, 1f);
        V(ImGuiStyleVar.ChildBorderSize, 0f);
        V(ImGuiStyleVar.PopupBorderSize, 1f);
        V(ImGuiStyleVar.FrameBorderSize, 0f);
        V(ImGuiStyleVar.ScrollbarSize, 10f);
        V(ImGuiStyleVar.GrabMinSize, 14f);
        V2(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        V2(ImGuiStyleVar.FramePadding, new Vector2(10, 6));
        V2(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
        V2(ImGuiStyleVar.ItemInnerSpacing, new Vector2(6, 6));
        V2(ImGuiStyleVar.CellPadding, new Vector2(8, 6));
        V2(ImGuiStyleVar.ButtonTextAlign, new Vector2(0.5f, 0.5f));

        return new Scope(colors, vars);
    }

    internal readonly struct Scope : IDisposable
    {
        private readonly int colors;
        private readonly int vars;

        public Scope(int colors, int vars)
        {
            this.colors = colors;
            this.vars = vars;
        }

        public void Dispose()
        {
            ImGui.PopStyleVar(vars);
            ImGui.PopStyleColor(colors);
        }
    }
}
