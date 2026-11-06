using System;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class RewardsAlertsPage : Page
{
    public RewardsAlertsPage(Plugin plugin) : base(plugin) { }

    public override string Title => Strings.RewardsAlerts;
    public override char Icon => Theme.Icon.Bell;

    public override void Draw()
    {
        var a = Config.Alerts;
        var changed = false;
        var s = Widgets.Scale;

        Widgets.PageHeader(Strings.RewardsAlerts, "Currency caps and how Keystroke gets your attention.");

        Widgets.SectionLabel("MGC CAP");
        if (Widgets.BeginCard("mgc"))
        {
            var v = a.MgcCapWarning;
            if (Widgets.ToggleRow("Warn when MGC approaches the cap", ref v, "Keybound currency stops accumulating at the cap; spend it before then.", Theme.Icon.Coins, Theme.Theme.Purple)) { a.MgcCapWarning = v; changed = true; }
            if (a.MgcCapWarning)
            {
                var pct = a.MgcCapWarningPercent;
                Widgets.Row("Warn at", null, 220 * s, Widgets.FrameHeight,
                    () => { if (Widgets.SliderInt("##pct", ref pct, 50, 100, "%d %% of cap", 220)) { a.MgcCapWarningPercent = pct; changed = true; } });
            }
            Widgets.EndCard();
        }

        Widgets.SectionLabel("CHANNELS");
        if (Widgets.BeginCard("channels"))
        {
            var v = a.ToastEnabled;
            if (Widgets.ToggleRow("In-game toast", ref v, null, Theme.Icon.Bell)) { a.ToastEnabled = v; changed = true; }
            Widgets.RowSeparator();
            v = a.SoundEnabled;
            if (Widgets.ToggleRow("Sound effect", ref v, null, Theme.Icon.Volume2)) { a.SoundEnabled = v; changed = true; }
            if (a.SoundEnabled)
            {
                var se = a.SoundEffect;
                Widgets.Row("Sound", "Game sound effects 1–16, as in <se.N> chat macros.", 160 * s, Widgets.FrameHeight,
                    () => { if (Widgets.SliderInt("##se", ref se, 1, 16, "<se.%d>", 160)) { a.SoundEffect = se; changed = true; } });
            }
            Widgets.RowSeparator();
            v = a.ChatEnabled;
            if (Widgets.ToggleRow("Chat message", ref v, null, Theme.Icon.Type)) { a.ChatEnabled = v; changed = true; }
            Widgets.RowSeparator();
            v = a.WindowsToastEnabled;
            if (Widgets.ToggleRow("Windows notification when the game is in the background", ref v, null, Theme.Icon.Monitor)) { a.WindowsToastEnabled = v; changed = true; }
            Widgets.EndCard();
        }

        Widgets.SectionLabel("EVENTS");
        if (Widgets.BeginCard("events"))
        {
            var v = a.NotifyLoopFinished;
            if (Widgets.ToggleRow("Loop finished or stop condition reached", ref v, null, Theme.Icon.Flag)) { a.NotifyLoopFinished = v; changed = true; }
            Widgets.RowSeparator();
            v = a.NotifyMgcWarning;
            if (Widgets.ToggleRow("MGC warning and cap", ref v, null, Theme.Icon.TriangleAlert)) { a.NotifyMgcWarning = v; changed = true; }
            Widgets.RowSeparator();
            v = a.NotifyErrors;
            if (Widgets.ToggleRow("Automation stopped on an error", ref v, null, Theme.Icon.OctagonX)) { a.NotifyErrors = v; changed = true; }
            Widgets.RowSeparator();
            v = a.NotifyGameResult;
            if (Widgets.ToggleRow("Every game result", ref v, "Noisy; off by default.", Theme.Icon.Trophy)) { a.NotifyGameResult = v; changed = true; }
            Widgets.EndCard();
        }

        if (changed) Save();
    }
}
