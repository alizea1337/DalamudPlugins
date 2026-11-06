using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI;

/// <summary>
/// One-time confirmation before Instant mode is enabled. Shared by the Typing page and the
/// compact window. Call <see cref="Request"/> to enable (shows the sheet if not yet acknowledged)
/// and <see cref="Draw"/> every frame from a window that may host the popup.
/// </summary>
internal static class InstantModeSheet
{
    private static bool pending;

    /// <summary>Enables Instant mode, asking first if the user hasn't acknowledged the warning.</summary>
    public static void Request(TypingSettings t, Configuration config)
    {
        if (t.InstantModeAcknowledged)
        {
            t.InstantMode = true;
            config.Save();
        }
        else
        {
            pending = true;
        }
    }

    public static void Draw(Configuration config)
    {
        if (!pending) return;
        var t = config.Typing;
        var s = Widgets.Scale;

        ImGui.OpenPopup("##instantsheet");
        ImGui.SetNextWindowSize(new Vector2(420 * s, 0));
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(22, 20) * s);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Theme.Theme.Rgb(0x2C, 0x2C, 0x2E));
        if (ImGui.BeginPopupModal("##instantsheet", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove))
        {
            ImGui.SetCursorPosX((ImGui.GetWindowWidth() - Widgets.IconSize(Icon.TriangleAlert, true).X) / 2);
            Widgets.Icon(Icon.TriangleAlert, Theme.Theme.Orange, true);
            ImGui.Dummy(new Vector2(0, 6 * s));
            using (Widgets.F.BodySemibold.Push())
            {
                var w = ImGui.CalcTextSize(Strings.InstantModeTitle).X;
                ImGui.SetCursorPosX((ImGui.GetWindowWidth() - w) / 2);
                ImGui.TextUnformatted(Strings.InstantModeTitle);
            }
            ImGui.Dummy(new Vector2(0, 6 * s));
            ImGui.PushTextWrapPos();
            Widgets.Secondary(Strings.InstantModeBody);
            ImGui.PopTextWrapPos();
            ImGui.Dummy(new Vector2(0, 14 * s));

            var bw = (ImGui.GetContentRegionAvail().X - 8 * s) / 2;
            if (Widgets.Button(Strings.Cancel + "##cancel", Widgets.ButtonStyle.Default, new Vector2(bw, 30 * s)))
            {
                pending = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(0, 8 * s);
            if (Widgets.Button(Strings.InstantModeConfirm + "##ok", Widgets.ButtonStyle.Primary, new Vector2(bw, 30 * s)))
            {
                t.InstantMode = true;
                t.InstantModeAcknowledged = true;
                config.Save();
                pending = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        ImGui.PopStyleColor();
        ImGui.PopStyleVar();
    }
}
