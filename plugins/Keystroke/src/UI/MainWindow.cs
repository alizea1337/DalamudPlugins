using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Keystroke.UI.Pages;
using Keystroke.UI.Theme;

namespace Keystroke.UI;

/// <summary>
/// The settings-style main window: a sidebar of pages on the left, the selected page on the
/// right. Chrome (close control, drag region) is drawn by hand so the window looks like a
/// macOS panel rather than a stock ImGui window.
/// </summary>
internal sealed class MainWindow : Window
{
    private readonly Plugin plugin;
    private readonly List<Page> pages;
    private int selected;
    private bool closeHovered;

    public MainWindow(Plugin plugin, IEnumerable<Page> pages) : base("Keystroke###KeystrokeMain")
    {
        this.plugin = plugin;
        this.pages = new List<Page>(pages);
        selected = Math.Clamp(plugin.Config.Window.LastPage, 0, this.pages.Count - 1);

        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse;
        Size = plugin.Config.Window.Size;
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(700, 460), MaximumSize = new Vector2(1600, 1200) };
        RespectCloseHotkey = true;
    }

    public void Select<T>() where T : Page
    {
        var i = pages.FindIndex(p => p is T);
        if (i >= 0) selected = i;
    }

    public override void PreDraw()
    {
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

        var s = Widgets.Scale;
        var size = ImGui.GetWindowSize();
        var pos = ImGui.GetWindowPos();
        var sidebarW = Theme.Theme.SidebarWidth * s;
        var dl = ImGui.GetWindowDrawList();

        // Sidebar background with rounded left corners
        var r = Theme.Theme.WindowRounding * s;
        dl.AddRectFilled(pos, pos + new Vector2(sidebarW, size.Y), Theme.Theme.U32(Theme.Theme.SidebarBg), r, ImDrawFlags.RoundCornersLeft);
        dl.AddLine(pos + new Vector2(sidebarW, 0), pos + new Vector2(sidebarW, size.Y), Theme.Theme.U32(Theme.Theme.Separator));

        DrawCloseControl(pos, s);
        DrawSidebar(sidebarW, s);
        DrawContent(sidebarW, size, s);

        if (ImGui.GetWindowSize() != plugin.Config.Window.Size)
            plugin.Config.Window.Size = ImGui.GetWindowSize();
    }

    private void DrawCloseControl(Vector2 pos, float s)
    {
        // macOS traffic light: a single red close dot; shows × on hover.
        var center = pos + new Vector2(20, 20) * s;
        var radius = 6 * s;
        ImGui.SetCursorScreenPos(center - new Vector2(radius + 4 * s));
        if (ImGui.InvisibleButton("##close", new Vector2((radius + 4 * s) * 2)))
            IsOpen = false;
        closeHovered = ImGui.IsItemHovered();
        var dl = ImGui.GetWindowDrawList();
        dl.AddCircleFilled(center, radius, Theme.Theme.U32(Theme.Theme.Rgb(0xFF, 0x5F, 0x57)), 24);
        dl.AddCircle(center, radius, Theme.Theme.U32(new Vector4(0, 0, 0, 0.25f)), 24, 1f);
        if (closeHovered)
        {
            var k = 2.6f * s;
            var c = Theme.Theme.U32(new Vector4(0.3f, 0.05f, 0.03f, 1f));
            dl.AddLine(center + new Vector2(-k, -k), center + new Vector2(k, k), c, 1.4f * s);
            dl.AddLine(center + new Vector2(-k, k), center + new Vector2(k, -k), c, 1.4f * s);
        }
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

            // Footer: state pill
            var state = plugin.Controller.State;
            var (col, label) = StateBadge(state);
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

    public static (Vector4, string) StateBadge(Automation.AutomationState state) => state switch
    {
        Automation.AutomationState.Idle => (Theme.Theme.Gray, Strings.StateIdle),
        Automation.AutomationState.WaitingForDuty => (Theme.Theme.Yellow, Strings.StateWaitingForDuty),
        Automation.AutomationState.Queued => (Theme.Theme.Accent, Strings.StateQueued),
        Automation.AutomationState.Playing => (Theme.Theme.Green, Strings.StatePlaying),
        Automation.AutomationState.Paused => (Theme.Theme.Orange, Strings.StatePaused),
        Automation.AutomationState.Finishing => (Theme.Theme.Orange, Strings.StateStopping),
        Automation.AutomationState.Error => (Theme.Theme.Red, Strings.StateError),
        _ => (Theme.Theme.Gray, state.ToString()),
    };
}
