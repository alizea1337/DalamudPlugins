using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class OverlayPage : Page
{
    public OverlayPage(Plugin plugin) : base(plugin) { }

    public override string Title => "overlay";
    public override char Icon => Theme.Icon.Eye;

    public override void Draw()
    {
        var o = Config.Overlay;
        var changed = false;

        Widgets.BeginColumns(2);
        Widgets.BeginGroup("overlay");
        var v = o.Enabled;
        if (Widgets.Check("show overlay", ref v)) { o.Enabled = v; changed = true; }
        v = o.OnlyInKeyboundAreas;
        if (Widgets.Check("only in keybound square and the duty", ref v, "Otherwise it is shown whenever you are logged in.")) { o.OnlyInKeyboundAreas = v; changed = true; }
        v = o.Locked;
        if (Widgets.Check("lock position", ref v)) { o.Locked = v; changed = true; }
        v = o.ClickThroughWhenLocked;
        if (Widgets.Check("click through when locked", ref v)) { o.ClickThroughWhenLocked = v; changed = true; }
        var op = (int)System.Math.Round(o.Opacity * 100);
        if (Widgets.Slider("opacity", ref op, 20, 100, "{0} %")) { o.Opacity = op / 100f; changed = true; }
        Widgets.EndGroup();

        Widgets.Column(1);
        Widgets.BeginGroup("content");
        v = o.ShowCurrentAnswer;
        if (Widgets.Check("current word as keycaps", ref v)) { o.ShowCurrentAnswer = v; changed = true; }
        v = o.ShowSessionStats;
        if (Widgets.Check("session stats", ref v, "Games, mgp and mgc this session.")) { o.ShowSessionStats = v; changed = true; }
        Widgets.EndGroup();
        Widgets.EndColumns();

        if (changed) Save();
    }
}
