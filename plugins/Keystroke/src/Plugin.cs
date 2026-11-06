using System;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Keystroke.Automation;
using Keystroke.Input;
using Keystroke.UI;
using Keystroke.UI.Pages;
using Keystroke.UI.Theme;

namespace Keystroke;

/// <summary>Plugin entry point: wires services, windows and the per-frame tick. Logic lives elsewhere.</summary>
public sealed class Plugin : IDalamudPlugin
{
    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    internal Configuration Config { get; private set; }
    internal IAutomationController Controller { get; }
    internal HotkeyService Hotkeys { get; }
    internal InspectorWindow Inspector { get; }
    internal MainWindow Main { get; }

    private readonly WindowSystem windows = new("Keystroke");
    private readonly Fonts fonts;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Services>();
        Config = Configuration.Load();
        Theme.Configure(Config.Appearance);

        fonts = new Fonts();
        Widgets.Init(fonts);

        Controller = new StubAutomationController();
        Hotkeys = new HotkeyService(() => Config);
        Hotkeys.StartStop += () => { if (Controller.State == AutomationState.Idle) Controller.Start(); else Controller.Stop(); };
        Hotkeys.PauseResume += () => { if (Controller.State == AutomationState.Paused) Controller.Resume(); else Controller.Pause(); };
        Hotkeys.ToggleOverlay += () => { Config.Overlay.Enabled = !Config.Overlay.Enabled; Config.Save(); };
        Hotkeys.OpenWindow += () => Main!.Toggle();

        Inspector = new InspectorWindow(Config);
        Main = new MainWindow(this, new Page[]
        {
            new DashboardPage(this),
            new AutomationPage(this),
            new TypingPage(this),
            new RewardsAlertsPage(this),
            new HotkeysPage(this),
            new OverlayPage(this),
            new AppearancePage(this),
            new AdvancedPage(this),
            new AboutPage(this),
        });
        windows.AddWindow(Main);
        windows.AddWindow(Inspector);

        Services.Commands.AddHandler(Strings.Command, new CommandInfo(OnCommand) { HelpMessage = Strings.CommandHelp });
        pluginInterface.UiBuilder.Draw += windows.Draw;
        pluginInterface.UiBuilder.OpenMainUi += () => Main.IsOpen = true;
        pluginInterface.UiBuilder.OpenConfigUi += () => { Main.IsOpen = true; Main.Select<AutomationPage>(); };
        Services.Framework.Update += OnUpdate;

        Services.Log.Information("Keystroke {Version} loaded", Version);
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "inspector":
            case "inspect":
                Inspector.Toggle();
                break;
            case "start":
                Controller.Start();
                break;
            case "stop":
                Controller.Stop();
                break;
            default:
                Main.Toggle();
                break;
        }
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        try
        {
            Hotkeys.Tick();
            Controller.Tick();
            Inspector.Tick();
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Framework tick failed");
        }
    }

    internal void ResetConfiguration()
    {
        Config = new Configuration();
        Theme.Configure(Config.Appearance);
        Config.Save();
        Services.Log.Information("Configuration reset to defaults");
    }

    public void Dispose()
    {
        Services.Framework.Update -= OnUpdate;
        Services.PluginInterface.UiBuilder.Draw -= windows.Draw;
        Services.Commands.RemoveHandler(Strings.Command);
        Config.Save();
        windows.RemoveAllWindows();
        fonts.Dispose();
    }
}
