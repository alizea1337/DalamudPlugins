using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Run;
using Provenance.Integrations;
using Provenance.UI.Pages;
using Provenance.UI.Theme;

namespace Provenance.UI;

/// <summary>
/// The warning at the top of the run pages while another automation plugin is in a run's way right now: Questionable
/// running its quests, or YesAlready and TextAdvance left active because pausing them is switched off (Security tab).
/// Nothing is drawn otherwise. Which warning applies is <see cref="PluginConflicts.Notices"/>.
/// </summary>
internal static class OtherPluginsNotice
{
    /// <param name="width">The card's width, as the page's other cards have it; 0 for the full width.</param>
    /// <param name="startBlockerShown">The page says itself, right below, why a run can't start: that isn't said twice.</param>
    public static void Draw(Plugin plugin, float width = 0, bool startBlockerShown = false)
    {
        var notices = PluginConflicts.Notices(OtherPlugins.State, plugin.Config.Security.PauseOtherPlugins);
        if (notices.Count == 0) return;
        var s = Ui.S;
        foreach (var notice in notices)
        {
            if (startBlockerShown && notice.BlocksStart) continue;
            Ui.BeginCard(Palette.WithAlpha(Palette.Warn, 0.07f), width: width);
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Palette.Warn);
            ImGui.SameLine(0, 10 * s);
            ImGui.BeginGroup();
            using (Ui.F.Strong.Push()) Ui.Text(notice.Title);
            Ui.Wrapped(notice.Text, Palette.TextMuted);
            if (notice.InSettings)
            {
                // The link's text starts 10 px inside its box: moved out so it lines up with the text above.
                ImGui.SetCursorPosX(ImGui.GetCursorPosX() - 10 * s);
                if (Ui.LinkButton("Open Security", Palette.Accent, FontAwesomeIcon.ShieldAlt, id: "##otherpluginssecurity"))
                    plugin.Main.Select<SecurityPage>();
            }
            ImGui.EndGroup();
            Ui.EndCard();
        }
    }
}
