using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class OverlayPage : Page
{
    public OverlayPage(Plugin plugin) : base(plugin) { }

    public override string Title => Strings.Overlay;
    public override char Icon => Theme.Icon.Eye;

    public override void Draw()
    {
        var o = Config.Overlay;
        var changed = false;
        var s = Widgets.Scale;

        Widgets.PageHeader(Strings.Overlay, "A small status pill that stays on screen while you play.");

        if (Widgets.BeginCard("overlay"))
        {
            var v = o.Enabled;
            if (Widgets.ToggleRow("Show overlay", ref v, null, Theme.Icon.Eye)) { o.Enabled = v; changed = true; }
            Widgets.RowSeparator();
            v = o.OnlyInKeyboundAreas;
            if (Widgets.ToggleRow("Only in the Keybound lobby and duty", ref v, "Otherwise it is shown whenever you are logged in.", Theme.Icon.Target)) { o.OnlyInKeyboundAreas = v; changed = true; }
            Widgets.RowSeparator();
            v = o.Locked;
            if (Widgets.ToggleRow("Lock position", ref v, null, Theme.Icon.Lock)) { o.Locked = v; changed = true; }
            Widgets.RowSeparator();
            v = o.ClickThroughWhenLocked;
            if (Widgets.ToggleRow("Click through when locked", ref v, null, Theme.Icon.Crosshair)) { o.ClickThroughWhenLocked = v; changed = true; }
            Widgets.RowSeparator();
            var op = (int)System.Math.Round(o.Opacity * 100);
            Widgets.Row("Opacity", null, 220 * s, Widgets.FrameHeight,
                () => { if (Widgets.SliderInt("##op", ref op, 20, 100, "%d %%", 220)) { o.Opacity = op / 100f; changed = true; } }, Theme.Icon.Layers);
            Widgets.EndCard();
        }

        Widgets.SectionLabel("CONTENT");
        if (Widgets.BeginCard("content"))
        {
            var v = o.ShowCurrentAnswer;
            if (Widgets.ToggleRow("Current answer", ref v, "The word Keystroke is typing right now.", Theme.Icon.Type)) { o.ShowCurrentAnswer = v; changed = true; }
            Widgets.RowSeparator();
            v = o.ShowSessionStats;
            if (Widgets.ToggleRow("Session stats", ref v, "Games and MGP this session.", Theme.Icon.Activity)) { o.ShowSessionStats = v; changed = true; }
            Widgets.EndCard();
        }

        if (changed) Save();
    }
}
