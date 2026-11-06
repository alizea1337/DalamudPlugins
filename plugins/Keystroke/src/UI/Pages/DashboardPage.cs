using System;
using System.Linq;
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
            var ms = Controller.MeasuredMsPerKey;
            if (ms > 0)
                Widgets.Caption($"{ms:0} ms per key  ·  ≈{60000 / ms / 5:0} WPM  ·  {Config.Typing.Profile}{(Config.Typing.InstantMode ? " (Instant)" : "")}");
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

        var hist = Controller.History;
        Widgets.SectionLabel("LIFETIME");
        if (Widgets.BeginCard("lifetime"))
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            var w = ImGui.GetContentRegionAvail().X / 4;
            Widgets.StatTile("Games", hist.TotalGames.ToString("N0"), null, w);
            ImGui.SameLine();
            Widgets.StatTile("MGP", hist.TotalMgp.ToString("N0"), Theme.Theme.Green, w);
            ImGui.SameLine();
            Widgets.StatTile("MGC", hist.TotalMgc.ToString("N0"), Theme.Theme.Purple, w);
            ImGui.SameLine();
            Widgets.StatTile("Best WPM", hist.BestWpm.ToString("0"), null, w);
            ImGui.Dummy(new Vector2(0, 10 * s));
            DrawHistoryChart(hist, s);
            ImGui.Dummy(new Vector2(0, 4 * s));
            Widgets.EndCard();
        }
    }

    /// <summary>MGP per day for the last 14 days as rounded bars, drawn with the draw list.</summary>
    private static void DrawHistoryChart(Rewards.RewardStore hist, float s)
    {
        var days = hist.LastDays(14);
        var max = Math.Max(1, days.Max(d => d.Mgp));
        var width = ImGui.GetContentRegionAvail().X;
        var height = 96 * s;
        var pos = ImGui.GetCursorScreenPos();
        var dl = ImGui.GetWindowDrawList();
        var gap = 6 * s;
        var barW = (width - gap * (days.Count - 1)) / days.Count;
        var labelH = ImGui.GetTextLineHeight();
        var chartH = height - labelH - 4 * s;

        using (Widgets.F.Caption.Push())
        {
            ImGui.TextColored(Theme.Theme.SecondaryLabel, "MGP per day, last 14 days");
        }
        pos = ImGui.GetCursorScreenPos();

        // Baseline
        dl.AddLine(pos + new Vector2(0, chartH), pos + new Vector2(width, chartH), Theme.Theme.U32(Theme.Theme.Separator));

        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var x = pos.X + i * (barW + gap);
            var h = (float)(d.Mgp / (double)max) * (chartH - 6 * s);
            var isToday = i == days.Count - 1;
            var top = new Vector2(x, pos.Y + chartH - h);
            var bottom = new Vector2(x + barW, pos.Y + chartH);
            var col = d.Mgp > 0 ? (isToday ? Theme.Theme.Accent : Theme.Theme.WithAlpha(Theme.Theme.Accent, 0.55f)) : Theme.Theme.WithAlpha(Theme.Theme.Label, 0.06f);
            if (h < 3 * s) top.Y = bottom.Y - 3 * s;
            dl.AddRectFilled(top, bottom, Theme.Theme.U32(col), 4 * s);

            // Hover tooltip
            ImGui.SetCursorScreenPos(new Vector2(x, pos.Y));
            ImGui.InvisibleButton("##bar" + i, new Vector2(barW, chartH));
            if (ImGui.IsItemHovered())
                Widgets.Tooltip($"{d.Day:ddd d MMM}\n{d.Games} games · {d.Mgp:N0} MGP · {d.Mgc:N0} MGC");

            // Day label every other day to keep it airy
            if (i % 2 == 1 || isToday)
            {
                var label = isToday ? "Today" : d.Day.ToString("d/M");
                using var cap = Widgets.F.Caption.Push();
                var ts = ImGui.CalcTextSize(label);
                dl.AddText(new Vector2(x + (barW - ts.X) / 2, pos.Y + chartH + 4 * s), Theme.Theme.U32(Theme.Theme.TertiaryLabel), label);
            }
        }
        ImGui.SetCursorScreenPos(pos + new Vector2(0, height));
        ImGui.Dummy(new Vector2(width, 0));
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
        var circle = 44f;
        var rowTop = ImGui.GetCursorPosY();

        if (!running && !paused)
        {
            if (Widgets.CircleButton("##start", Theme.Icon.Play, circle, Theme.Theme.Accent, Vector4.One, "Start")) Controller.Start();
        }
        else
        {
            if (paused)
            {
                if (Widgets.CircleButton("##resume", Theme.Icon.Play, circle, Theme.Theme.Accent, Vector4.One, "Resume")) Controller.Resume();
            }
            else
            {
                if (Widgets.CircleButton("##pause", Theme.Icon.Pause, circle, Theme.Theme.ControlBgHover, Theme.Theme.Label, "Pause")) Controller.Pause();
            }
            ImGui.SameLine(0, 10 * s);
            if (Widgets.CircleButton("##stop", Theme.Icon.Square, circle, Theme.Theme.WithAlpha(Theme.Theme.Red, 0.22f), Theme.Theme.Red, "Stop")) Controller.Stop();
        }

        // Count field and loop buttons share one frame height, centred on the transport circles.
        // No group here: BeginGroup after SameLine breaks the line tracking the buttons rely on.
        var h = Widgets.FrameHeight;
        ImGui.SameLine(0, 18 * s);
        ImGui.SetCursorPosY(rowTop + (circle * s - h) / 2);
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

        // Keep the row height stable regardless of which control ended the line.
        ImGui.SetCursorPosY(rowTop + circle * s);
    }
}
