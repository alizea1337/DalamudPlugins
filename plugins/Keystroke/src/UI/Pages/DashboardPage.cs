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

    public override string Title => "dashboard";
    public override char Icon => Theme.Icon.LayoutDashboard;

    public override void Draw()
    {
        var s = Widgets.S;
        var c = Controller;
        var state = c.State;
        var (stateColor, stateLabel) = MainWindow.StateBadge(state);

        if (!Services.ClientState.IsLoggedIn)
            Widgets.Notice(Theme.Icon.Info, Theme.Theme.Info, "Log in to a character to get started.");
        else if (!Keybound.IsKeyboundArea((ushort)Services.ClientState.TerritoryType))
            Widgets.Notice(Theme.Icon.Info, Theme.Theme.Info, "Go to Keybound Square in the Gold Saucer; queueing and play start from there.");

        Widgets.BeginColumns(2);

        // ---- left: transport + keycaps
        Widgets.BeginGroup("control");
        Widgets.StatusDot(stateColor, stateLabel, state is AutomationState.Playing or AutomationState.Queued);
        Widgets.Small(c.StatusDetail);
        if (c.LastError != null && state == AutomationState.Error) Widgets.Small(c.LastError, Theme.Theme.Danger);
        ImGui.Dummy(new Vector2(0, 6 * s));
        DrawTransport(state, s);
        ImGui.Dummy(new Vector2(0, 8 * s));

        if (Config.Appearance.Keycaps)
        {
            if (c.CurrentPrompt.Length > 0) Widgets.Keycaps(c.CurrentPrompt, c.CurrentAnswer.Length, 24f);
            else
            {
                Widgets.Keycaps("keystroke", state == AutomationState.Playing ? 9 : 0, 24f);
                Widgets.Small("the word being typed shows here as keycaps");
            }
        }
        else if (c.CurrentPrompt.Length > 0)
        {
            using var b = Widgets.F.Bold.Push();
            Widgets.Text(c.CurrentPrompt, Theme.Theme.AccentText);
        }
        var ms = c.MeasuredMsPerKey;
        if (ms > 0) Widgets.Small($"{ms:0} ms per key · {60000 / ms / 5:0} wpm · {Config.Typing.Profile.ToString().ToLowerInvariant()}{(Config.Typing.InstantMode ? " (instant)" : "")}");
        Widgets.EndGroup();

        Widgets.BeginGroup("loop");
        Widgets.Row("games", 70 * s, () => { if (Widgets.IntField("##count", ref loopCount)) loopCount = Math.Clamp(loopCount, 1, 999); });
        var running = state is AutomationState.Playing or AutomationState.WaitingForDuty or AutomationState.Queued or AutomationState.Finishing or AutomationState.Paused;
        if (running) ImGui.BeginDisabled();
        if (Widgets.Button("run loop##loop", Widgets.ButtonKind.Normal, null, Theme.Icon.Repeat, "queue and play this many games, then stop")) c.StartLoop(loopCount);
        ImGui.SameLine(0, 6 * s);
        if (Widgets.Button("repeat##rep", Widgets.ButtonKind.Normal, null, Theme.Icon.RefreshCw, "queue and play until stopped or a stop condition is met")) c.StartUnlimited();
        if (running) ImGui.EndDisabled();
        if (c.LoopRemaining > 0) Widgets.Small($"{c.LoopRemaining} games left");
        else if (c.LoopRemaining < 0) Widgets.Small("repeating until stopped");
        Widgets.EndGroup();

        // ---- right: session + lifetime
        Widgets.Column(1);
        var st = c.Session;
        Widgets.BeginGroup("this session");
        var half = Widgets.ContentWidth / 2;
        Widgets.Stat("games", st.GamesCompleted.ToString(), null, half);
        ImGui.SameLine();
        Widgets.Stat("mgp", st.MgpGained.ToString("N0"), Theme.Theme.AccentText, half);
        Widgets.Stat("mgc", st.MgcGained.ToString("N0"), Theme.Theme.Info, half);
        ImGui.SameLine();
        Widgets.Stat("games / hour", st.GamesPerHour.ToString("0.0"), null, half);
        Widgets.ValueRow("wpm", st.WordsPerMinute.ToString("0"));
        Widgets.ValueRow("accuracy", (st.Accuracy * 100).ToString("0.0") + "%");
        Widgets.ValueRow("mgp / hour", st.MgpPerHour.ToString("N0"));
        Widgets.ValueRow("since", st.StartedAt.ToString("HH:mm"));
        Widgets.EndGroup();

        var hist = c.History;
        Widgets.BeginGroup("lifetime");
        Widgets.Stat("games", hist.TotalGames.ToString("N0"), null, half);
        ImGui.SameLine();
        Widgets.Stat("mgp", hist.TotalMgp.ToString("N0"), Theme.Theme.AccentText, half);
        Widgets.Stat("mgc", hist.TotalMgc.ToString("N0"), Theme.Theme.Info, half);
        ImGui.SameLine();
        Widgets.Stat("best wpm", hist.BestWpm.ToString("0"), null, half);
        ImGui.Dummy(new Vector2(0, 4 * s));
        DrawHistoryChart(hist, s);
        Widgets.EndGroup();

        Widgets.EndColumns();
    }

    private void DrawTransport(AutomationState state, float s)
    {
        var running = state is AutomationState.Playing or AutomationState.WaitingForDuty or AutomationState.Queued or AutomationState.Finishing;
        var paused = state == AutomationState.Paused;
        if (!running && !paused)
        {
            if (Widgets.Button("start##start", Widgets.ButtonKind.Accent, new Vector2(96 * s, 24 * s), Theme.Icon.Play, "play one game (queues first from the lobby)")) Controller.Start();
        }
        else
        {
            if (paused)
            {
                if (Widgets.Button("resume##resume", Widgets.ButtonKind.Accent, new Vector2(96 * s, 24 * s), Theme.Icon.Play)) Controller.Resume();
            }
            else
            {
                if (Widgets.Button("pause##pause", Widgets.ButtonKind.Normal, new Vector2(96 * s, 24 * s), Theme.Icon.Pause)) Controller.Pause();
            }
            ImGui.SameLine(0, 6 * s);
            if (Widgets.Button("stop##stop", Widgets.ButtonKind.Danger, new Vector2(80 * s, 24 * s), Theme.Icon.Square)) Controller.Stop();
        }
    }

    /// <summary>MGP per day for the last 14 days as flat bars inside an inset well.</summary>
    private static void DrawHistoryChart(Rewards.RewardStore hist, float s)
    {
        var days = hist.LastDays(14);
        var max = Math.Max(1, days.Max(d => d.Mgp));
        var width = Widgets.ContentWidth;
        var chartH = 64 * s;
        var dl = ImGui.GetWindowDrawList();

        Widgets.Small("mgp per day, last 14 days");
        var pos = ImGui.GetCursorScreenPos();
        Theme.Theme.DrawInset(dl, pos, pos + new Vector2(width, chartH), s);
        var inner = pos + new Vector2(4 * s);
        var innerW = width - 8 * s;
        var innerH = chartH - 8 * s;
        var gap = 2 * s;
        var barW = (innerW - gap * (days.Count - 1)) / days.Count;

        for (var i = 0; i < days.Count; i++)
        {
            var d = days[i];
            var x = inner.X + i * (barW + gap);
            var h = (float)(d.Mgp / (double)max) * innerH;
            var isToday = i == days.Count - 1;
            var top = new Vector2(x, inner.Y + innerH - h);
            var bottom = new Vector2(x + barW, inner.Y + innerH);
            if (h < 2 * s) top.Y = bottom.Y - 2 * s;
            var col = d.Mgp > 0 ? (isToday ? Theme.Theme.AccentColor : Theme.Theme.AccentDark) : Theme.Theme.BevelMid;
            dl.AddRectFilled(top, bottom, Theme.Theme.U32(col));
            if (isToday && d.Mgp > 0 && Theme.Theme.Effects) Theme.Theme.DrawGlow(dl, top, bottom, Theme.Theme.AccentColor, 4 * s, 0.5f);

            ImGui.SetCursorScreenPos(new Vector2(x, inner.Y));
            ImGui.InvisibleButton("##bar" + i, new Vector2(barW, innerH));
            if (ImGui.IsItemHovered())
                Widgets.Tooltip($"{d.Day:ddd d MMM}\n{d.Games} games · {d.Mgp:N0} mgp · {d.Mgc:N0} mgc");
        }
        ImGui.SetCursorScreenPos(new Vector2(pos.X, pos.Y + chartH));
        ImGui.Dummy(new Vector2(width, 2 * s));
    }
}
