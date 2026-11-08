using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Keystroke.Automation;
using Keystroke.Game;
using Keystroke.UI.Theme;

namespace Keystroke.UI;

/// <summary>
/// Small bevelled strip: signal stripe, state dot and status, the word as keycaps, session counters.
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
                ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoDocking | ImGuiWindowFlags.NoBackground;
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
        var s = Widgets.S;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Theme.Theme.FrameInset * s + 8 * s, Theme.Theme.FrameInset * s + Theme.Theme.StripeHeight * s + 6 * s));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, Math.Clamp(o.Opacity, 0.2f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowMinSize, new Vector2(220 * s, 0));
    }

    public override void PostDraw() => ImGui.PopStyleVar(5);

    public override void Draw()
    {
        using var theme = Theme.Theme.Push();
        using var body = Widgets.F.Body.Push();
        var s = Widgets.S;
        var c = plugin.Controller;
        var o = Settings;
        var (col, label) = MainWindow.StateBadge(c.State);

        // Frame behind the content (window has no background of its own)
        var dl = ImGui.GetWindowDrawList();
        var pos = ImGui.GetWindowPos();
        var size = ImGui.GetWindowSize();
        dl.ChannelsSplit(2);
        dl.ChannelsSetCurrent(1);

        ImGui.BeginGroup();
        Widgets.StatusDot(col, label, c.State is AutomationState.Playing or AutomationState.Queued);
        if (c.State != AutomationState.Idle && c.StatusDetail.Length > 0 && c.StatusDetail != label)
        {
            ImGui.SameLine(0, 8 * s);
            Widgets.Small(c.StatusDetail.Length > 40 ? c.StatusDetail[..40] + "…" : c.StatusDetail);
        }
        ImGui.EndGroup();

        if (o.ShowCurrentAnswer && c.CurrentPrompt.Length > 0)
        {
            ImGui.Dummy(new Vector2(0, 2 * s));
            if (plugin.Config.Appearance.Keycaps) Widgets.Keycaps(c.CurrentPrompt, c.CurrentAnswer.Length, 18f, 260 * s);
            else { using var b = Widgets.F.Bold.Push(); Widgets.Text(c.CurrentPrompt, Theme.Theme.AccentText); }
        }

        if (o.ShowSessionStats)
        {
            var st = c.Session;
            var ms = c.MeasuredMsPerKey;
            var speed = ms > 0 ? $" · {60000 / ms / 5:0} wpm" : "";
            Widgets.Small($"{st.GamesCompleted} games · {st.MgpGained:N0} mgp · {st.MgcGained:N0} mgc{speed}");
        }

        dl.ChannelsSetCurrent(0);
        Theme.Theme.DrawFrame(dl, pos, pos + size, s);
        var inset = Theme.Theme.FrameInset * s;
        Theme.Theme.DrawStripe(dl, pos + new Vector2(inset, inset), size.X - inset * 2, Theme.Theme.StripeHeight * s);
        dl.ChannelsMerge();

        if (!o.Locked && pos != lastPos && lastPos != Vector2.Zero && pos != o.Position)
        {
            o.Position = pos;
            plugin.Config.Save();
        }
        lastPos = pos;
    }
}
