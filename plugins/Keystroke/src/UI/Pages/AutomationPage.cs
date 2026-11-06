using System;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class AutomationPage : Page
{
    public AutomationPage(Plugin plugin) : base(plugin) { }

    public override string Title => Strings.Automation;
    public override char Icon => Theme.Icon.Repeat;

    public override void Draw()
    {
        var a = Config.Automation;
        var changed = false;

        Widgets.PageHeader(Strings.Automation, "What Keystroke does on its own when you enter the Gold Saucer.");

        Widgets.SectionLabel("PLAY");
        if (Widgets.BeginCard("play"))
        {
            var v = a.AutoPlayOnEntry;
            if (Widgets.ToggleRow("Start typing when the duty begins", ref v, "Typing starts as soon as the Keybound Brawler duty loads. Otherwise press Start on the Dashboard.", Theme.Icon.Play)) { a.AutoPlayOnEntry = v; changed = true; }
            Widgets.RowSeparator();
            v = a.AutoLeaveAfterResult;
            if (Widgets.ToggleRow("Leave the duty after the result screen", ref v, null, Theme.Icon.DoorOpen)) { a.AutoLeaveAfterResult = v; changed = true; }
            Widgets.EndCard();
        }

        Widgets.SectionLabel("QUEUE");
        if (Widgets.BeginCard("queue"))
        {
            var v = a.AutoQueueOnLobbyEntry;
            if (Widgets.ToggleRow("Auto-queue when entering the lobby", ref v, "Talks to the Keybound Registrar and queues again after each game until you stop it.", Theme.Icon.RefreshCw)) { a.AutoQueueOnLobbyEntry = v; changed = true; }
            Widgets.RowSeparator();
            v = a.AutoCommenceParty;
            if (Widgets.ToggleRow("Commence automatically when in a party", ref v, "Accepts the duty pop for every party member, including cross-world parties.", Theme.Icon.Users)) { a.AutoCommenceParty = v; changed = true; }
            Widgets.EndCard();
        }

        var sc = a.StopConditions;
        Widgets.SectionLabel("STOP CONDITIONS");
        Widgets.Caption("When a condition is met, Keystroke finishes the current game, leaves, and does not queue again.");
        if (Widgets.BeginCard("stop"))
        {
            var v = sc.StopAfterGamesEnabled;
            if (Widgets.ToggleRow("Stop after a number of games", ref v, null, Theme.Icon.Flag)) { sc.StopAfterGamesEnabled = v; changed = true; }
            if (sc.StopAfterGamesEnabled)
            {
                var n = sc.StopAfterGames;
                Widgets.Row("Games", null, 120 * Widgets.Scale, Widgets.FrameHeight, () => { if (Widgets.InputInt("##games", ref n)) { sc.StopAfterGames = Math.Max(1, n); changed = true; } });
            }
            Widgets.RowSeparator();

            v = sc.StopAtMgpEnabled;
            if (Widgets.ToggleRow("Stop at an MGP target", ref v, null, Theme.Icon.Coins)) { sc.StopAtMgpEnabled = v; changed = true; }
            if (sc.StopAtMgpEnabled)
            {
                var n = sc.StopAtMgp;
                Widgets.Row("MGP", null, 160 * Widgets.Scale, Widgets.FrameHeight, () => { if (Widgets.InputInt("##mgp", ref n, 10_000, 160)) { sc.StopAtMgp = Math.Max(0, n); changed = true; } });
            }
            Widgets.RowSeparator();

            v = sc.StopAtTimeEnabled;
            if (Widgets.ToggleRow("Stop at a time of day", ref v, "Local time on this PC.", Theme.Icon.Clock)) { sc.StopAtTimeEnabled = v; changed = true; }
            if (sc.StopAtTimeEnabled)
            {
                var h = sc.StopAtHour;
                var m = sc.StopAtMinute;
                Widgets.Row("Time", null, 170 * Widgets.Scale, Widgets.FrameHeight, () =>
                {
                    ImGui.SetNextItemWidth(80 * Widgets.Scale);
                    if (ImGui.InputInt("##h", ref h, 1, 1)) { sc.StopAtHour = Math.Clamp(h, 0, 23); changed = true; }
                    ImGui.SameLine(0, 4 * Widgets.Scale);
                    ImGui.TextUnformatted(":");
                    ImGui.SameLine(0, 4 * Widgets.Scale);
                    ImGui.SetNextItemWidth(80 * Widgets.Scale);
                    if (ImGui.InputInt("##m", ref m, 5, 5)) { sc.StopAtMinute = Math.Clamp(m, 0, 59); changed = true; }
                });
            }
            Widgets.EndCard();
        }

        Widgets.SectionLabel("KEYBOUND COFFERS");
        if (Widgets.BeginCard("coffers"))
        {
            var coffers = Controller.Coffers;
            var carried = coffers.Count();
            var desc = coffers.Running ? coffers.Status : coffers.Status.Length > 0 ? coffers.Status : $"{carried} carried. Opens them one at a time while you are out of combat. Never starts on its own.";
            Widgets.Row("Open carried Keybound Coffers", desc, 110 * Widgets.Scale, Widgets.FrameHeight, () =>
            {
                if (coffers.Running)
                {
                    if (Widgets.Button("Stop##coffers", Widgets.ButtonStyle.Destructive, null, Theme.Icon.Square)) coffers.Stop("Stopped");
                }
                else
                {
                    if (carried == 0) ImGui.BeginDisabled();
                    if (Widgets.Button("Open all##coffers", Widgets.ButtonStyle.Default, null, Theme.Icon.PackageOpen)) coffers.Start();
                    if (carried == 0) ImGui.EndDisabled();
                }
            }, Theme.Icon.Gift);
            Widgets.EndCard();
        }

        if (changed) Save();
    }
}
