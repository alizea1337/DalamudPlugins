using System.Numerics;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI;

/// <summary>One-time confirmation before Instant mode is enabled; shared by the Typing page and the compact strip.</summary>
internal static class InstantModeSheet
{
    private static bool pending;

    public static void Request(TypingSettings t, Configuration config)
    {
        if (t.InstantModeAcknowledged) { t.InstantMode = true; config.Save(); }
        else pending = true;
    }

    public static void Draw(Configuration config)
    {
        if (!pending) return;
        var t = config.Typing;
        var s = Widgets.S;

        ImGui.OpenPopup("##instantsheet");
        ImGui.SetNextWindowSize(new Vector2(400 * s, 0));
        ImGui.SetNextWindowPos(ImGui.GetMainViewport().GetCenter(), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(18, 16) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleColor(ImGuiCol.PopupBg, Vector4.Zero);
        if (ImGui.BeginPopupModal("##instantsheet", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove))
        {
            var dl = ImGui.GetWindowDrawList();
            var pos = ImGui.GetWindowPos();
            var size = ImGui.GetWindowSize();
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);

            Widgets.Icon(Theme.Icon.TriangleAlert, Theme.Theme.Warn, true);
            ImGui.SameLine(0, 8 * s);
            using (Widgets.F.Title.Push()) Widgets.Text("enable instant mode?", Theme.Theme.TextBright);
            ImGui.Dummy(new Vector2(0, 6 * s));
            Widgets.Wrapped("Instant mode sends every key of the word in one frame and submits it. It is far faster than any human and easy to notice for other players. Leave it off unless you play in a premade group that doesn't mind.", Theme.Theme.Text, size.X - 36 * s);
            ImGui.Dummy(new Vector2(0, 12 * s));

            var bw = (size.X - 36 * s - 8 * s) / 2;
            if (Widgets.Button("cancel##cancel", Widgets.ButtonKind.Normal, new Vector2(bw, 24 * s)))
            {
                pending = false;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine(0, 8 * s);
            if (Widgets.Button("enable instant mode##ok", Widgets.ButtonKind.Accent, new Vector2(bw, 24 * s)))
            {
                t.InstantMode = true;
                t.InstantModeAcknowledged = true;
                config.Save();
                pending = false;
                ImGui.CloseCurrentPopup();
            }

            dl.ChannelsSetCurrent(0);
            Theme.Theme.DrawFrame(dl, pos, pos + size, s);
            var inset = Theme.Theme.FrameInset * s;
            Theme.Theme.DrawStripe(dl, pos + new Vector2(inset, inset), size.X - inset * 2, Theme.Theme.StripeHeight * s);
            dl.ChannelsMerge();
            ImGui.EndPopup();
        }
        ImGui.PopStyleColor();
        ImGui.PopStyleVar(2);
    }
}
