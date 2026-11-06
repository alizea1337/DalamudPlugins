using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.Automation;
using Keystroke.Game;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class DashboardPage : Page
{
    private int loopCount = 10;

    public DashboardPage(Plugin plugin) : base(plugin) { }

    public override string Title => Strings.Dashboard;
    public override char Icon => Theme.Icon.LayoutDashboard;

    public override void Draw()
    {
        var s = Widgets.Scale;
        var state = Controller.State;
        var (stateColor, stateLabel) = MainWindow.StateBadge(state);

        Widgets.PageHeader(Strings.Dashboard, DateTime.Now.ToString("dddd, d MMMM"));

        if (!Services.ClientState.IsLoggedIn)
            Widgets.Banner(Theme.Icon.Info, Theme.Theme.Accent, Strings.NotLoggedIn);
        else if (!Keybound.IsKeyboundArea((ushort)Services.ClientState.TerritoryType))
            Widgets.Banner(Theme.Icon.Info, Theme.Theme.Accent, Strings.NotInKeybound);

        // Transport card
        if (Widgets.BeginCard("transport"))
        {
            ImGui.Dummy(new Vector2(0, 6 * s));
            ImGui.BeginGroup();
            DrawStateRing(stateColor, s);
            ImGui.EndGroup();
            ImGui.SameLine(0, 18 * s);
            ImGui.BeginGroup();
            using (Widgets.F.Title.Push()) ImGui.TextColored(stateColor, stateLabel);
            Widgets.Secondary(Controller.StatusDetail);
            if (Controller.LastError != null && state == AutomationState.Error)
                Widgets.Caption(Controller.LastError, Theme.Theme.Red);
            if (Controller.CurrentPrompt.Length > 0)
            {
                // Word being typed, with the already-sent part highlighted.
                var prompt = Controller.CurrentPrompt;
                var done = Controller.CurrentAnswer.Length;
                using var mono = Widgets.F.BodySemibold.Push();
                ImGui.TextColored(Theme.Theme.Accent, prompt[..Math.Min(done, prompt.Length)]);
                ImGui.SameLine(0, 0);
                ImGui.TextColored(Theme.Theme.TertiaryLabel, prompt[Math.Min(done, prompt.Length)..]);
            }
            if (Controller.LoopRemaining > 0)
                Widgets.Caption($"{Controller.LoopRemaining} games remaining in loop");
            else if (Controller.LoopRemaining < 0)
                Widgets.Caption("Repeating until stopped");
            ImGui.Dummy(new Vector2(0, 8 * s));
            DrawTransport(state, s);
            ImGui.EndGroup();
            ImGui.Dummy(new Vector2(0, 8 * s));
            Widgets.EndCard();
        }

        // Stats
        var stats = Controller.Session;
        Widgets.SectionLabel("THIS SESSION");
        if (Widgets.BeginCard("session"))
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            var w = ImGui.GetContentRegionAvail().X / 4;
            Widgets.StatTile("Games", stats.GamesCompleted.ToString(), null, w);
            ImGui.SameLine();
            Widgets.StatTile("MGP gained", stats.MgpGained.ToString("N0"), Theme.Theme.Green, w);
            ImGui.SameLine();
            Widgets.StatTile("MGC gained", stats.MgcGained.ToString("N0"), Theme.Theme.Purple, w);
            ImGui.SameLine();
            Widgets.StatTile("Games / hour", stats.GamesPerHour.ToString("0.0"), null, w);
            ImGui.Dummy(new Vector2(0, 10 * s));
            Widgets.StatTile("WPM", stats.WordsPerMinute.ToString("0"), null, w);
            ImGui.SameLine();
            Widgets.StatTile("Accuracy", (stats.Accuracy * 100).ToString("0.0") + "%", null, w);
            ImGui.SameLine();
            Widgets.StatTile("MGP / hour", stats.MgpPerHour.ToString("N0"), null, w);
            ImGui.SameLine();
            Widgets.StatTile("Since", stats.StartedAt.ToString("HH:mm"), null, w);
            ImGui.Dummy(new Vector2(0, 6 * s));
            Widgets.EndCard();
        }

        Widgets.SectionLabel("LIFETIME");
        if (Widgets.BeginCard("lifetime"))
        {
            Widgets.ValueRow("Games played", "—", description: "Lifetime stats and the history chart arrive with the rewards milestone.");
            Widgets.EndCard();
        }
    }

    private static void DrawStateRing(Vector4 color, float s)
    {
        var d = 72 * s;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var c = pos + new Vector2(d / 2);
        dl.AddCircle(c, d / 2 - 3 * s, Theme.Theme.U32(Theme.Theme.WithAlpha(color, 0.25f)), 64, 6 * s);
        var t = (float)(ImGui.GetTime() % 2.0) / 2f;
        dl.PathArcTo(c, d / 2 - 3 * s, t * MathF.Tau, t * MathF.Tau + MathF.PI * 0.6f, 32);
        dl.PathStroke(Theme.Theme.U32(color), ImDrawFlags.None, 6 * s);
        var ic = Theme.Icon.Keyboard;
        var sz = Widgets.IconSize(ic, true);
        using (Widgets.F.IconsLarge.Push())
            dl.AddText(c - sz / 2, Theme.Theme.U32(color), ic.ToString());
        ImGui.Dummy(new Vector2(d, d));
    }

    private void DrawTransport(AutomationState state, float s)
    {
        var running = state is AutomationState.Playing or AutomationState.WaitingForDuty or AutomationState.Queued or AutomationState.Finishing;
        var paused = state == AutomationState.Paused;

        if (!running && !paused)
        {
            if (Widgets.CircleButton("##start", Theme.Icon.Play, 44, Theme.Theme.Accent, Vector4.One, "Start")) Controller.Start();
        }
        else
        {
            if (paused)
            {
                if (Widgets.CircleButton("##resume", Theme.Icon.Play, 44, Theme.Theme.Accent, Vector4.One, "Resume")) Controller.Resume();
            }
            else
            {
                if (Widgets.CircleButton("##pause", Theme.Icon.Pause, 44, Theme.Theme.ControlBgHover, Theme.Theme.Label, "Pause")) Controller.Pause();
            }
            ImGui.SameLine(0, 10 * s);
            if (Widgets.CircleButton("##stop", Theme.Icon.Square, 44, Theme.Theme.WithAlpha(Theme.Theme.Red, 0.22f), Theme.Theme.Red, "Stop")) Controller.Stop();
        }

        // Count field and loop buttons share one frame height, centred on the 44 px transport circles.
        var h = Widgets.FrameHeight;
        var rowTop = ImGui.GetCursorPosY();
        ImGui.SameLine(0, 18 * s);
        ImGui.BeginGroup();
        ImGui.SetCursorPosY(rowTop + (44 * s - h) / 2);
        ImGui.SetNextItemWidth(70 * s);
        ImGui.InputInt("##loopcount", ref loopCount, 0, 0);
        loopCount = Math.Clamp(loopCount, 1, 999);
        ImGui.SameLine(0, 6 * s);
        if (Widgets.Button("Loop##loop", Widgets.ButtonStyle.Default, new Vector2(78 * s, h), Theme.Icon.Repeat) && !running)
            Controller.StartLoop(loopCount);
        if (ImGui.IsItemHovered()) Widgets.Tooltip("Queue and play this many games, then stop.");
        ImGui.SameLine(0, 6 * s);
        if (Widgets.Button("Repeat##unl", Widgets.ButtonStyle.Default, new Vector2(92 * s, h), Theme.Icon.RefreshCw) && !running)
            Controller.StartUnlimited();
        if (ImGui.IsItemHovered()) Widgets.Tooltip("Queue and play until you stop it or a stop condition is met.");
        ImGui.EndGroup();
    }
}
