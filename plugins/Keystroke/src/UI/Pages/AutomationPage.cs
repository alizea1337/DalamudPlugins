using System;
using Dalamud.Bindings.ImGui;
using Keystroke.UI.Theme;

namespace Keystroke.UI.Pages;

internal sealed class AutomationPage : Page
{
    public AutomationPage(Plugin plugin) : base(plugin) { }

    public override string Title => "automation";
    public override char Icon => Theme.Icon.Repeat;

    public override void Draw()
    {
        var a = Config.Automation;
        var sc = a.StopConditions;
        var changed = false;
        var s = Widgets.S;

        Widgets.BeginColumns(2);

        Widgets.BeginGroup("play");
        var v = a.AutoPlayOnEntry;
        if (Widgets.Check("start typing when the duty begins", ref v, "Otherwise press start on the dashboard once inside.")) { a.AutoPlayOnEntry = v; changed = true; }
        v = a.AutoLeaveAfterResult;
        if (Widgets.Check("leave after the result screen", ref v)) { a.AutoLeaveAfterResult = v; changed = true; }
        Widgets.EndGroup();

        Widgets.BeginGroup("queue");
        v = a.AutoQueueOnLobbyEntry;
        if (Widgets.Check("auto-queue in keybound square", ref v, "Registers with the Keybound Registrar and queues again after every game until you press stop.")) { a.AutoQueueOnLobbyEntry = v; changed = true; }
        v = a.AutoCommenceParty;
        if (Widgets.Check("commence while in a party", ref v, "Accepts the duty pop even when you are not the party leader.")) { a.AutoCommenceParty = v; changed = true; }
        Widgets.EndGroup();

        Widgets.BeginGroup("keybound coffers");
        var coffers = Controller.Coffers;
        var carried = coffers.Count();
        Widgets.Small(coffers.Running ? coffers.Status : coffers.Status.Length > 0 ? coffers.Status : $"{carried} carried");
        ImGui.Dummy(new System.Numerics.Vector2(0, 4 * s));
        if (coffers.Running)
        {
            if (Widgets.Button("stop##coffers", Widgets.ButtonKind.Danger, null, Theme.Icon.Square)) coffers.Stop("Stopped");
        }
        else
        {
            if (carried == 0) ImGui.BeginDisabled();
            if (Widgets.Button("open all##coffers", Widgets.ButtonKind.Normal, null, Theme.Icon.PackageOpen, "Opens them one at a time while out of combat. Never starts on its own.")) coffers.Start();
            if (carried == 0) ImGui.EndDisabled();
        }
        Widgets.EndGroup();

        Widgets.Column(1);

        Widgets.BeginGroup("stop conditions");
        Widgets.Small("When one is met the current game finishes, you leave, and nothing re-queues.");
        ImGui.Dummy(new System.Numerics.Vector2(0, 4 * s));
        v = sc.StopAfterGamesEnabled;
        if (Widgets.Check("after a number of games", ref v)) { sc.StopAfterGamesEnabled = v; changed = true; }
        if (sc.StopAfterGamesEnabled)
        {
            var n = sc.StopAfterGames;
            Widgets.Row("games", 70 * s, () => { if (Widgets.IntField("##games", ref n)) { sc.StopAfterGames = Math.Max(1, n); changed = true; } });
        }
        v = sc.StopAtMgpEnabled;
        if (Widgets.Check("at an mgp target", ref v)) { sc.StopAtMgpEnabled = v; changed = true; }
        if (sc.StopAtMgpEnabled)
        {
            var n = sc.StopAtMgp;
            Widgets.Row("mgp", 110 * s, () => { if (Widgets.IntField("##mgp", ref n, 110 * s, 10_000)) { sc.StopAtMgp = Math.Max(0, n); changed = true; } });
        }
        v = sc.StopAtTimeEnabled;
        if (Widgets.Check("at a time of day", ref v, "Local time on this PC.")) { sc.StopAtTimeEnabled = v; changed = true; }
        if (sc.StopAtTimeEnabled)
        {
            var h = sc.StopAtHour;
            var m = sc.StopAtMinute;
            Widgets.Row("time", 124 * s, () =>
            {
                if (Widgets.IntField("##h", ref h, 58 * s)) { sc.StopAtHour = Math.Clamp(h, 0, 23); changed = true; }
                ImGui.SameLine(0, 4 * s);
                Widgets.Text(":");
                ImGui.SameLine(0, 4 * s);
                if (Widgets.IntField("##m", ref m, 58 * s)) { sc.StopAtMinute = Math.Clamp(m, 0, 59); changed = true; }
            });
        }
        Widgets.EndGroup();

        Widgets.EndColumns();
        if (changed) Save();
    }
}
