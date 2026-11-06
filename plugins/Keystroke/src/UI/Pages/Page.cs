using Keystroke.Automation;

namespace Keystroke.UI.Pages;

/// <summary>One sidebar destination. Pages draw into the content column; the window owns chrome.</summary>
internal abstract class Page
{
    protected readonly Plugin Plugin;
    protected Configuration Config => Plugin.Config;
    protected IAutomationController Controller => Plugin.Controller;

    protected Page(Plugin plugin) => Plugin = plugin;

    public abstract string Title { get; }
    public abstract char Icon { get; }
    public abstract void Draw();

    /// <summary>Persist settings; pages call this after any change so the config is never stale.</summary>
    protected void Save() => Config.Save();
}
