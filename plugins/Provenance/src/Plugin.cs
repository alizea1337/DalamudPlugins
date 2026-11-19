using System;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Provenance.Data;
using Provenance.Integrations;
using Provenance.Run;
using Provenance.UI;
using Provenance.UI.Pages;
using Provenance.UI.Theme;

namespace Provenance;

/// <summary>Plugin entry point: wires services, the window and the per-frame tick.</summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/provenance";
    private const string Alias = "/prov";

    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    internal Configuration Config { get; }
    internal PlanService Plans { get; }
    internal Raphael Raphael { get; } = new();
    internal RunEngine Runs { get; }
    private readonly Run.Guard guard;
    internal MainWindow Main { get; }

    private readonly WindowSystem windows = new("Provenance");
    private readonly Fonts fonts;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();
        ECommons.ECommonsMain.Init(pluginInterface, this);
        Config = Configuration.Load();
        fonts = new Fonts();
        Ui.Init(fonts);

        Plans = new PlanService(() => Config, Raphael);
        Runs = new RunEngine(this);
        guard = new Run.Guard(this);
        Main = new MainWindow(this, new Page[]
        {
            new ImportPage(this),
            new BrowsePage(this),
            new PlanPage(this),
            new RunPage(this),
            new CharacterPage(this),
            new SettingsPage(this),
            new SecurityPage(this),
        });
        windows.AddWindow(Main);

        var help = new CommandInfo(OnCommand) { HelpMessage = "Open Provenance. /provenance plan | browse | import | character | settings | start | pause | stop | compact | tp <aetheryte> | flag" };
        Services.Commands.AddHandler(Command, help);
        Services.Commands.AddHandler(Alias, new CommandInfo(OnCommand) { ShowInHelp = false });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += OpenMain;
        pluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        Services.Framework.Update += OnUpdate;
        Services.Log.Information("Provenance {Version} loaded", Version);
    }

    private void OpenMain() => Main.IsOpen = true;

    private void OpenSettings()
    {
        Main.IsOpen = true;
        Main.Select<SettingsPage>();
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim();
        var space = trimmed.IndexOf(' ');
        var verb = (space < 0 ? trimmed : trimmed[..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        switch (verb)
        {
            case "start": Runs.Start(); Main.IsOpen = true; Main.Select<RunPage>(); return;
            case "pause": if (Runs.Phase == RunPhase.Paused) Runs.Resume(); else Runs.Pause(); return;
            case "stop": StopCommand(); return;
            case "tp": TeleportCommand(rest); return;
            case "flag": FlagCommand(); return;
            case "compact": Main.IsOpen = true; Main.SetCompact(!Main.Compact); return;
        }
        switch (trimmed.ToLowerInvariant())
        {
            case "plan": Main.IsOpen = true; Main.Select<PlanPage>(); break;
            case "browse": Main.IsOpen = true; Main.Select<BrowsePage>(); break;
            case "import": Main.IsOpen = true; Main.Select<ImportPage>(); break;
            case "settings": OpenSettings(); break;
            case "character": Main.IsOpen = true; Main.Select<CharacterPage>(); break;
            default: Main.Toggle(); break;
        }
    }

    /// <summary>/prov tp &lt;name&gt;: teleports to an unlocked aetheryte with Provenance's own teleport.</summary>
    private static void TeleportCommand(string name)
    {
        if (name.Length == 0) { Services.Chat.Print("[Provenance] Usage: /prov tp <aetheryte name>"); return; }
        var d = Teleporter.Find(name);
        if (d == null) { Services.Chat.PrintError($"[Provenance] No unlocked aetheryte matches \"{name}\"."); return; }
        var why = Teleporter.Teleport(d);
        if (why == null) Services.Chat.Print($"[Provenance] Teleporting to {d.Name} ({d.GilCost:N0} gil).");
        else Services.Chat.PrintError($"[Provenance] Can't teleport to {d.Name}: {why}.");
    }

    /// <summary>/prov stop: ends the run, and any walk Provenance started (for example /prov flag).</summary>
    private void StopCommand()
    {
        var walking = Vnav.Available && Vnav.Busy;
        if (walking) Vnav.Stop();
        if (Runs.Active) Runs.Stop();
        else if (walking) Services.Chat.Print("[Provenance] Stopped moving.");
        else Services.Chat.Print("[Provenance] Nothing to stop.");
    }

    /// <summary>/prov flag: walks (or flies, when airborne) to the map flag with vnavmesh.</summary>
    private static void FlagCommand()
    {
        if (!Vnav.Available) { Services.Chat.PrintError("[Provenance] vnavmesh isn't loaded."); return; }
        if (!Vnav.Ready) { Services.Chat.PrintError("[Provenance] vnavmesh is still building the map for this zone."); return; }
        if (Vnav.FlagPoint is not { } point) { Services.Chat.PrintError("[Provenance] Place a map flag first."); return; }
        var fly = Services.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.InFlight];
        if (Vnav.MoveTo(point, fly)) Services.Chat.Print($"[Provenance] Moving to the flag{(fly ? " by air" : "")}. Type /prov stop to stop.");
        else Services.Chat.PrintError("[Provenance] vnavmesh is already busy with another path.");
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try
        {
            Plans.Tick();
            Runs.Tick();
            guard.Tick();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Framework tick failed");
        }
    }

    /// <summary>Saves the settings and rebuilds the plan; pages call this after any change.</summary>
    internal void Changed()
    {
        Config.Save();
        Plans.Invalidate();
    }

    public void Dispose()
    {
        Services.Framework.Update -= OnUpdate;
        Services.PluginInterface.UiBuilder.Draw -= windows.Draw;
        Services.PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        Services.PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        Services.Commands.RemoveHandler(Command);
        Services.Commands.RemoveHandler(Alias);
        Config.Save();
        windows.RemoveAllWindows();
        Runs.Dispose();
        Plans.Dispose();
        UI.JobIcons.Dispose();
        fonts.Dispose();
        ECommons.ECommonsMain.Dispose();
    }
}
