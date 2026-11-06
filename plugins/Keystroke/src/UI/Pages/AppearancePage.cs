using System;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class AppearancePage : Page
{
    public AppearancePage(Plugin plugin) : base(plugin) { }

    public override string Title => Strings.Appearance;
    public override char Icon => Theme.Icon.Sparkles;

    public override void Draw()
    {
        var a = Config.Appearance;
        var changed = false;
        var s = Widgets.Scale;

        Widgets.PageHeader(Strings.Appearance, "Keystroke is always dark. Glass lets the game show through.");

        Widgets.SectionLabel("GLASS");
        if (Widgets.BeginCard("glass"))
        {
            var v = a.Glass;
            if (Widgets.ToggleRow("Glass", ref v, "Translucent window and sidebar, like macOS vibrancy. Dalamud can't blur what's behind a window, so this is tint without blur.", Theme.Icon.Layers)) { a.Glass = v; changed = true; }
            if (a.Glass)
            {
                Widgets.RowSeparator();
                var t = (int)Math.Round(a.Transparency * 100);
                Widgets.Row("Transparency", "Higher values show more of the game.", 220 * s, Widgets.FrameHeight,
                    () => { if (Widgets.SliderInt("##t", ref t, 0, 100, "%d %%", 220)) { a.Transparency = t / 100f; changed = true; } });
                Widgets.RowSeparator();
                v = a.ContrastCards;
                if (Widgets.ToggleRow("Keep cards readable", ref v, "Cards stay more opaque than the window so rows don't blend into busy scenes.", Theme.Icon.Eye)) { a.ContrastCards = v; changed = true; }
            }
            Widgets.EndCard();
        }

        if (changed)
        {
            Theme.Theme.Configure(a);
            Save();
        }
    }
}
