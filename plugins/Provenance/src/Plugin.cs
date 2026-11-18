using System;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Provenance.Data;
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
    internal MainWindow Main { get; }

    private readonly WindowSystem windows = new("Provenance");
    private readonly Fonts fonts;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();
        Config = Configuration.Load();
        fonts = new Fonts();
        Ui.Init(fonts);

        Plans = new PlanService(() => Config);
        Main = new MainWindow(this, new Page[]
        {
            new ImportPage(this),
            new BrowsePage(this),
            new PlanPage(this),
            new RunPage(this),
            new CharacterPage(this),
            new SettingsPage(this),
        });
        windows.AddWindow(Main);

        var help = new CommandInfo(OnCommand) { HelpMessage = "Open Provenance. /provenance plan | browse | import | character | settings" };
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
        switch (args.Trim().ToLowerInvariant())
        {
            case "plan": Main.IsOpen = true; Main.Select<PlanPage>(); break;
            case "browse": Main.IsOpen = true; Main.Select<BrowsePage>(); break;
            case "import": Main.IsOpen = true; Main.Select<ImportPage>(); break;
            case "settings": OpenSettings(); break;
            case "character": Main.IsOpen = true; Main.Select<CharacterPage>(); break;
            default: Main.Toggle(); break;
        }
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try
        {
            Plans.Tick();
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
        Plans.Dispose();
        fonts.Dispose();
    }
}
