using System.Diagnostics;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class AdvancedPage : Page
{
    public AdvancedPage(Plugin plugin) : base(plugin) { }

    public override string Title => Strings.Advanced;
    public override char Icon => Theme.Icon.SlidersHorizontal;

    public override void Draw()
    {
        var a = Config.Advanced;
        var changed = false;
        var s = Widgets.Scale;

        Widgets.PageHeader(Strings.Advanced, "Recovery, diagnostics and the Inspector.");

        Widgets.SectionLabel("RELIABILITY");
        if (Widgets.BeginCard("rel"))
        {
            var v = a.AutomaticRecovery;
            if (Widgets.ToggleRow("Automatic recovery", ref v, "If a key fails to reach the game, wait for input to settle and retry instead of stopping.", Theme.Icon.RotateCcw)) { a.AutomaticRecovery = v; changed = true; }
            Widgets.RowSeparator();
            v = a.KeepActivityTimersAlive;
            if (Widgets.ToggleRow("Keep the activity timers alive", ref v, "Prevents the duty's inactivity kick while automation is playing. No keys are sent for this.", Theme.Icon.Hourglass)) { a.KeepActivityTimersAlive = v; changed = true; }
            Widgets.EndCard();
        }

        Widgets.SectionLabel("DIAGNOSTICS");
        if (Widgets.BeginCard("diag"))
        {
            var v = a.InspectorEnabled;
            if (Widgets.ToggleRow("Inspector", ref v, "Live view of the Keybound Brawler addons, arrays and casts. Use 'Copy snapshot' when reporting a problem.", Theme.Icon.Search))
            {
                a.InspectorEnabled = v;
                Plugin.Inspector.IsOpen = v;
                changed = true;
            }
            if (a.InspectorEnabled)
            {
                Widgets.Row("Inspector window", null, 110 * s, Widgets.FrameHeight, () =>
                {
                    if (Widgets.Button("Open##insp", Widgets.ButtonStyle.Default, null, Theme.Icon.ExternalLink)) Plugin.Inspector.IsOpen = true;
                });
            }
            Widgets.RowSeparator();
            v = a.VerboseLogging;
            if (Widgets.ToggleRow("Verbose logging", ref v, "Writes every typing decision to the Dalamud log (/xllog).", Theme.Icon.Bug)) { a.VerboseLogging = v; changed = true; }
            Widgets.RowSeparator();
            Widgets.Row("Data folder", Services.PluginInterface.GetPluginConfigDirectory(), 110 * s, Widgets.FrameHeight, () =>
            {
                if (Widgets.Button("Reveal##cfg", Widgets.ButtonStyle.Default, null, Theme.Icon.FolderOpen))
                    Process.Start(new ProcessStartInfo("explorer.exe", Services.PluginInterface.GetPluginConfigDirectory()) { UseShellExecute = true });
            }, Theme.Icon.FolderOpen);
            Widgets.EndCard();
        }

        Widgets.SectionLabel("RESET");
        if (Widgets.BeginCard("reset"))
        {
            Widgets.Row("Reset all settings", "Restores every setting to its default. Session stats are kept.", 110 * s, Widgets.FrameHeight, () =>
            {
                if (Widgets.Button("Reset##all", Widgets.ButtonStyle.Destructive)) Plugin.ResetConfiguration();
            }, Theme.Icon.TriangleAlert, Theme.Theme.Red);
            Widgets.EndCard();
        }

        if (changed) Save();
    }
}
