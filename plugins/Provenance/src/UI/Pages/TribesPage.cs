using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Provenance.Core.Character;
using Provenance.Core.Tribes;
using Provenance.Data;
using Provenance.UI.Theme;
using Game = Provenance.Integrations.Tribes;
using Tribe = Provenance.Core.Tribes.Tribe;
using TribeRun = Provenance.Run.Tribes.TribeRun;

namespace Provenance.UI.Pages;

/// <summary>
/// Tribes (Expeditions): the allied societies. The day (allowances left, the reset, quests left over from before it),
/// the run's goal, the combat job its fights are done on and its options, and every society with its rank and today's
/// status; a society opens to what unlocks it, how far its next rank and its top rank are, today's quests (what each
/// asks and as which class, what the run does, what's yours to do and why, what's missing), whether they match the
/// giver's list, what each rank opens in its shop (and what you own), and its achievements.
/// </summary>
internal sealed class TribesPage : Page
{
    private readonly HashSet<uint> open = new();
    private List<DropdownItem>? goalItems;
    private Snapshot? snapshot;
    private DateTime snapshotAt;
    private string? startProblem;

    public TribesPage(Plugin plugin) : base(plugin) { }

    private TribeSettings S => Config.Tribes;
    private TribeRun Loop => Plugin.TribeLoop;
    private TribeData? Data => Plans.Catalog?.Tribes;

    public override string Title => "Tribes";
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Handshake;

    public override (string Text, Vector4 Color)? Status
        => Loop.Active
            ? Loop.Waiting != null
                ? (Loop.WaitingForCutscene ? "Paused for a quest cutscene" : "Waiting for you: see the top of the page", Palette.Warn)
                : (Loop.Status, Loop.Paused || Loop.Pausing ? Palette.Warn : Palette.Accent)
            : Loop.Problem != null ? ("The run stopped with a problem; see below", Palette.Warn)
            : Data is { Tribes.Count: > 0 } && Game.Ready ? ($"{Game.AllowancesLeft} of {Game.DailyAllowance} allowances left today", Palette.TextMuted) : null;

    public override (Vector4 Color, bool Pulse)? Live
        => Loop.Active ? (Loop.Paused || Loop.Pausing ? Palette.Warn : Palette.Accent, Loop.Working) : null;

    public override IEnumerable<FooterAction> Actions
    {
        get
        {
            if (Loop.Active)
            {
                foreach (var a in TribeRunActions(Loop)) yield return a;
                yield break;
            }
            yield return new FooterAction("Start", Ui.Kind.Primary, FontAwesomeIcon.Play, () => startProblem = Loop.Start(),
                Tooltip: "Accepts today's planned dailies at each society and does them: the crafting, the gathering, the fights and the errands.");
        }
    }

    /// <summary>The run's buttons while it goes: Stop, and Pause / Resume (also on the compact strip).</summary>
    public static IEnumerable<FooterAction> TribeRunActions(TribeRun loop)
    {
        yield return new FooterAction("Stop", Ui.Kind.Danger, FontAwesomeIcon.Stop, () => loop.Stop(),
            Tooltip: (loop.Paused ? "Ends the run." : "Stops once the item being crafted is finished; anything else stops right away.")
                     + " Dailies already accepted stay in your journal.");
        if (loop.Paused)
            yield return new FooterAction("Resume", Ui.Kind.Primary, FontAwesomeIcon.Play, loop.Resume,
                Tooltip: loop.Waiting != null && !loop.WaitingForCutscene ? "Carries on once you've done what it asked." : "Carries on where it paused.");
        else if (loop.Pausing)
            yield return new FooterAction("Keep going", Ui.Kind.Secondary, FontAwesomeIcon.Play, loop.Resume, Tooltip: "Never mind the pause.");
        else
            yield return new FooterAction("Pause", Ui.Kind.Secondary, FontAwesomeIcon.Pause, loop.Pause,
                Tooltip: "Pauses after the item being crafted, the node being gathered or the enemy being fought, otherwise right away.");
    }

    /// <summary>What the page shows, read from the game at most twice a second.</summary>
    /// <param name="Who">The player's classes, the class that's on and the combat job picked under Settings, Monster drops.</param>
    private sealed record Snapshot(List<TribeToday> Days, List<AcceptedTribeQuest> Accepted, int Allowances, PlayerClasses Who);

    private Snapshot Read(TribeData data)
    {
        if (snapshot != null && DateTime.UtcNow - snapshotAt < TimeSpan.FromMilliseconds(500)) return snapshot;
        var accepted = Game.Accepted(data);
        var slots = Game.DailySlots();
        var days = data.Tribes.Select(t => Game.Today(data, t, accepted, slots)).ToList();
        snapshot = new Snapshot(days, accepted, Game.AllowancesLeft, Plugin.TribePlanning.Who());
        snapshotAt = DateTime.UtcNow;
        return snapshot;
    }

    public override void Draw()
    {
        var s = Ui.S;
        var w = MathF.Min(ImGui.GetContentRegionAvail().X, 760 * s);
        // Until the page has been opened once, nothing of Tribes runs in the background (the watch).
        if (!S.Seen)
        {
            S.Seen = true;
            Config.Save();
        }
        Ui.Heading("Tribes", "The allied societies' daily quests: twelve a day between them, three from each giver. Open a society for "
                             + "today's quests, how far its next rank is, and what its ranks open.");
        ImGui.Dummy(new Vector2(0, 2 * s));
        OtherPluginsNotice.Draw(Plugin, w);
        if (startProblem != null && !Loop.Active)
        {
            Ui.Icon(FontAwesomeIcon.ExclamationTriangle, Palette.Warn, small: true);
            ImGui.SameLine(0, 8 * s);
            Ui.Wrapped("Can't start: " + startProblem, Palette.Warn, w);
            ImGui.Dummy(new Vector2(0, 2 * s));
        }
        if (Data is not { Tribes.Count: > 0 } data)
        {
            // The societies are read after the rest of the catalog.
            Ui.Wrapped(Plans.Catalog is not { TribesRead: true } ? "Reading the game data…" : "The game data has no allied societies.", Palette.TextMuted);
            return;
        }
        if (!Game.Ready)
        {
            DrawNotLoggedIn(w);
            return;
        }

        var snap = Read(data);
        DrawRun(snap, w);
        DrawDay(snap, w);
        DrawSocieties(data, snap, w, true);
        DrawSocieties(data, snap, w, false);
    }

    private static void DrawNotLoggedIn(float w)
    {
        var s = Ui.S;
        Ui.BeginCard(Palette.WithAlpha(Palette.Warn, 0.07f), width: w);
        Ui.Icon(FontAwesomeIcon.InfoCircle, Palette.Warn);
        ImGui.SameLine(0, 10 * s);
        ImGui.BeginGroup();
        using (Ui.F.Strong.Push()) Ui.Text("Log in to see your societies");
        Ui.Wrapped("Ranks, today's quests and allowances come from your character.", Palette.TextMuted);
        ImGui.EndGroup();
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ the run (goal and options)

    private static readonly (TribeGoal Goal, string Label, string Text)[] Goals =
    {
        (TribeGoal.RankUp, "Rank up", "Societies below their top rank first, the lowest rank first. A society whose bar is full waits for its rank quest, and its quests go to the others."),
        (TribeGoal.Currency, "Currency", "As many dailies as the day allows, for each society's currency (and gil and crystals)."),
        (TribeGoal.Levelling, "Levelling", "As many dailies as the day allows, each on the lowest class that can take it; a battle quest on the lowest combat job "
                                           + "at least 5 levels above it."),
    };

    /// <summary>
    /// The run's place on the page, laid out like the Custom Deliveries page: what it waits for you to do (when it does),
    /// its progress and live log (while it runs, or after it ran), then the goal and options, then the day's plan (shared
    /// with the Plan page). Start, Pause and Stop are on the action bar (<see cref="Actions"/>).
    /// </summary>
    private void DrawRun(Snapshot snap, float w)
    {
        if (Loop.Active && Loop.Waiting != null) DrawWaiting(w);
        if (Loop.Active || Loop.Lines.Count > 0) DrawProgress(w);
        DrawSettings(snap, w);
        DrawPlan(Plugin, w, false);
    }

    /// <summary>
    /// The run waits for the player: what to do, in the run's words, and Resume right under it. A cutscene the options
    /// pause for reads the same, quieter.
    /// </summary>
    private void DrawWaiting(float w)
    {
        var s = Ui.S;
        var cutscene = Loop.WaitingForCutscene;
        var tint = cutscene ? Palette.Accent : Palette.Warn;
        Ui.BeginCard(Palette.WithAlpha(tint, 0.08f), width: w);
        Ui.Icon(cutscene ? FontAwesomeIcon.Film : FontAwesomeIcon.UserCheck, tint);
        ImGui.SameLine(0, 10 * s);
        ImGui.BeginGroup();
        using (Ui.F.Strong.Push()) Ui.Text(cutscene ? "Paused for the cutscene" : "Waiting for you");
        Ui.Wrapped(cutscene ? "Watch it, then Resume carries on with the quest." : Loop.Waiting ?? "", Palette.Text);
        if (Loop.Doing is { } quest)
            using (Ui.F.Small.Push())
                Ui.Wrapped(Loop.Visiting is { } tribe ? $"\"{quest.Name}\", {tribe.Name}" : $"\"{quest.Name}\"", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 4 * s));
        if (Ui.Button("Resume", Ui.Kind.Primary, FontAwesomeIcon.Play, id: "##tribesresume",
                tooltip: cutscene ? "Carries on with the quest." : "Carries on from the step you did.")) Loop.Resume();
        ImGui.EndGroup();
        Ui.EndCard();
    }

    /// <summary>The run going (or the last one): what it does now, how far it got, and its log.</summary>
    private void DrawProgress(float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text(Loop.Active ? "Under way" : "Last run");
        if (Loop.Active && Loop.Waiting == null) Ui.Wrapped(Loop.Status, Palette.Text);
        if (Loop.Active && Loop.Visiting is { } tribe) Ui.Chip(tribe.Name, Palette.Accent, FontAwesomeIcon.Handshake);
        using (Ui.F.Small.Push())
        {
            var done = $"{Loop.Completed} {(Loop.Completed == 1 ? "daily" : "dailies")} done, {Loop.AcceptedCount} accepted";
            Ui.Muted(Loop.Active ? $"{done}, {Loop.Plan.Count} still planned" : done);
        }
        ImGui.Dummy(new Vector2(0, 2 * s));
        foreach (var (at, text, problem) in Loop.Lines.Reverse().Take(8))
        {
            using (Ui.F.Small.Push())
            {
                Ui.Text(at.ToString("HH:mm"), Palette.TextFaint);
                ImGui.SameLine(0, 10 * s);
                Ui.Wrapped(text, problem ? Palette.Warn : Palette.TextMuted);
            }
        }
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ the day's plan

    /// <summary>
    /// The day's plan: the dailies in tour order with the class each is taken on, the crystals they may use, notices
    /// (a rank quest ready) and what's left out and why. Also on the Plan page when there's no order.
    /// </summary>
    public static void DrawPlan(Plugin plugin, float w, bool fromPlanPage)
    {
        var s = Ui.S;
        if (plugin.Plans.Catalog is not { } catalog || catalog.Tribes is not { Tribes.Count: > 0 } || !Game.Ready) return;
        var loop = plugin.TribeLoop;
        var plan = loop.Active ? loop.Plan : plugin.TribePlanning.Current;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Today's plan");
        if (plan.Count == 0)
        {
            Ui.Wrapped(plan.Blocked ?? (Game.AllowancesLeft == 0 ? "No allowances left today." : "Nothing for the run today with the current options."), Palette.TextMuted);
        }
        else
        {
            var societies = plan.Visits.Select(v => v.Tribe.Id).Distinct().Count();
            var inJournal = plan.Count - plan.ToAccept;
            var parts = new List<string>();
            if (plan.ToAccept > 0) parts.Add($"{plan.ToAccept} {(plan.ToAccept == 1 ? "daily" : "dailies")} to accept");
            if (inJournal > 0) parts.Add($"{inJournal} in your journal");
            Ui.Wrapped($"{GoalName(plan.Goal)}: {Join(parts)} at {societies} {(societies == 1 ? "society" : "societies")}, "
                       + $"with {plan.Allowances} of {Game.DailyAllowance} allowances left.", Palette.TextMuted);
            if (plan.Blocked != null) Ui.Wrapped(plan.Blocked, Palette.Warn);
            ImGui.Dummy(new Vector2(0, 2 * s));
            foreach (var v in plan.Visits)
            foreach (var q in v.Quests)
                DrawPlanned(catalog, v, q, TribeClasses.FightsOnAnotherJob(q.Quest) ? plugin.Config.Combat.ClassJob : 0);
            DrawCrystals(plugin, catalog, plan);
        }
        if (plan.Notices.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            foreach (var n in plan.Notices) Ui.Wrapped($"{n.Tribe.Name}: {n.Text}", Palette.Text);
        }
        if (plan.LeftOut.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            Ui.Muted("Not this time:");
            const int shown = 6;
            var reasons = LeftOutLines(plan);
            foreach (var line in reasons.Take(shown))
                using (Ui.F.Small.Push()) Ui.Wrapped(line, Palette.TextMuted);
            if (reasons.Count > shown)
                using (Ui.F.Small.Push())
                    Ui.Wrapped($"And {reasons.Count - shown} more reasons. Open a society on the Tribes page for each quest's reason.", Palette.TextFaint);
        }
        if (fromPlanPage)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            if (Ui.LinkButton("Open Tribes", Palette.Accent, FontAwesomeIcon.Handshake)) plugin.Main.Select<TribesPage>();
        }
        Ui.EndCard();
    }

    private static string GoalName(TribeGoal goal) => Goals.First(g => g.Goal == goal).Label;

    /// <summary>
    /// What the plan leaves out, a line per reason in the plan's order: one or two dailies by name, more as a count
    /// with their societies. With twenty societies and twelve allowances a day, most of a day's offers share a reason
    /// ("No allowances left for it today"), which would bury the others.
    /// </summary>
    private static List<string> LeftOutLines(TribePlan plan)
    {
        var lines = new List<string>();
        foreach (var group in plan.LeftOut.GroupBy(l => l.Reason))
        {
            var all = group.ToList();
            var societies = all.Select(l => l.Tribe.Name).Distinct().ToList();
            var where = societies.Count <= 3 ? Join(societies) : $"{string.Join(", ", societies.Take(2))} and {societies.Count - 2} more societies";
            var what = all.Count <= 2 ? Join(all.Select(l => $"\"{l.Quest.Name}\"")) : $"{all.Count} dailies";
            lines.Add($"{what} ({where}): {group.Key}.");
        }
        return lines;
    }

    /// <summary>One planned daily: its item or errand glyph, its name over its society and what it asks, and the class chip.</summary>
    /// <param name="fighter">The combat job a crafter's daily fights on (the Ixal's); 0 for the others.</param>
    private static void DrawPlanned(GameCatalog catalog, TribeVisit v, PlannedQuest q, uint fighter)
    {
        var s = Ui.S;
        var start = ImGui.GetCursorScreenPos();
        var width = Ui.CardInnerWidth;
        var h = 40 * s;
        var dl = ImGui.GetWindowDrawList();
        DrawQuestIcon(dl, catalog, q.Quest, q.ClassJob, start + new Vector2(0, (h - 28 * s) / 2));

        var (chip, color, icon, job) = q.Leftover
            ? ("Left over from yesterday", Palette.Warn, FontAwesomeIcon.ExclamationTriangle, 0u)
            : q.Accepted
                ? ($"Accepted as {ClassJobs.Abbreviation(q.ClassJob)}", Palette.ClassJobColor(q.ClassJob), (FontAwesomeIcon?)null, q.ClassJob)
                : (ClassJobs.Abbreviation(q.ClassJob), Palette.ClassJobColor(q.ClassJob), null, q.ClassJob);
        var chipW = Ui.ChipWidth(chip, icon, job);
        var x = start.X + 38 * s;
        var textRight = start.X + width - chipW - 12 * s;
        dl.PushClipRect(start, new Vector2(textRight, start.Y + h), true);
        dl.AddText(new Vector2(x, start.Y + 3 * s), Palette.U32(Palette.Text), q.Quest.Name);
        using (Ui.F.Small.Push())
            dl.AddText(new Vector2(x, start.Y + 22 * s), Palette.U32(Palette.TextMuted), $"{v.Tribe.Name}. {What(catalog, q.Quest, q.ClassJob, fighter)}");
        dl.PopClipRect();
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - chipW, start.Y + (h - 20 * s) / 2));
        Ui.Chip(chip, color, icon, job);
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
    }

    /// <summary>
    /// The crystals the plan's crafting dailies may use (counted for the most crafts a daily asks), with what's in the
    /// bags; when some are short, where the rest comes from.
    /// </summary>
    private static void DrawCrystals(Plugin plugin, GameCatalog catalog, TribePlan plan)
    {
        var s = Ui.S;
        var crystals = plugin.TribePlanning.CrystalsShown(plan);
        if (crystals.Count == 0) return;
        ImGui.Dummy(new Vector2(0, 4 * s));
        Ui.Wrapped("Crystals, for up to " + QuestCraft.MostCrafts + " crafts a daily: "
                   + Join(crystals.Select(c => $"{c.Needed:N0} {catalog.ItemNamePlural(c.ItemId)} (you have {c.OnHand:N0})")) + ".", Palette.TextMuted);
        var shortOnes = crystals.Where(c => c.Short > 0).ToList();
        if (shortOnes.Count == 0) return;
        var fromRetainers = shortOnes.Where(c => c.Retainers > 0).ToList();
        var text = fromRetainers.Count > 0
            ? $"Before the tour the run takes what's short from your retainers ({Join(fromRetainers.Select(c => $"{c.Retainers:N0} {catalog.ItemNamePlural(c.ItemId)}"))}) and gathers the rest. "
            : "Before the tour the run gathers what's short. ";
        using (Ui.F.Small.Push()) Ui.Wrapped(text + "A daily the crystals still don't cover is left out.", Palette.TextFaint);
    }


    private void DrawSettings(Snapshot snap, float w)
    {
        var s = Ui.S;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text("Goal");
        var taking = snap.Days.Where(d => d.Unlocked && !S.LeftOut.Contains(d.Tribe.Id)).ToList();
        var effective = TribeGoals.Effective(S.Goal, taking);
        var shown = Goals.First(g => g.Goal == effective).Label;
        if (Ui.DropdownField("##tribegoal", shown, "Pick a goal", 320 * s, FontAwesomeIcon.Flag, detail: S.Goal == null ? "picked for you" : null))
        {
            goalItems = new List<DropdownItem>
            {
                new("Pick for me", Sub: "Rank up while a society is below its top rank, then Currency"),
            };
            goalItems.AddRange(Goals.Select(g => new DropdownItem(g.Label, Sub: g.Text)));
        }
        var pick = Ui.DropdownList("##tribegoal", goalItems, S.Goal is { } chosen ? 1 + Array.FindIndex(Goals, g => g.Goal == chosen) : 0, "");
        if (pick >= 0)
        {
            S.Goal = pick == 0 ? null : Goals[pick - 1].Goal;
            Config.Save();
        }
        ImGui.Dummy(new Vector2(0, 2 * s));
        Ui.Wrapped(Goals.First(g => g.Goal == effective).Text, Palette.TextMuted);

        DrawCombatJob(snap, taking, effective == TribeGoal.Levelling);

        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("Options");
        var v = S.PauseForCutscenes;
        if (Ui.Checkbox("Pause for cutscenes", ref v, "Off: the run skips quest cutscenes. On: it pauses so you can watch, and Resume carries on."))
        {
            S.PauseForCutscenes = v;
            Config.Save();
        }
        v = S.LeaveOutSay;
        if (Ui.Checkbox("Leave out quests that /say in public", ref v,
                "Some dailies have you say a phrase in /say, which players nearby see. The run says only the quest's own phrase, and never twice: "
                + "if the game misses it, the run asks you to do that step. On: those quests are yours to do."))
        {
            S.LeaveOutSay = v;
            Config.Save();
            snapshot = null;
        }
        v = S.WatchByHand;
        if (Ui.Checkbox("Note what the run would do while I do society quests myself", ref v,
                "For bug reports: the debug log notes the steps and targets the run would pick. It only watches, it never acts."))
        {
            S.WatchByHand = v;
            Config.Save();
        }
        Ui.EndCard();
    }

    /// <summary>
    /// The job the run's fights are done on (Settings, Monster drops), where a fight can come up: with a battle society
    /// or the Ixal unlocked and ticked. A chip with the job, or in warning colours what's missing, and where to change it.
    /// </summary>
    private static void DrawCombatJob(Snapshot snap, List<TribeToday> taking, bool levelling)
    {
        if (!taking.Any(d => d.Tribe.Kind == TribeKind.Battle || d.Tribe.Id == QuestPolicy.IxalId)) return;
        var s = Ui.S;
        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("Combat job");
        var job = snap.Who.CombatJob;
        if (job != 0 && snap.Who.Of(job) is { Fights: true } picked)
        {
            Ui.Chip($"{ClassName(job)}, level {picked.Level}", Palette.ClassJobColor(job), job: job);
            Ui.Wrapped("The job picked under Settings, Monster drops. Battle societies' dailies and the fights in Ixal dailies are done on it, "
                       + "in its best gearset, with the chocobo as set there."
                       + (levelling ? " Under Levelling a battle daily goes to a lower combat job when one is at least 5 levels above it." : ""), Palette.TextMuted);
            if (!picked.HasGearset)
                Ui.Wrapped($"No gearset is saved for {ClassName(job)}: the run can only change to a job with one.", Palette.Warn);
            return;
        }
        Ui.Chip(job == 0 ? "None picked" : $"No combo for {ClassJobs.Abbreviation(job)}", Palette.Warn, FontAwesomeIcon.ExclamationTriangle);
        Ui.Wrapped("Battle societies' dailies and the fights in Ixal dailies need a combat job: pick one under Settings, Monster drops. "
                   + "Without one, battle dailies are only taken on the combat job you're on when you start, and Ixal dailies with a fight are left out.", Palette.TextMuted);
    }

    // ------------------------------------------------------------------ the day

    private void DrawDay(Snapshot snap, float w)
    {
        var s = Ui.S;
        Ui.BeginCard(Palette.WithAlpha(Palette.Accent, 0.07f), width: w);
        Ui.Icon(FontAwesomeIcon.CalendarAlt, Palette.Accent);
        ImGui.SameLine(0, 10 * s);
        ImGui.BeginGroup();
        using (Ui.F.Strong.Push()) Ui.Text($"{snap.Allowances} of {Game.DailyAllowance} allowances left today");
        var reset = Game.NextReset;
        Ui.Wrapped($"They come back in {Until(reset)}, at {reset:HH:mm} your time. Each giver offers up to 3 dailies a day, "
                   + "and 3 more of the new rank on the day you rank up, when that rank has dailies of its own.", Palette.TextMuted);
        ImGui.EndGroup();

        var leftovers = snap.Accepted.Where(a => a.Leftover).ToList();
        if (leftovers.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            Ui.Chip("Left over from yesterday", Palette.Warn, FontAwesomeIcon.ExclamationTriangle);
            var names = string.Join(", ", leftovers.Select(a => $"\"{a.Quest.Name}\""));
            Ui.Wrapped($"{names} {(leftovers.Count == 1 ? "was" : "were")} accepted before the reset. The game gives no new dailies until "
                       + $"{(leftovers.Count == 1 ? "it's" : "they're")} handed in or abandoned.", Palette.Warn);
        }

        var ready = snap.Days.Where(d => d.RankQuestReady != null).ToList();
        if (ready.Count > 0)
        {
            ImGui.Dummy(new Vector2(0, 4 * s));
            Ui.Wrapped($"Rank quest ready for {Join(ready.Select(d => d.Tribe.Name))}. Do it yourself for now: a button for it comes in a later version.",
                Palette.Text);
        }
        Ui.EndCard();
    }

    // ------------------------------------------------------------------ societies

    private void DrawSocieties(TribeData data, Snapshot snap, float w, bool craftOrGather)
    {
        var s = Ui.S;
        var days = snap.Days.Where(d => d.Tribe.CraftOrGather == craftOrGather).ToList();
        if (days.Count == 0) return;
        Ui.BeginCard(width: w);
        using (Ui.F.Strong.Push()) Ui.Text(craftOrGather ? "Crafter and gatherer societies" : "Battle societies");
        Ui.Wrapped(craftOrGather
            ? "Their dailies are the run's: open one for today's quests and what's in the way."
            : "Their dailies are the run's too, fights included, on your combat job: open one for today's quests and what's in the way.", Palette.TextMuted);
        ImGui.Dummy(new Vector2(0, 4 * s));
        foreach (var day in days)
        {
            var state = StateOf(day, snap);
            DrawRow(data, day, state);
            if (open.Contains(day.Tribe.Id)) DrawDetails(data, day, state, snap);
        }
        Ui.EndCard();
    }

    private enum State { Locked, Leftover, RankQuest, Top, DoneToday, UnderWay, LeftOut, Open }

    private State StateOf(TribeToday day, Snapshot snap)
    {
        if (!day.Unlocked) return State.Locked;
        if (snap.Accepted.Any(a => a.Leftover && a.Quest.TribeId == day.Tribe.Id)) return State.Leftover;
        if (day.RankQuestReady != null) return State.RankQuest;
        if (S.LeftOut.Contains(day.Tribe.Id)) return State.LeftOut;
        if (day.Offers.Any() && day.LeftToday(Game.DailyAllowance) == 0)
            return day.Offers.All(o => o.State == OfferState.Done) ? State.DoneToday : State.UnderWay;
        if (day.AtTop) return State.Top;
        return State.Open;
    }

    private void DrawRow(TribeData data, TribeToday day, State state)
    {
        var s = Ui.S;
        var t = day.Tribe;
        var width = Ui.CardInnerWidth;
        var h = 46 * s;
        var start = ImGui.GetCursorScreenPos();
        var isOpen = open.Contains(t.Id);
        if (ImGui.InvisibleButton("##tribe" + t.Id, new Vector2(width, h)))
        {
            if (!open.Remove(t.Id)) open.Add(t.Id);
        }
        var hovered = ImGui.IsItemHovered();
        if (hovered) ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        var dl = ImGui.GetWindowDrawList();
        var hover = Ui.Fade(ImGui.GetID("##tribehover" + t.Id), hovered ? 1f : 0f, 22f);
        if (hover > 0.01f)
            dl.AddRectFilled(start - new Vector2(8 * s, 0), start + new Vector2(width + 8 * s, h), Palette.U32(Palette.WithAlpha(Palette.Raised, 0.7f * hover)), Ui.InputRadius * s);

        // Chevron, then name over place.
        using (Ui.F.IconSmall.Push())
        {
            var glyph = (isOpen ? FontAwesomeIcon.AngleDown : FontAwesomeIcon.AngleRight).ToIconString();
            var gs = ImGui.CalcTextSize(glyph);
            dl.AddText(new Vector2(start.X + 6 * s - gs.X / 2, start.Y + (h - gs.Y) / 2), Palette.U32(Palette.Mix(Palette.TextFaint, Palette.TextMuted, hover)), glyph);
        }
        var x = start.X + 22 * s;
        using (Ui.F.Strong.Push()) dl.AddText(new Vector2(x, start.Y + 5 * s), Palette.U32(state == State.Locked ? Palette.TextMuted : Palette.Text), t.Name);
        using (Ui.F.Small.Push()) dl.AddText(new Vector2(x, start.Y + 26 * s), Palette.U32(Palette.TextMuted), t.Place);

        // Status chip on the right; rank and today's count in between, when there's room.
        var (chipText, chipColor, chipIcon) = Chip(day, state);
        var chipLeft = start.X + width;
        if (chipText != null)
        {
            chipLeft -= Ui.ChipWidth(chipText, chipIcon);
            ImGui.SetCursorScreenPos(new Vector2(chipLeft, start.Y + (h - 20 * s) / 2));
            Ui.Chip(chipText, chipColor, chipIcon);
        }
        if (day.Unlocked)
        {
            var rankX = start.X + MathF.Max(190 * s, width * 0.42f);
            if (rankX + 110 * s < chipLeft - 8 * s)
            {
                dl.AddText(new Vector2(rankX, start.Y + 6 * s), Palette.U32(Palette.Text), RankText(data, day));
                ImGui.SetCursorScreenPos(new Vector2(rankX, start.Y + 30 * s));
                var fraction = day.AtTop ? 1f : day.Needed > 0 ? Math.Clamp(day.Value / (float)day.Needed, 0f, 1f) : 0f;
                Ui.Bar("##rep" + t.Id, fraction, day.BarFull ? Palette.Warn : Palette.Accent, 100 * s, 5);
            }
            var todayText = TodayText(day);
            var todayX = rankX + 130 * s;
            using (Ui.F.Small.Push())
            {
                var tw = ImGui.CalcTextSize(todayText).X;
                if (todayText.Length > 0 && todayX + tw < chipLeft - 10 * s)
                    dl.AddText(new Vector2(todayX, start.Y + (h - ImGui.GetTextLineHeight()) / 2), Palette.U32(Palette.TextMuted), todayText);
            }
        }
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
    }

    /// <summary>"Honored", "Bloodsworn, the top", "Allied".</summary>
    private static string RankText(TribeData data, TribeToday day)
    {
        var name = data.RankName(day.Tribe, day.Rank, day.Allied);
        return day.AtTop && !day.Allied ? $"{name}, the top" : name;
    }

    /// <summary>"2 of 3 today": taken, of what the day allows the society (6 on a rank-up day, more with several givers).</summary>
    private static string TodayText(TribeToday day)
    {
        var all = day.Taken + day.LeftToday(Game.DailyAllowance);
        return all == 0 ? "" : $"{day.Taken} of {all} today";
    }

    /// <summary>The status chip: what stands in the way, or what's done; nothing while today's quests are open.</summary>
    private static (string? Text, Vector4 Color, FontAwesomeIcon? Icon) Chip(TribeToday day, State state) => state switch
    {
        State.Locked => ("Locked", Palette.TextMuted, FontAwesomeIcon.Lock),
        State.Leftover => ("Left over from yesterday", Palette.Warn, FontAwesomeIcon.ExclamationTriangle),
        State.RankQuest => ("Rank quest ready", Palette.Warn, FontAwesomeIcon.Book),
        State.DoneToday => ("Done today", Palette.Accent, FontAwesomeIcon.Check),
        State.UnderWay => ("Under way", Palette.Accent, FontAwesomeIcon.Hourglass),
        State.LeftOut => ("Left out", Palette.TextMuted, FontAwesomeIcon.MinusCircle),
        State.Top => day.Offers.Any() ? (null, default, null) : ("Top rank", Palette.Accent, FontAwesomeIcon.Crown),
        _ => (null, default, null),
    };

    // ------------------------------------------------------------------ a society opened

    private void DrawDetails(TribeData data, TribeToday day, State state, Snapshot snap)
    {
        var s = Ui.S;
        var t = day.Tribe;
        var indent = 22 * s;
        ImGui.Indent(indent);
        if (!day.Unlocked)
        {
            DrawUnlock(t);
        }
        else
        {
            var include = !S.LeftOut.Contains(t.Id);
            ImGui.PushID((int)t.Id);
            if (Ui.Checkbox("Include in the run", ref include, "Left out, the run doesn't visit this society."))
            {
                if (include) S.LeftOut.Remove(t.Id);
                else S.LeftOut.Add(t.Id);
                Config.Save();
            }
            ImGui.PopID();
            foreach (var left in snap.Accepted.Where(a => a.Leftover && a.Quest.TribeId == t.Id))
                Ui.Wrapped($"\"{left.Quest.Name}\" is left over from before the reset: hand it in or abandon it, and the giver offers today's dailies.", Palette.Warn);
            DrawRank(data, day, snap);
            DrawQuests(day, snap);
        }
        DrawShop(data, day);
        DrawAchievements(data, t);
        ImGui.Unindent(indent);
        ImGui.Dummy(new Vector2(0, 8 * s));
    }

    /// <summary>A locked society: the unlock quests still to do, where to get each, and what each needs first.</summary>
    private static void DrawUnlock(Tribe t)
    {
        var s = Ui.S;
        var steps = t.Unlock.Where(u => !Game.QuestDone(u.QuestId)).ToList();
        if (steps.Count == 0)
        {
            Ui.Wrapped("Its unlock quests are done, but the game hasn't sent the rank yet.", Palette.TextMuted);
            return;
        }
        var first = steps[0];
        Ui.Wrapped($"Unlock it with the quest \"{first.Name}\" from {first.NpcName} in {Where(first.Place, first.MapPosition)}"
                   + (first.Requirement.Length > 0 ? $", on {first.Requirement}." : "."), Palette.Text);
        var before = first.Previous.Where(p => !Game.QuestDone(p.QuestId)).Select(p => $"\"{p.Name}\"").ToList();
        if (before.Count > 0) Ui.Wrapped($"It needs {Join(before)} done first.", Palette.TextMuted);
        foreach (var next in steps.Skip(1))
            Ui.Wrapped($"Then \"{next.Name}\" from {next.NpcName} in {Where(next.Place, next.MapPosition)}.", Palette.TextMuted);
        using (Ui.F.Small.Push()) Ui.Wrapped("The run doesn't do unlock quests.", Palette.TextFaint);
        ImGui.Dummy(new Vector2(0, 2 * s));
    }

    /// <summary>The bar and what's next: the rank quest, or the dailies and days to the next rank and to the top.</summary>
    private void DrawRank(TribeData data, TribeToday day, Snapshot snap)
    {
        var s = Ui.S;
        var t = day.Tribe;
        ImGui.Dummy(new Vector2(0, 2 * s));
        if (day.RankQuestReady is { } rq)
        {
            Ui.Wrapped($"The bar is full: the rank quest \"{rq.Name}\" is next, from {rq.NpcName} in {Where(rq.Place, rq.MapPosition)}. "
                       + "Until it's done, dailies give no reputation.", Palette.Warn);
            var first = rq.OneOfFirst.Where(q => !Game.QuestDone(q.QuestId)).ToList();
            if (first.Count == rq.OneOfFirst.Count && first.Count > 0)
                Ui.Wrapped($"Before it, one of {Join(first.Select(q => $"\"{q.Name}\""), "or")} (either does).", Palette.TextMuted);
            using (Ui.F.Small.Push()) Ui.Wrapped("Do it yourself for now: a button for it comes in a later version.", Palette.TextMuted);
            return;
        }
        if (day.AtTop)
        {
            var allied = t.IntersocietalQuest != 0 && !day.Allied && t.Expansion > 0
                ? $" The quest \"{t.IntersocietalName}\" makes it Allied, once all three of the expansion's societies are at the top (it takes a combat class)."
                : "";
            Ui.Wrapped($"{data.RankName(t, day.Rank, day.Allied)}, the top rank. The dailies still pay currency and gil.{allied}", Palette.TextMuted);
            return;
        }
        var ladder = data.Ladder(t.Id);
        var standing = day.Standing(snap.Allowances);
        var next = TribeRanks.ToNext(ladder, standing);
        var top = TribeRanks.ToTop(ladder, standing);
        var nextName = data.RankName(t, day.Rank + 1, day.Allied);
        var line = $"{day.Value:N0} of {day.Needed:N0} reputation. {Dailies(next)} to {nextName}{Days(next)}";
        if (day.Rank + 1 < t.MaxRank && top.Known)
            line += $", {Dailies(top)} to {data.RankName(t, t.MaxRank, false)}{Days(top)}";
        Ui.Wrapped(line + ".", Palette.TextMuted);
        if (t.RankQuest(day.Rank) is { } upcoming)
            using (Ui.F.Small.Push()) Ui.Wrapped($"Then the rank quest \"{upcoming.Name}\".", Palette.TextFaint);
    }

    private static string Dailies(RankEstimate e) => e.Known ? $"{e.Dailies} more {(e.Dailies == 1 ? "daily" : "dailies")}" : "some dailies";

    private static string Days(RankEstimate e)
        => !e.Known || e.Days == 0 ? "" : e.LastDay == 0 ? ", today" : e.Days == 1 ? ", tomorrow" : $", about {e.Days} days";

    // ------------------------------------------------------------------ today's quests

    private void DrawQuests(TribeToday day, Snapshot snap)
    {
        var s = Ui.S;
        var catalog = Plans.Catalog!;
        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("Today's quests");
        var withOffers = day.Givers.Where(g => g.Offers.Count > 0).ToList();
        if (withOffers.Count == 0)
        {
            Ui.Wrapped(day.RankedUpToday
                ? "You ranked up today, and this rank has no dailies of its own: the givers offer nothing more until the reset."
                : "Nothing offered today.", Palette.TextMuted);
            return;
        }
        foreach (var giver in withOffers)
        {
            if (withOffers.Count > 1)
                using (Ui.F.Small.Push()) Ui.Text(giver.Giver.Name, Palette.TextMuted);
            foreach (var offer in giver.Offers) DrawQuest(catalog, day, offer, snap);
            DrawCheck(giver.Giver);
        }
    }

    private void DrawQuest(GameCatalog catalog, TribeToday day, TribeOffer offer, Snapshot snap)
    {
        var s = Ui.S;
        var q = offer.Quest;
        var levelling = S.Goal == TribeGoal.Levelling;
        var judgement = QuestPolicy.Judge(q, new PolicyOptions(S.LeaveOutSay, offer.AcceptedAs));
        var job = offer.State == OfferState.Accepted ? offer.AcceptedAs : TribeClasses.Choose(q, snap.Who, levelling);
        QuestReadiness? ready = null;
        if (offer.State == OfferState.Open && judgement.RunCanDo)
            ready = TribeClasses.Readiness(q, snap.Who, levelling, Game.OnHand, Game.Owns, catalog.ItemName, catalog.ItemNamePlural);
        if (ready is { Ready: true }) job = ready.ClassJob;
        // The combat job a crafter's daily fights on (the Ixal's), when one is usable.
        var fighter = TribeClasses.Fighter(q, snap.Who);

        // The chip says where the quest stands; the line under the name says why, or what it asks.
        string chip;
        Vector4 color;
        FontAwesomeIcon? icon = null;
        uint chipJob = 0;
        string sub;
        if (offer.State == OfferState.Done)
        {
            (chip, color, icon, sub) = ("Done today", Palette.Accent, FontAwesomeIcon.Check, What(catalog, q, job, fighter));
        }
        else if (!judgement.RunCanDo)
        {
            (chip, color, icon, sub) = ("Do it yourself", Palette.TextMuted, FontAwesomeIcon.UserCheck, judgement.Reason ?? "");
            if (offer.State == OfferState.Accepted && offer.AcceptedAs != 0) sub = $"Accepted as {ClassJobs.Abbreviation(offer.AcceptedAs)}. " + sub;
        }
        else if (offer.State == OfferState.Accepted && TribeClasses.FighterNeed(q, snap.Who) is { } fight)
        {
            // In the journal as its crafter, but nothing to do its fight on.
            (chip, color, icon, sub) = (NeedChip(fight.Need), Palette.Warn, FontAwesomeIcon.ExclamationTriangle,
                $"Accepted as {ClassJobs.Abbreviation(offer.AcceptedAs)}. {fight.Text}{CombatJobHint(fight)}");
        }
        else if (offer.State == OfferState.Accepted)
        {
            (chip, color, chipJob, sub) = ($"Accepted as {ClassJobs.Abbreviation(offer.AcceptedAs)}", Palette.ClassJobColor(offer.AcceptedAs), offer.AcceptedAs,
                What(catalog, q, offer.AcceptedAs, fighter));
        }
        else if (ready is { Ready: false } need)
        {
            (chip, color, icon, sub) = (NeedChip(need.Need), Palette.Warn, FontAwesomeIcon.ExclamationTriangle, need.Text ?? "");
            if (need.Need == NeedKind.Crystals) sub += " in your bags. The run takes them from your retainers, or gathers them, when it can.";
            else sub += CombatJobHint(need);
        }
        else
        {
            (chip, color, icon, sub) = ("The run does it", Palette.Accent, FontAwesomeIcon.Check, What(catalog, q, job, fighter));
        }

        var start = ImGui.GetCursorScreenPos();
        var width = Ui.CardInnerWidth - 22 * s;
        var dl = ImGui.GetWindowDrawList();
        var chipW = Ui.ChipWidth(chip, icon, chipJob);
        var textX = start.X + 38 * s;
        var textW = MathF.Max(80 * s, width - 38 * s - chipW - 12 * s);
        float subH;
        using (Ui.F.Small.Push()) subH = ImGui.CalcTextSize(sub, false, textW).Y;
        var h = MathF.Max(44 * s, 24 * s + subH + 4 * s);

        DrawQuestIcon(dl, catalog, q, job, start + new Vector2(0, 4 * s));
        dl.PushClipRect(start, new Vector2(textX + textW, start.Y + h), true);
        dl.AddText(new Vector2(textX, start.Y + 4 * s), Palette.U32(offer.State == OfferState.Done ? Palette.TextMuted : Palette.Text), q.Name);
        dl.PopClipRect();
        using (Ui.F.Small.Push())
            dl.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(textX, start.Y + 24 * s), Palette.U32(Palette.TextMuted), sub, textW);
        ImGui.SetCursorScreenPos(new Vector2(start.X + width - chipW, start.Y + 4 * s));
        Ui.Chip(chip, color, icon, chipJob);
        ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + h));
        ImGui.Dummy(new Vector2(width, 0));
    }

    private static string NeedChip(NeedKind need) => need switch
    {
        NeedKind.Crystals => "Needs crystals",
        NeedKind.ClassLevel => "Needs a higher level",
        NeedKind.Gearset => "Needs a gearset",
        NeedKind.EquipItem => "Needs an item",
        NeedKind.CombatJob => "Needs a combat job",
        _ => "Needs something",
    };

    /// <summary>
    /// For a need of the combat job that names the job (its level, its gearset): which job that is and where it's
    /// picked, as a sentence after the need. Nothing for the other needs (theirs say where to go already).
    /// </summary>
    private static string CombatJobHint(QuestReadiness need) => !need.AboutCombatJob ? "" : need.Need switch
    {
        NeedKind.ClassLevel => ". The run fights on the job picked under Settings, Monster drops.",
        NeedKind.Gearset => ". Save one, or pick another job under Settings, Monster drops.",
        _ => "",
    };

    /// <summary>The quest's item (what it crafts or gathers for the class), else a glyph for its kind of errand, in a 28 px square.</summary>
    private static void DrawQuestIcon(ImDrawListPtr dl, GameCatalog catalog, TribeQuest q, uint job, Vector2 pos)
    {
        var s = Ui.S;
        var d = 28 * s;
        var item = Item(q, job);
        if (item != 0)
        {
            Ui.DrawItemIcon(dl, catalog.ItemIcon(item), pos, d);
            return;
        }
        var (glyph, color) = Glyph(q);
        dl.AddRectFilled(pos, pos + new Vector2(d), Palette.U32(Palette.WithAlpha(color, 0.14f)), MathF.Min(Ui.IconRadius * s, d * 0.25f));
        using (Ui.F.IconSmall.Push())
        {
            var text = glyph.ToIconString();
            var size = ImGui.CalcTextSize(text);
            dl.AddText(pos + (new Vector2(d) - size) / 2, Palette.U32(color), text);
        }
    }

    /// <summary>The item the quest has this class make or gather (0 for errands).</summary>
    private static uint Item(TribeQuest q, uint job)
    {
        if (q.Craft is { } c && (job == 0 || ClassJobs.IsCrafter(job))) return c.Product;
        if (q.Gather.Count > 0) return (q.GatherFor(job) ?? q.Gather[0]).ItemId;
        return 0;
    }

    private static (FontAwesomeIcon, Vector4) Glyph(TribeQuest q)
    {
        if (q.Has(StepKinds.Snipe)) return (FontAwesomeIcon.Crosshairs, Palette.TextMuted);
        if (q.Has(StepKinds.Stealth)) return (FontAwesomeIcon.EyeSlash, Palette.TextMuted);
        if (q.Has(StepKinds.Combat)) return (FontAwesomeIcon.FistRaised, Palette.Accent);
        if (q.Has(StepKinds.Mount)) return (FontAwesomeIcon.Horse, Palette.Accent);
        if (q.Has(StepKinds.Say)) return (FontAwesomeIcon.CommentDots, Palette.Accent);
        if (q.Has(StepKinds.Emote)) return (FontAwesomeIcon.Smile, Palette.Accent);
        if (q.Has(StepKinds.Carry)) return (FontAwesomeIcon.Box, Palette.Accent);
        if (q.Has(StepKinds.KeyItem)) return (FontAwesomeIcon.Key, Palette.Accent);
        return (FontAwesomeIcon.MapMarkerAlt, Palette.Accent);
    }

    /// <summary>
    /// What the quest asks and as which class, in a few words: "Crafts Snow Removal Tool as Carpenter, up to 4", "Gathers
    /// Ancient Lumber as Botanist", "Fights Zahar'ak lancers and Zahar'ak archers as Paladin", "An errand as Paladin: rides
    /// a quest mount and uses its action".
    /// </summary>
    /// <param name="fighter">The combat job a crafter's daily fights on (the Ixal's); 0 when it has no fight, or no job is usable.</param>
    private static string What(GameCatalog catalog, TribeQuest q, uint job, uint fighter = 0)
    {
        if (q.Craft is { } c && (job == 0 || ClassJobs.IsCrafter(job)))
        {
            // What a crafting daily does in public or on another job is said too: a fight (the Ixal's), a /say phrase.
            var besides = new List<string>();
            if (q.Has(StepKinds.Combat)) besides.Add($"fights {Enemies(q)}{As(fighter)}");
            if (q.Has(StepKinds.Say)) besides.Add("says a phrase in /say");
            return $"Crafts {catalog.ItemName(c.Product)}{As(job)}, up to {QuestCraft.MostCrafts}{(c.NeedsHq ? ", high quality only" : "")}"
                   + (besides.Count > 0 ? ", and " + Join(besides) : "");
        }
        if (q.Gather.Count > 0 && (job == 0 || job is 16 or 17))
        {
            var g = q.GatherFor(job) ?? q.Gather[0];
            return $"Gathers {catalog.ItemName(g.ItemId)}{As(job)}";
        }
        var parts = new List<string>();
        if (q.Has(StepKinds.Mount)) parts.Add(q.Has(StepKinds.MountAction) ? "rides a quest mount and uses its action" : "rides a quest mount");
        if (q.Has(StepKinds.Say)) parts.Add("says a phrase in /say");
        if (q.Emotes.Count > 0) parts.Add("uses " + Join(q.Emotes.Select(EmoteCommand)));
        if (q.Has(StepKinds.Carry)) parts.Add("carries a load");
        if (q.Has(StepKinds.ItemOnEnemy)) parts.Add("uses a key item on an enemy");
        else if (q.Has(StepKinds.UseItem)) parts.Add("uses a key item");
        else if (q.Has(StepKinds.KeyItem)) parts.Add("hands over a key item");
        if (q.SubZones.Count > 0) parts.Add("goes to " + Join(q.SubZones.Select(ZoneName).Where(z => z.Length > 0).Distinct()));
        if (q.Has(StepKinds.Snipe)) parts.Add("shoots targets");
        if (q.Has(StepKinds.Stealth)) parts.Add("stays unseen under a spell");
        if (q.Has(StepKinds.Combat))
            return $"Fights {Enemies(q)}{As(job)}" + (parts.Count > 0 ? "; also " + Join(parts) : "");
        return $"An errand{As(job)}: " + (parts.Count == 0 ? "talks to people nearby" : Join(parts));
    }

    /// <summary>" as Paladin" (nothing while no class is known).</summary>
    private static string As(uint job) => job != 0 ? $" as {ClassName(job)}" : "";

    /// <summary>
    /// Who a quest fights: the field monsters it counts by name ("Zahar'ak lancers and Zahar'ak archers"), else the
    /// enemies that appear for it (the game data gives those no name).
    /// </summary>
    private static string Enemies(TribeQuest q) => q.FieldEnemies.Count > 0 ? Join(q.FieldEnemies) : "the quest's own enemies";

    /// <summary>Whether the offers worked out for a giver match what its list showed today.</summary>
    private static void DrawCheck(TribeGiver giver)
    {
        var s = Ui.S;
        ImGui.Dummy(new Vector2(0, 2 * s));
        switch (Game.Matches(giver.NpcId))
        {
            case true:
                Ui.Chip("Matches the game", Palette.Accent, FontAwesomeIcon.Check);
                break;
            case false:
                Ui.Chip("Differs from the game", Palette.Danger, FontAwesomeIcon.ExclamationTriangle);
                Ui.Wrapped($"{giver.Name}'s list showed other quests. A bug report (Settings, General) helps fix this.", Palette.Danger);
                break;
            default:
                using (Ui.F.Small.Push())
                    Ui.Wrapped($"Worked out from today's data. Talking to {giver.Name} checks them.", Palette.TextFaint);
                break;
        }
        ImGui.Dummy(new Vector2(0, 2 * s));
    }

    // ------------------------------------------------------------------ the shop and achievements

    /// <summary>What each rank opens in the society's shops: item icons per rank, with a mark on the ones you already have.</summary>
    private void DrawShop(TribeData data, TribeToday day)
    {
        var s = Ui.S;
        var t = day.Tribe;
        if (t.Shop.Count == 0 || Plans.Catalog is not { } catalog) return;
        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("What its ranks open");
        // One entry per item, at the earliest rank that opens it.
        var items = t.Shop.GroupBy(i => i.ItemId).Select(g => g.OrderBy(i => i.Rank < 0 ? int.MaxValue : i.Rank).First()).ToList();
        var labelW = 120 * s;
        foreach (var group in items.GroupBy(i => i.Rank).OrderBy(g => g.Key < 0 ? int.MaxValue : g.Key))
        {
            var label = group.Key switch
            {
                0 => "From the start",
                TribeShopItem.AlliedRank => "Allied",
                < 0 => "After quests",
                _ => data.RankName(t, group.Key, false),
            };
            var reached = group.Key switch
            {
                0 => day.Unlocked,
                TribeShopItem.AlliedRank => day.Allied,
                < 0 => false,
                _ => day.Rank >= group.Key,
            };
            // Placed by screen position: SameLine offsets count from the group and would add the indent again.
            var rowStart = ImGui.GetCursorScreenPos();
            using (Ui.F.Small.Push())
                ImGui.GetWindowDrawList().AddText(new Vector2(rowStart.X, rowStart.Y + (ShopIcon * s - ImGui.GetTextLineHeight()) / 2),
                    Palette.U32(reached ? Palette.Text : Palette.TextMuted), label);
            ImGui.SetCursorScreenPos(new Vector2(rowStart.X + labelW, rowStart.Y));
            ShopIcons(catalog, group.OrderBy(i => i.Kind == ShopItemKind.Other ? 1 : 0).ThenBy(i => i.Kind).ToList(), rowStart.X + labelW);
            ImGui.Dummy(new Vector2(0, 2 * s));
        }
        using (Ui.F.Small.Push())
            Ui.Wrapped("A check marks what you already have (mounts, minions, orchestrion rolls, cards and the like). Hover for the price.", Palette.TextFaint);
    }

    /// <summary>Shop icons are this big (before the interface scale).</summary>
    private const float ShopIcon = 26f;

    private static void ShopIcons(GameCatalog catalog, List<TribeShopItem> items, float left)
    {
        var s = Ui.S;
        var d = ShopIcon * s;
        var first = true;
        foreach (var item in items)
        {
            if (!first)
            {
                ImGui.SameLine(0, 4 * s);
                if (Ui.RemainingWidth() < d)
                {
                    ImGui.NewLine();
                    ImGui.SetCursorScreenPos(new Vector2(left, ImGui.GetCursorScreenPos().Y));
                }
            }
            first = false;
            var pos = ImGui.GetCursorScreenPos();
            Ui.ItemIcon(catalog.ItemIcon(item.ItemId), ShopIcon);
            var owned = item.Unlockable ? Game.Owned(item.ItemId) : null;
            if (owned == true)
            {
                // A small check badge in the corner: owned.
                var dl = ImGui.GetWindowDrawList();
                var c = pos + new Vector2(d - 4 * s, d - 4 * s);
                dl.AddCircleFilled(c, 6.5f * s, Palette.U32(Palette.Surface));
                dl.AddCircleFilled(c, 5.5f * s, Palette.U32(Palette.Accent));
                var ink = Palette.U32(Palette.OnAccent);
                dl.AddLine(c + new Vector2(-2.6f, 0.1f) * s, c + new Vector2(-0.7f, 2f) * s, ink, 1.6f * s);
                dl.AddLine(c + new Vector2(-0.7f, 2f) * s, c + new Vector2(2.8f, -1.9f) * s, ink, 1.6f * s);
            }
            if (ImGui.IsItemHovered())
            {
                var cost = item.Currency == 1 ? $"{item.Cost:N0} gil" : $"{item.Cost:N0} {(item.Cost == 1 ? catalog.ItemName(item.Currency) : catalog.ItemNamePlural(item.Currency))}";
                var gate = item.Rank < 0 && item.GateName.Length > 0 ? $"\nAfter the quest \"{item.GateName}\"." : "";
                var have = owned switch { true => "\nYou have it.", false => "\nNot yours yet.", _ => "" };
                Ui.ShowTooltip($"{catalog.ItemName(item.ItemId)}{(item.Count > 1 ? $" x{item.Count}" : "")}\n{cost}.{gate}{have}");
            }
        }
    }

    private static void DrawAchievements(TribeData data, Tribe t)
    {
        var s = Ui.S;
        if (t.Achievements.Count == 0) return;
        ImGui.Dummy(new Vector2(0, 6 * s));
        using (Ui.F.Strong.Push()) Ui.Text("Achievements");
        foreach (var a in t.Achievements)
        {
            var what = a.Intersocietal ? "the intersocietal quests" : data.RankName(t, a.Rank, false);
            var title = a.Title.Length > 0 ? $", title \"{a.Title}\"" : "";
            var state = Game.AchievementDone(a.Id) switch { true => "done", false => "not yet", _ => "open the Achievements window once to see" };
            Ui.Muted($"\"{a.Name}\" ({what}{title}): {state}");
        }
    }

    // ------------------------------------------------------------------ words

    private static string Where(string place, Vector2 map) => map == Vector2.Zero ? place : $"{place} ({map.X:0.0}, {map.Y:0.0})";

    private static string Join(IEnumerable<string> parts, string last = "and")
    {
        var list = parts.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + $" {last} " + list[^1],
        };
    }

    /// <summary>A class's name as the game spells it, each word capitalised ("Carpenter", "Dark Knight"); its abbreviation when the sheet has none.</summary>
    private static string ClassName(uint job)
    {
        var name = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(job)?.Name.ExtractText() ?? "";
        return name.Length == 0 ? ClassJobs.Abbreviation(job) : string.Join(' ', name.Split(' ').Select(TribeData.Capitalise));
    }

    private static string EmoteCommand(uint emote)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Emote>().GetRowOrDefault(emote)?.TextCommand.ValueNullable?.Command.ExtractText() is { Length: > 0 } c ? c : "an emote";

    private static string ZoneName(uint territory)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territory)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";

    private static string Until(DateTime when)
    {
        var left = when - DateTime.Now;
        if (left <= TimeSpan.Zero) return "a moment";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours} h {left.Minutes} min";
        return $"{Math.Max(1, left.Minutes)} min";
    }
}
