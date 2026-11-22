using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>
/// A page that's in the sidebar but not built yet (Expeditions: tribes, custom deliveries, the Diadem, ocean fishing):
/// its title and a notice saying so, with what it's planned for. Its sidebar row sits a step quieter and says so on hover.
/// </summary>
internal sealed class PlaceholderPage : Page
{
    private readonly string title;
    private readonly FontAwesomeIcon icon;
    private readonly string plannedFor;

    /// <param name="plannedFor">What the page will do, as the end of "Planned for a later version: …".</param>
    public PlaceholderPage(Plugin plugin, string title, FontAwesomeIcon icon, string plannedFor) : base(plugin)
    {
        this.title = title;
        this.icon = icon;
        this.plannedFor = plannedFor;
    }

    public override string Title => title;
    public override FontAwesomeIcon Icon => icon;
    public override bool Placeholder => true;

    public override void Draw()
    {
        var s = Ui.S;
        var w = MathF.Min(ImGui.GetContentRegionAvail().X, 760 * s);
        Ui.Heading(title);
        ImGui.Dummy(new Vector2(0, 4 * s));
        Ui.BeginCard(Palette.WithAlpha(Palette.Accent, 0.07f), width: w);
        Ui.Icon(FontAwesomeIcon.InfoCircle, Palette.Accent);
        ImGui.SameLine(0, 10 * s);
        ImGui.BeginGroup();
        using (Ui.F.Strong.Push()) Ui.Text("This page is a placeholder");
        Ui.Wrapped($"Planned for a later version: {plannedFor}. Nothing here works yet.", Palette.TextMuted);
        ImGui.EndGroup();
        Ui.EndCard();
    }
}
