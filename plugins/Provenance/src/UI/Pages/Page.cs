using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface;
using Provenance.Data;

namespace Provenance.UI.Pages;

/// <summary>A button in the sticky action bar at the bottom of the window.</summary>
internal sealed record FooterAction(string Label, Ui.Kind Kind, FontAwesomeIcon? Icon, Action OnClick, bool Enabled = true, string? Tooltip = null);

/// <summary>One tab. Pages draw into the scrolling content area; the window owns header and action bar.</summary>
internal abstract class Page
{
    protected readonly Plugin Plugin;

    protected Page(Plugin plugin) => Plugin = plugin;

    protected Configuration Config => Plugin.Config;
    protected PlanService Plans => Plugin.Plans;

    public abstract string Title { get; }
    public abstract void Draw();

    /// <summary>Short line on the left of the action bar.</summary>
    public virtual (string Text, Vector4 Color)? Status => null;

    /// <summary>Buttons on the right of the action bar, primary last.</summary>
    public virtual IEnumerable<FooterAction> Actions => Array.Empty<FooterAction>();

    /// <summary>Small count drawn on the tab (for example items still missing).</summary>
    public virtual (int Count, Vector4 Color)? Badge => null;

    protected void Go<T>() where T : Page => Plugin.Main.Select<T>();

    protected void Changed() => Plugin.Changed();
}
