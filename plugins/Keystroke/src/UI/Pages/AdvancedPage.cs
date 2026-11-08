using System.Diagnostics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class AdvancedPage : Page
{
    public AdvancedPage(Plugin plugin) : base(plugin) { }

    public override string Title => "advanced";
    public override char Icon => Theme.Icon.SlidersHorizontal;

    public override void Draw()
    {
        var a = Config.Advanced;
        var changed = false;
        var s = Widgets.S;

        Widgets.BeginColumns(2);

        Widgets.BeginGroup("reliability");
        var v = a.AutomaticRecovery;
        if (Widgets.Check("automatic recovery", ref v, "If keys stop reaching the game, wait for input to settle and retry instead of stopping.")) { a.AutomaticRecovery = v; changed = true; }
        v = a.KeepActivityTimersAlive;
        if (Widgets.Check("keep the activity timers alive", ref v, "Prevents the duty's inactivity kick while automation runs. No keys are sent for this.")) { a.KeepActivityTimersAlive = v; changed = true; }
        Widgets.EndGroup();

        Widgets.BeginGroup("reset");
        Widgets.Small("Restores every setting to its default. Session stats and history are kept.");
        ImGui.Dummy(new System.Numerics.Vector2(0, 4 * s));
        if (Widgets.Button("reset all settings##reset", Widgets.ButtonKind.Danger, null, Theme.Icon.TriangleAlert)) Plugin.ResetConfiguration();
        Widgets.EndGroup();

        Widgets.Column(1);

        Widgets.BeginGroup("diagnostics");
        v = a.InspectorEnabled;
        if (Widgets.Check("inspector", ref v, "Live view of the Keybound Brawler addons, arrays and casts. Use 'copy summary' when reporting a problem."))
        {
            a.InspectorEnabled = v;
            Plugin.Inspector.IsOpen = v;
            changed = true;
        }
        if (a.InspectorEnabled)
        {
            if (Widgets.Button("open inspector##insp", Widgets.ButtonKind.Normal, null, Theme.Icon.Search)) Plugin.Inspector.IsOpen = true;
        }
        v = a.VerboseLogging;
        if (Widgets.Check("verbose logging", ref v, "Writes every key decision to the Dalamud log (/xllog).")) { a.VerboseLogging = v; changed = true; }
        ImGui.Dummy(new System.Numerics.Vector2(0, 4 * s));
        if (Widgets.Button("reveal data folder##cfg", Widgets.ButtonKind.Normal, null, Theme.Icon.FolderOpen, Services.PluginInterface.GetPluginConfigDirectory()))
            Process.Start(new ProcessStartInfo("explorer.exe", Services.PluginInterface.GetPluginConfigDirectory()) { UseShellExecute = true });
        Widgets.EndGroup();

        Widgets.EndColumns();
        if (changed) Save();
    }
}
