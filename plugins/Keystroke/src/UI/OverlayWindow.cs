using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Keystroke.Automation;
using Keystroke.Game;
using Keystroke.UI.Theme;

namespace Keystroke.UI;

/// <summary>
/// Compact status pill: state dot, status text, the word being typed, session counters.
/// Visibility, lock/click-through, opacity and position come from the Overlay settings.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private readonly Plugin plugin;
    private Vector2 lastPos;

    public OverlayWindow(Plugin plugin) : base("Keystroke Overlay###KeystrokeOverlay")
    {
        this.plugin = plugin;
        Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar |
                ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.AlwaysAutoResize |
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking;
        RespectCloseHotkey = false;
        IsOpen = true;
        Position = plugin.Config.Overlay.Position;
        PositionCondition = ImGuiCond.FirstUseEver;
    }

    private OverlaySettings Settings => plugin.Config.Overlay;

    public override bool DrawConditions()
    {
        var o = Settings;
        if (!o.Enabled || !Services.ClientState.IsLoggedIn) return false;
        if (o.OnlyInKeyboundAreas && !Keybound.IsKeyboundArea((ushort)Services.ClientState.TerritoryType)) return false;
        return true;
    }

    public override void PreDraw()
    {
        var o = Settings;
        var f = Flags & ~(ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMouseInputs);
        if (o.Locked)
        {
            f |= ImGuiWindowFlags.NoMove;
            if (o.ClickThroughWhenLocked) f |= ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoMouseInputs;
        }
        Flags = f;

        var s = Widgets.Scale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(14, 10) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 14 * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(o.Opacity, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.WindowBg, Theme.Theme.Rgb(0x1C, 0x1C, 0x1E, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.Border, Theme.Theme.Separator);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(4);
    }

    public override void Draw()
    {
        using var theme = Theme.Theme.Push();
        using var body = Widgets.F.Body.Push();
        var s = Widgets.Scale;
        var c = plugin.Controller;
        var o = Settings;
        var (col, label) = MainWindow.StateBadge(c.State);

        ImGui.BeginGroup();
        Widgets.StatusDot(col, label);
        if (c.State != AutomationState.Idle && c.StatusDetail.Length > 0 && c.StatusDetail != label)
        {
            ImGui.SameLine(0, 8 * s);
            Widgets.Caption(c.StatusDetail.Length > 48 ? c.StatusDetail[..48] + "…" : c.StatusDetail);
        }
        ImGui.EndGroup();

        if (o.ShowCurrentAnswer && c.CurrentPrompt.Length > 0)
        {
            var prompt = c.CurrentPrompt;
            var done = Math.Min(c.CurrentAnswer.Length, prompt.Length);
            using var semi = Widgets.F.BodySemibold.Push();
            ImGui.TextColored(Theme.Theme.Accent, prompt[..done]);
            ImGui.SameLine(0, 0);
            ImGui.TextColored(Theme.Theme.TertiaryLabel, prompt[done..]);
        }

        if (o.ShowSessionStats)
        {
            var st = c.Session;
            var ms = c.MeasuredMsPerKey;
            var speed = ms > 0 ? $" · {60000 / ms / 5:0} WPM" : "";
            Widgets.Caption($"{st.GamesCompleted} games · {st.MgpGained:N0} MGP · {st.MgcGained:N0} MGC{speed}");
        }

        // Persist a moved position.
        var pos = ImGui.GetWindowPos();
        if (!o.Locked && pos != lastPos && lastPos != Vector2.Zero && pos != o.Position)
        {
            o.Position = pos;
            plugin.Config.Save();
        }
        lastPos = pos;
    }
}
