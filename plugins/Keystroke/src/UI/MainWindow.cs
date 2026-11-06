using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Keystroke.Automation;
using Keystroke.UI.Pages;
using Keystroke.UI.Theme;

namespace Keystroke.UI;

/// <summary>
/// The settings-style main window: a sidebar of pages on the left, the selected page on the
/// right. Chrome (traffic lights, drag region) is drawn by hand so the window looks like a
/// macOS panel rather than a stock ImGui window. The yellow light collapses the window to a
/// compact transport panel; the green light expands it again.
/// </summary>
internal sealed class MainWindow : Window
{
    private static readonly Vector2 CompactSize = new(360, 168);
    private static readonly string[] ModeLabels = { "Human", "Instant" };

    private readonly Plugin plugin;
    private readonly List<Page> pages;
    private int selected;
    private bool applySize;

    public MainWindow(Plugin plugin, IEnumerable<Page> pages) : base("Keystroke###KeystrokeMain")
    {
        this.plugin = plugin;
        this.pages = new List<Page>(pages);
        selected = Math.Clamp(plugin.Config.Window.LastPage, 0, this.pages.Count - 1);

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse;
        RespectCloseHotkey = true;
        ApplyMode();
    }

    private bool Compact => plugin.Config.Window.Compact;

    public void Select<T>() where T : Page
    {
        var i = pages.FindIndex(p => p is T);
        if (i >= 0) selected = i;
    }

    public void SetCompact(bool compact)
    {
        if (!compact && Compact) { /* leaving compact: nothing to store, size was kept */ }
        plugin.Config.Window.Compact = compact;
        plugin.Config.Save();
        ApplyMode();
    }

    private void ApplyMode()
    {
        if (Compact)
        {
            Size = CompactSize;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = CompactSize, MaximumSize = CompactSize };
            Flags |= ImGuiWindowFlags.NoResize;
        }
        else
        {
            Size = plugin.Config.Window.Size;
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(700, 460), MaximumSize = new Vector2(1600, 1200) };
            Flags &= ~ImGuiWindowFlags.NoResize;
        }
        SizeCondition = ImGuiCond.Always;
        applySize = true;
    }

    public override void PreDraw()
    {
        if (!applySize) SizeCondition = ImGuiCond.FirstUseEver;
        applySize = false;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, Theme.Theme.WindowRounding * Widgets.Scale);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Theme.WindowBg);
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Theme.Separator);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(3);
    }

    public override void OnClose()
    {
        plugin.Config.Window.LastPage = selected;
        plugin.Config.Save();
    }

    public override void Draw()
    {
        using var theme = Theme.Theme.Push();
        using var body = Widgets.F.Body.Push();

        if (Compact) DrawCompact();
        else DrawFull();

        InstantModeSheet.Draw(plugin.Config);
    }

    // ------------------------------------------------------------------ full layout

    private void DrawFull()
    {
        var s = Widgets.Scale;
        var size = ImGui.GetWindowSize();
        var pos = ImGui.GetWindowPos();
        var sidebarW = Theme.Theme.SidebarWidth * s;
        var dl = ImGui.GetWindowDrawList();

        var r = Theme.Theme.WindowRounding * s;
        dl.AddRectFilled(pos, pos + new Vector2(sidebarW, size.Y), Theme.Theme.U32(Theme.Theme.SidebarBg), r, ImDrawFlags.RoundCornersLeft);
        dl.AddLine(pos + new Vector2(sidebarW, 0), pos + new Vector2(sidebarW, size.Y), Theme.Theme.U32(Theme.Theme.Separator));

        DrawTrafficLights(pos, s, compact: false);
        DrawSidebar(sidebarW, s);
        DrawContent(sidebarW, size, s);

        if (ImGui.GetWindowSize() != plugin.Config.Window.Size)
            plugin.Config.Window.Size = ImGui.GetWindowSize();
    }

    /// <summary>Red closes; yellow collapses to compact; green (compact only) expands.</summary>
    private void DrawTrafficLights(Vector2 pos, float s, bool compact)
    {
        var radius = 6 * s;
        var first = pos + new Vector2(20, 20) * s;
        var step = new Vector2(20 * s, 0);

        if (Light("##close", first, radius, Theme.Theme.Rgb(0xFF, 0x5F, 0x57), LightGlyph.Close, "Close"))
            IsOpen = false;

        if (!compact)
        {
            if (Light("##compact", first + step, radius, Theme.Theme.Rgb(0xFE, 0xBC, 0x2E), LightGlyph.Minus, "Compact view"))
                SetCompact(true);
        }
        else
        {
            if (Light("##expand", first + step, radius, Theme.Theme.Rgb(0x28, 0xC8, 0x40), LightGlyph.Expand, "Full view"))
                SetCompact(false);
        }
    }

    private enum LightGlyph { Close, Minus, Expand }

    private static bool Light(string id, Vector2 center, float radius, Vector4 color, LightGlyph glyph, string tooltip)
    {
        var s = Widgets.Scale;
        var hit = radius + 4 * s;
        ImGui.SetCursorScreenPos(center - new Vector2(hit));
        var pressed = ImGui.InvisibleButton(id, new Vector2(hit * 2));
        var hovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(center, radius, Theme.Theme.U32(color), 24);
        dl.AddCircle(center, radius, Theme.Theme.U32(new Vector4(0, 0, 0, 0.25f)), 24, 1f);
        if (hovered)
        {
            var k = 2.6f * s;
            var c = Theme.Theme.U32(new Vector4(0.2f, 0.12f, 0.02f, 1f));
            switch (glyph)
            {
                case LightGlyph.Close:
                    dl.AddLine(center + new Vector2(-k, -k), center + new Vector2(k, k), c, 1.4f * s);
                    dl.AddLine(center + new Vector2(-k, k), center + new Vector2(k, -k), c, 1.4f * s);
                    break;
                case LightGlyph.Minus:
                    dl.AddLine(center + new Vector2(-k, 0), center + new Vector2(k, 0), c, 1.6f * s);
                    break;
                case LightGlyph.Expand:
                    // two small triangles pointing outward, like macOS zoom
                    dl.AddTriangleFilled(center + new Vector2(-k, -k), center + new Vector2(0.6f * k, -k), center + new Vector2(-k, 0.6f * k), c);
                    dl.AddTriangleFilled(center + new Vector2(k, k), center + new Vector2(-0.6f * k, k), center + new Vector2(k, -0.6f * k), c);
                    break;
            }
            Widgets.Tooltip(tooltip);
        }
        return pressed;
    }

    private void DrawSidebar(float sidebarW, float s)
    {
        ImGui.SetCursorPos(new Vector2(0, 44 * s));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(10 * s, 0));
        if (ImGui.BeginChild("##sidebar", new Vector2(sidebarW, ImGui.GetWindowHeight() - 44 * s), false, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            var dl = ImGui.GetWindowDrawList();
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i];
                var itemH = 30 * s;
                var itemW = ImGui.GetContentRegionAvail().X;
                var p = ImGui.GetCursorScreenPos();
                var isSelected = i == selected;

                if (ImGui.InvisibleButton("##nav" + i, new Vector2(itemW, itemH)))
                    selected = i;
                var hovered = ImGui.IsItemHovered();

                if (isSelected)
                    dl.AddRectFilled(p, p + new Vector2(itemW, itemH), Theme.Theme.U32(Theme.Theme.Accent), 6 * s);
                else if (hovered)
                    dl.AddRectFilled(p, p + new Vector2(itemW, itemH), Theme.Theme.U32(Theme.Theme.SidebarSelection), 6 * s);

                var iconCol = isSelected ? Vector4.One : Theme.Theme.Accent;
                var textCol = isSelected ? Vector4.One : Theme.Theme.Label;
                var iconSz = Widgets.IconSize(page.Icon);
                using (Widgets.F.Icons.Push())
                    dl.AddText(p + new Vector2(10 * s, (itemH - iconSz.Y) / 2), Theme.Theme.U32(iconCol), page.Icon.ToString());
                var textSz = ImGui.CalcTextSize(page.Title);
                using (Widgets.F.BodyMedium.Push())
                    dl.AddText(p + new Vector2(10 * s + 24 * s, (itemH - textSz.Y) / 2), Theme.Theme.U32(textCol), page.Title);

                ImGui.Dummy(new Vector2(0, 2 * s));
            }

            var (col, label) = StateBadge(plugin.Controller.State);
            ImGui.SetCursorPosY(ImGui.GetWindowHeight() - 36 * s);
            Widgets.StatusDot(col, label);
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private void DrawContent(float sidebarW, Vector2 size, float s)
    {
        var pad = Theme.Theme.ContentPadding * s;
        ImGui.SetCursorPos(new Vector2(sidebarW + pad, 36 * s));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(0, 0));
        var contentSize = new Vector2(size.X - sidebarW - pad * 2, size.Y - 36 * s - pad);
        if (ImGui.BeginChild("##content", contentSize, false, ImGuiWindowFlags.NoBackground))
        {
            try
            {
                pages[selected].Draw();
            }
            catch (Exception e)
            {
                Services.Log.Error(e, "Page draw failed: {Page}", pages[selected].Title);
                Widgets.Banner(Icon.TriangleAlert, Theme.Theme.Red, $"This page failed to draw: {e.Message}");
            }
            ImGui.Dummy(new Vector2(0, pad));
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    // ------------------------------------------------------------------ compact layout

    private void DrawCompact()
    {
        var s = Widgets.Scale;
        var pos = ImGui.GetWindowPos();
        var controller = plugin.Controller;
        var state = controller.State;
        var (stateColor, stateLabel) = StateBadge(state);
        var t = plugin.Config.Typing;

        DrawTrafficLights(pos, s, compact: true);

        // Title at top centre
        using (Widgets.F.BodySemibold.Push())
        {
            var w = ImGui.CalcTextSize(Strings.PluginName).X;
            ImGui.SetCursorPos(new Vector2((ImGui.GetWindowWidth() - w) / 2, 12 * s));
            Widgets.Secondary(Strings.PluginName);
        }

        // State line
        ImGui.SetCursorPos(new Vector2(20 * s, 44 * s));
        Widgets.StatusDot(stateColor, stateLabel);
        ImGui.SameLine(0, 8 * s);
        Widgets.Caption(controller.StatusDetail);

        // Transport + mode
        var running = state is AutomationState.Playing or AutomationState.WaitingForDuty or AutomationState.Queued or AutomationState.Finishing;
        var paused = state == AutomationState.Paused;
        ImGui.SetCursorPos(new Vector2(20 * s, 74 * s));
        if (!running && !paused)
        {
            if (Widgets.CircleButton("##c_start", Icon.Play, 40, Theme.Theme.Accent, Vector4.One, "Start")) controller.Start();
        }
        else
        {
            if (paused)
            {
                if (Widgets.CircleButton("##c_resume", Icon.Play, 40, Theme.Theme.Accent, Vector4.One, "Resume")) controller.Resume();
            }
            else
            {
                if (Widgets.CircleButton("##c_pause", Icon.Pause, 40, Theme.Theme.ControlBgHover, Theme.Theme.Label, "Pause")) controller.Pause();
            }
        }
        ImGui.SameLine(0, 8 * s);
        if (!running && !paused) ImGui.BeginDisabled();
        if (Widgets.CircleButton("##c_stop", Icon.Square, 40, Theme.Theme.WithAlpha(Theme.Theme.Red, 0.22f), Theme.Theme.Red, "Stop")) controller.Stop();
        if (!running && !paused) ImGui.EndDisabled();

        ImGui.SameLine(0, 16 * s);
        ImGui.BeginGroup();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 7 * s);
        var mode = t.InstantMode ? 1 : 0;
        if (Widgets.Segmented("##c_mode", ModeLabels, ref mode, ImGui.GetWindowWidth() - ImGui.GetCursorPosX() - 20 * s))
        {
            if (mode == 1) InstantModeSheet.Request(t, plugin.Config);
            else { t.InstantMode = false; plugin.Config.Save(); }
        }
        if (ImGui.IsItemHovered())
            Widgets.Tooltip(t.InstantMode ? "Instant: inserts whole answers. Detectable." : "Human: humanized per-key typing.");
        ImGui.EndGroup();

        // Session line
        var st = controller.Session;
        ImGui.SetCursorPos(new Vector2(20 * s, 130 * s));
        Widgets.Caption($"{st.GamesCompleted} games · {st.MgpGained:N0} MGP · {st.MgcGained:N0} MGC this session");
    }

    public static (Vector4, string) StateBadge(AutomationState state) => state switch
    {
        AutomationState.Idle => (Theme.Theme.Gray, Strings.StateIdle),
        AutomationState.WaitingForDuty => (Theme.Theme.Yellow, Strings.StateWaitingForDuty),
        AutomationState.Queued => (Theme.Theme.Accent, Strings.StateQueued),
        AutomationState.Playing => (Theme.Theme.Green, Strings.StatePlaying),
        AutomationState.Paused => (Theme.Theme.Orange, Strings.StatePaused),
        AutomationState.Finishing => (Theme.Theme.Orange, Strings.StateStopping),
        AutomationState.Error => (Theme.Theme.Red, Strings.StateError),
        _ => (Theme.Theme.Gray, state.ToString()),
    };
}
