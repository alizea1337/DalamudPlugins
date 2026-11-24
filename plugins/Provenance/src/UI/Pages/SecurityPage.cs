using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Run;
using Provenance.Integrations;
using Provenance.UI.Theme;

namespace Provenance.UI.Pages;

/// <summary>
/// Getting unstuck, trades from other players, other automation plugins (paused, or a run steps aside), the tripwire
/// (being moved, players around), and limits that keep a run from going too far.
/// </summary>
internal sealed class SecurityPage : Page
{
    public SecurityPage(Plugin plugin) : base(plugin) { }

    public override string Title => "Security";
    public override FontAwesomeIcon Icon => FontAwesomeIcon.ShieldAlt;

    public override (string Text, Vector4 Color)? Status => ("Changes are saved as you make them", Palette.TextFaint);

    public override void Draw()
    {
        var s = Ui.S;
        var c = Config.Security;
        var w = MathF.Min(Ui.RemainingWidth(), 720 * s);
        Ui.Heading("Security", "How a run gets itself out of trouble, what it refuses, and where it stops.");

        // Unstuck
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Getting unstuck");
        Ui.Wrapped("Applies to all of Provenance's walking and flying: gathering nodes, vendors, exchanges, bells, monsters and inns.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        var v = c.AdvancedUnstuck;
        if (Ui.Checkbox("Advanced unstuck", ref v, "Stuck once: find another path. Twice: jump and back off. Three times: side-step to a nearby spot. Then teleport to the zone's aetheryte, and only then give up. Off: find another path, teleport, give up."))
        { c.AdvancedUnstuck = v; Save(); }
        Slider("Stuck after", "##stuck", c.StuckSeconds, 3, 30, "%d seconds without progress", x => c.StuckSeconds = x);
        Ui.EndCard();

        // Trades
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Trades");
        Ui.Wrapped("A trade window another player opens is cancelled straight away and noted in the run log and chat, so nothing changes hands while a run moves items around.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        v = c.DeclineTrades;
        if (Ui.Checkbox("Refuse trade requests", ref v)) { c.DeclineTrades = v; Save(); }
        if (c.DeclineTrades)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + 30 * s);
            v = c.TradesOnlyDuringRuns;
            if (Ui.Checkbox("Only while a run is going", ref v)) { c.TradesOnlyDuringRuns = v; Save(); }
        }
        Ui.EndCard();

        // Other plugins: YesAlready and TextAdvance paused while a run works, and what each loaded one is doing
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Other plugins");
        Ui.Wrapped("Plugins that click through the game's windows or play by themselves get in a run's way.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        v = c.PauseOtherPlugins;
        if (Ui.Checkbox(PluginConflicts.PauseSetting, ref v,
                "Both stand aside while a run works and carry on as soon as it pauses, finishes or stops; their own settings stay as they are. "
                + "Off: only YesAlready is paused, and only while custom deliveries are handed in."))
        { c.PauseOtherPlugins = v; Save(); }
        DrawOtherPlugins(c.PauseOtherPlugins);
        Ui.EndCard();

        // Tripwire: being moved and players around
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Tripwire");
        Ui.Wrapped("While a run or the Cosmic loop works, any of these stops everything at once: walking, flying, gathering, fishing and even "
                   + "a craft under way. The chat says what set it off. Your own teleports and your party never count.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        v = c.StopWhenMoved;
        if (Ui.Checkbox("Stop when the character is moved", ref v,
                "Another zone, or far across this one in an instant, with no teleport, aethernet, door or knock-out before it. "
                + "A game master can summon a character, for example."))
        { c.StopWhenMoved = v; Save(); }
        v = c.StopForGameMaster;
        if (Ui.Checkbox("Stop when a game master is around", ref v, "Anyone the game shows as a game master, at any distance."))
        { c.StopForGameMaster = v; Save(); }
        v = c.StopWhenPlayerAppears;
        if (Ui.Checkbox("Stop when a player appears close by", ref v,
                "Someone who didn't walk there. Arrivals at an aetheryte don't count, but in busy places players also log in or come back from a duty right next to you."))
        { c.StopWhenPlayerAppears = v; Save(); }
        v = c.StopForHiddenPlayer;
        if (Ui.Checkbox("Stop when a hidden player is close by", ref v,
                "Someone the game knows is there but doesn't draw, for 3 seconds. A low character display limit hides players in busy places too. "
                + "A game master the game doesn't send to you at all can't be seen by any plugin."))
        { c.StopForHiddenPlayer = v; Save(); }
        if (c.StopWhenPlayerAppears || c.StopForHiddenPlayer)
            Slider("Close by", "##nearby", c.NearbyYalms, 5, 50, "within %d yalms", x => c.NearbyYalms = x);
        Ui.EndCard();

        // Limits
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Limits for a run");
        Ui.Wrapped("A run stops itself when it reaches one of these. 0 means no limit.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        Input("Gil to spend", "##gil", c.MaxGilPerRun, 0, 99_999_999, "gil at vendors per run", x => c.MaxGilPerRun = x);
        Slider("Time limit", "##minutes", c.MaxRunMinutes, 0, 600, c.MaxRunMinutes == 0 ? "no limit" : "%d minutes", x => c.MaxRunMinutes = x);
        Slider("Knock-outs", "##ko", c.MaxKnockOuts, 0, 10, c.MaxKnockOuts == 0 ? "stop at the first" : "get back up %d times", x => c.MaxKnockOuts = x);
        Slider("Free slots", "##slots", c.MinFreeSlots, 1, 20, "stop at %d free inventory slots", x => c.MinFreeSlots = x);
        Ui.Wrapped("The time limit waits for the item being crafted, so no materials are lost.", Palette.TextMuted);
        Ui.EndCard();
    }

    /// <summary>
    /// One row for each of the three plugins that's loaded: its name, its state as a chip (an icon and words, never
    /// the colour alone) and a sentence more where that helps. Names and states each keep to a column, the names' as
    /// wide as the page's other labels.
    /// </summary>
    private static void DrawOtherPlugins(bool pauseOthers)
    {
        var s = Ui.S;
        var statuses = PluginConflicts.Statuses(OtherPlugins.State, pauseOthers, OtherPlugins.Held);
        ImGui.Dummy(new Vector2(0, 2 * s));
        if (statuses.Count == 0)
        {
            Ui.Wrapped("YesAlready, TextAdvance and Questionable aren't loaded, so none of them is in a run's way.", Palette.TextMuted);
            return;
        }

        var column = 130 * s;
        using (Ui.F.Strong.Push())
            foreach (var status in statuses) column = MathF.Max(column, ImGui.CalcTextSize(status.Plugin).X + 14 * s);
        var chipH = 20 * s;
        var dl = ImGui.GetWindowDrawList();
        foreach (var status in statuses)
        {
            var (color, icon) = status.Tone switch
            {
                StatusTone.Fine => (Palette.Accent, FontAwesomeIcon.Pause),
                StatusTone.Warning => (Palette.Warn, FontAwesomeIcon.ExclamationTriangle),
                _ => (Palette.TextMuted, FontAwesomeIcon.InfoCircle),
            };
            var start = ImGui.GetCursorScreenPos();
            // Beside the name; under it when the card is too narrow (or the text too large) for both.
            var beside = column + Ui.ChipWidth(status.State, icon) <= Ui.CardInnerWidth;
            if (beside)
            {
                // The chip is taller than a line of text: the name sits on its middle.
                using (Ui.F.Strong.Push())
                    dl.AddText(new Vector2(start.X, start.Y + (chipH - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.Text), status.Plugin);
                ImGui.SetCursorScreenPos(new Vector2(start.X + column, start.Y));
            }
            else
            {
                using (Ui.F.Strong.Push()) Ui.Text(status.Plugin);
                ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 4 * s);
            }
            Ui.Chip(status.State, color, icon);
            if (status.Note == null) continue;
            // The sentence belongs to its chip: under it, and closer to it than to the next row.
            ImGui.SetCursorScreenPos(new Vector2(start.X + (beside ? column : 0), ImGui.GetCursorScreenPos().Y - 6 * s));
            using (Ui.F.Small.Push()) Ui.Wrapped(status.Note, Palette.TextMuted);
        }
    }

    private void Slider(string label, string id, int value, int min, int max, string format, Action<int> set)
    {
        var s = Ui.S;
        Ui.Text(label);
        ImGui.SameLine(130 * s);
        ImGui.SetNextItemWidth(260 * s);
        if (ImGui.SliderInt(id, ref value, min, max, format)) { set(value); Save(); }
    }

    private void Input(string label, string id, int value, int min, int max, string hint, Action<int> set)
    {
        var s = Ui.S;
        Ui.Text(label);
        ImGui.SameLine(130 * s);
        ImGui.SetNextItemWidth(160 * s);
        if (ImGui.InputInt(id, ref value, 1000, 10000)) { set(Math.Clamp(value, min, max)); Save(); }
        ImGui.SameLine(0, 8 * s);
        Ui.Muted(hint);
    }

    private void Save()
    {
        Config.Save();
        Integrations.Travel.AdvancedUnstuck = Config.Security.AdvancedUnstuck;
        Integrations.Travel.StuckAfter = TimeSpan.FromSeconds(Math.Clamp(Config.Security.StuckSeconds, 3, 30));
    }
}
