using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
using Provenance.Core.Planning;
using Provenance.Core.Run;
using Provenance.Core.Tribes;
using Provenance.Data;
using Provenance.Integrations;
using Provenance.Run.Gathering;
using Game = Provenance.Integrations.Tribes;

namespace Provenance.Run.Tribes;

/// <summary>
/// The allied society run: plan the day's dailies, get the crystals the crafting ones may need (bags, then retainers,
/// then gathering, else the quest is left out), then a tour of the givers. At each giver: the dailies already in the
/// journal, then the class change, the accepts, and each new daily's steps (<see cref="QuestStepper"/>; the crafting
/// through <see cref="Crafter"/>, the gathering at the quest's own nodes through <see cref="Gatherer"/>). After each
/// visit the day is planned again: a visit can change the offers (a rank-up day) and the allowances. Ticked on the
/// framework thread.
/// </summary>
/// <remarks>
/// Safety (Tribes build plan): nothing that's an addon callback is sent while the game has scene tasks pending
/// (<see cref="DeliveryWindow.SceneBusy"/>), only clicks on enabled buttons; the only Yes answered while accepting is the
/// game's own "Commence" question (Addon 102445); a quest offer that isn't the one picked is declined; the class the
/// visit takes the dailies on is put on before talking to the giver, and the stepper changes back to a quest's own class
/// after a crafting or gathering detour; /say and emotes are the stepper's (once, and one retry).
/// </remarks>
internal sealed unsafe class TribeRun
{
    private enum Step { Idle, Planning, Retainers, Gather, Tour, Finished }
    private enum Visit { Travel, Journal, SwitchJob, Accept, New, Leave }

    /// <summary>Visits in one run at most (each plans the day again; a day has 12 allowances).</summary>
    private const int MaxVisits = 40;

    private readonly Plugin plugin;
    private readonly QuestStepper stepper;
    private readonly QuestTravel travel = new();
    private readonly Crafter crafter;
    private readonly Gatherer gatherer;
    private readonly RetainerRun retainers;
    private readonly List<(DateTime At, string Text, bool Problem)> log = new();
    /// <summary>Quests the run gave up on today (Quest sheet row), with why: the plan leaves them out.</summary>
    private readonly Dictionary<uint, string> excluded = new();
    /// <summary>Givers the run couldn't get to (ENpc id), with why.</summary>
    private readonly Dictionary<uint, string> excludedGivers = new();
    /// <summary>Crystals already asked of the retainers, and gathered, this run (each is tried once).</summary>
    private readonly HashSet<uint> fromRetainers = new();
    private readonly HashSet<uint> gatheredCrystals = new();
    /// <summary>Crystals to have on hand while gathering them.</summary>
    private readonly Dictionary<uint, int> crystalWant = new();

    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime deadline;
    private int tries;
    private string? stopReason;
    private TribePlan plan = TribePlan.Empty;
    private string lastPlanLog = "";
    private string lastPlanLine = "";

    // The visit.
    private TribeVisit? visit;
    private Visit visitStep;
    private Visit afterTravel;
    private readonly Queue<PlannedQuest> queue = new();
    private readonly List<PlannedQuest> acceptedHere = new();
    private readonly HashSet<uint> skippedHere = new();
    /// <summary>Givers taken into this visit for their accepts (close by, same society: the Ixal have one per rank).</summary>
    private readonly HashSet<uint> chainedGivers = new();
    private PlannedQuest? pendingAccept;
    private DateTime pendingSince;
    private int visits;
    private int progressAtVisit;
    /// <summary>Trips back to the giver while accepting, this visit (one that isn't found where the data puts them ends it).</summary>
    private int giverTrips;
    /// <summary>Dailies left over from before the reset when the visit began (a new one found on the way plans the day again).</summary>
    private HashSet<uint> leftoversAtVisit = new();
    /// <summary>The visit ends to plan again, not for want of progress: its quests aren't left out.</summary>
    private bool replanning;

    /// <summary>Trips back to the giver while accepting, per visit, before the giver is given up on for the day.</summary>
    private const int MaxGiverTrips = 2;
    /// <summary>Givers of one society this close together (yalms, same zone) are all visited for their accepts before the quests start.</summary>
    private const float NearbyGivers = 80f;

    // The quest under way.
    private PlannedQuest? quest;
    private StepperState lastState;
    private bool crafting;
    private bool gathering;
    private bool yielding;
    private int equipTries;

    public TribeRun(Plugin plugin)
    {
        this.plugin = plugin;
        stepper = new QuestStepper(plugin, Log);
        crafter = new Crafter(plugin, Log);
        gatherer = new Gatherer(plugin, Log);
        retainers = new RetainerRun(plugin, Log);
    }

    public bool Active => step is not (Step.Idle or Step.Finished);
    public bool Working => Active && !Paused;
    public bool Paused { get; private set; }
    public bool Pausing { get; private set; }
    public string Status { get; private set; } = "";
    /// <summary>Why the run stopped, when it stopped on a problem.</summary>
    public string? Problem { get; private set; }
    /// <summary>What the player has to do before Resume (a step the run can't do, a question it won't answer); null otherwise.</summary>
    public string? Waiting { get; private set; }
    /// <summary>The wait is only a cutscene the options say to pause for.</summary>
    public bool WaitingForCutscene { get; private set; }
    public IReadOnlyList<(DateTime At, string Text, bool Problem)> Lines => log;
    /// <summary>Dailies finished since the run started.</summary>
    public int Completed { get; private set; }
    /// <summary>Dailies accepted since the run started.</summary>
    public int AcceptedCount { get; private set; }
    /// <summary>The plan the run goes by now (the rest of the day).</summary>
    public TribePlan Plan => plan;
    /// <summary>The society being visited, when there is one.</summary>
    public Tribe? Visiting => step == Step.Tour ? visit?.Tribe : null;
    /// <summary>The daily the stepper works on, when there is one.</summary>
    public TribeQuest? Doing => quest?.Quest;
    public QuestStepper Stepper => stepper;

    /// <summary>
    /// Skip cutscenes now (ECommons' AutoCutsceneSkipper, combined in Plugin.cs with the custom deliveries'): the stepper
    /// works on a quest and the options don't say to pause for them.
    /// </summary>
    public bool SkipCutscenesNow => Working && step == Step.Tour && stepper.SkipCutscenesNow;

    private TribeSettings Settings => plugin.Config.Tribes;
    private GameCatalog? Catalog => plugin.Plans.Catalog;

    // ------------------------------------------------------------------ start, stop, pause

    /// <summary>Starts the run; a reason when it can't.</summary>
    public string? Start()
    {
        if (Active) return null;
        if (plugin.Runs.Active) return "An order run is going; stop it first.";
        if (plugin.CosmicLoop.Active) return "The Cosmic Exploration loop is going; stop it first.";
        if (plugin.DeliveryLoop.Active) return "The custom deliveries are going; stop them first.";
        if (!Services.ClientState.IsLoggedIn || Services.Objects.LocalPlayer == null) return "Log in first.";
        if (!Vnav.Available) return "vnavmesh isn't loaded; it walks to the givers, the quests' targets and the nodes.";
        if (Catalog?.Tribes is not { Tribes.Count: > 0 }) return "Still reading the game data.";
        if (!Game.Ready) return "Your character's quest data isn't readable yet; try again in a moment.";
        var first = plugin.TribePlanning.Build();
        if (first.Count == 0) return NothingToDo(first);
        if (first.Quests.Any(q => q.Recipe != null) && Raphael.ExecutablePath() == null)
            return "Provenance's crafting solver (raphael-cli.exe) is missing next to it, so the crafting dailies can't be done. Reinstalling Provenance brings it back.";

        plugin.Config.LastLoop = "tribes";
        // Tribes has been used: the hand watch may run from now on (when its option is on).
        Settings.Seen = true;
        plugin.Config.Save();
        log.Clear();
        excluded.Clear();
        excludedGivers.Clear();
        fromRetainers.Clear();
        gatheredCrystals.Clear();
        crystalWant.Clear();
        Problem = null;
        Waiting = null;
        WaitingForCutscene = false;
        Completed = AcceptedCount = 0;
        visits = 0;
        visit = null;
        quest = null;
        crafting = gathering = false;
        Paused = Pausing = false;
        stopReason = null;
        lastPlanLog = lastPlanLine = "";
        plan = first;
        Travel.AdvancedUnstuck = plugin.Config.Security.AdvancedUnstuck;
        Travel.StuckAfter = TimeSpan.FromSeconds(Math.Clamp(plugin.Config.Security.StuckSeconds, 3, 30));
        DebugLog.BeginSession("tribes");
        DebugLog.Write("tribes", $"run: goal {Settings.Goal?.ToString() ?? "automatic"} ({first.Goal}), left out [{string.Join(",", Settings.LeftOut)}], "
                                 + $"pause for cutscenes {Settings.PauseForCutscenes}, leave out /say {Settings.LeaveOutSay}, {Game.AllowancesLeft} allowances, seed {Game.Seed}, "
                                 + $"job {GameState.CurrentJob}, cutscene skipper hooked {DeliveryWindow.CutsceneSkipperHooked?.ToString() ?? "unknown"}");
        Log("Started.", false);
        Go(Step.Planning);
        return null;
    }

    /// <summary>Why a plan with nothing in it can't start, in plain words.</summary>
    private static string NothingToDo(TribePlan p)
    {
        if (p.Blocked != null && !p.Quests.Any())
            return Game.AllowancesLeft <= 0 ? $"No allowances left today; they come back at {Game.NextReset:HH:mm}." : p.Blocked;
        var reasons = p.LeftOut.Select(l => $"{l.Quest.Name}: {l.Reason}").Take(3).ToList();
        if (reasons.Count == 0 && p.Notices.Count > 0) reasons.AddRange(p.Notices.Select(n => $"{n.Tribe.Name}: {n.Text}").Take(2));
        return "Nothing the run can do today" + (reasons.Count > 0 ? " (" + string.Join("; ", reasons) + ")." : ": no society is unlocked and ticked, or today's dailies are done.");
    }

    /// <summary>Stops: right away, except mid-craft, where the item under way is finished first (its materials would be lost).</summary>
    public void Stop(string reason = "Stopped.", bool problem = false)
    {
        if (!Active) return;
        if (crafting && crafter.MidCraft && !problem && !Paused)
        {
            stopReason = reason;
            crafter.RequestPause();
            Status = "Stopping after this craft";
            return;
        }
        Finish(reason, problem);
    }

    /// <summary>Pauses after the item being crafted, the node being gathered or the retainer open; otherwise right away.</summary>
    public void Pause()
    {
        if (!Active || Paused || Pausing) return;
        if (crafting && crafter.Started && !crafter.Finished)
        {
            crafter.RequestPause();
            Pausing = true;
            Status = "Pausing after this craft";
            Log("Pause asked for; finishing the item being crafted first.", false);
            return;
        }
        if ((gathering || step == Step.Gather) && (gatherer.OnNode || Services.Condition[ConditionFlag.Gathering]))
        {
            Pausing = true;
            Status = "Pausing after this node";
            Log("Pause asked for; finishing the node first.", false);
            return;
        }
        if (step == Step.Retainers)
        {
            retainers.RequestPause();
            Pausing = true;
            Status = "Pausing after this retainer";
            return;
        }
        PauseNow();
    }

    private void PauseNow()
    {
        travel.Cancel();
        stepper.Pause();
        if (gathering || step == Step.Gather) gatherer.Pause();
        Vnav.Stop();
        Pausing = false;
        Paused = true;
        Status = "Paused";
        Log("Paused.", false);
    }

    /// <summary>Carries on where it paused: the quest's step (after the player did it), the craft, the node, the trip.</summary>
    public void Resume()
    {
        if (!Active || !(Paused || Pausing)) return;
        var wasPaused = Paused;
        Paused = Pausing = false;
        Waiting = null;
        WaitingForCutscene = false;
        Log("Resumed.", false);
        if (!wasPaused)
        {
            // Never mind the pause asked for.
            if (crafting) crafter.Resume();
            if (step == Step.Retainers) retainers.Resume();
            return;
        }
        equipTries = 0;
        if (stepper.State == StepperState.WaitingForUser || stepper.Paused) stepper.Resume();
        if (crafting && crafter.IsPaused) crafter.Resume();
        if (gathering || step == Step.Gather) gatherer.Resume();
        if (step == Step.Retainers) retainers.Resume();
        if (step == Step.Tour && visitStep == Visit.Travel) TravelToGiver(afterTravel);
        // The visit's step afresh: its tries and its time limit start over (a long pause isn't a giver that doesn't answer).
        else if (step == Step.Tour && visit != null && visitStep is Visit.SwitchJob or Visit.Accept or Visit.Leave) To(visitStep);
        Wait(0.3);
    }

    private void Finish(string reason, bool problem)
    {
        Paused = Pausing = false;
        Waiting = null;
        WaitingForCutscene = false;
        stepper.Stop();
        travel.Cancel();
        if (step == Step.Retainers) retainers.Cancel();
        gatherer.Pause();
        gatherer.Stop();
        crafting = gathering = false;
        quest = null;
        visit = null;
        if (problem)
        {
            Problem = reason;
            Notify(reason, true);
        }
        Log(reason, problem);
        Status = problem ? "Stopped: " + reason : reason;
        DebugLog.Write("tribes", $"run ends: {Completed} done, {AcceptedCount} accepted, {visits} visits; given up on [{string.Join(", ", excluded.Select(e => $"{e.Key - 0x10000}: {e.Value}"))}]");
        step = Step.Finished;
    }

    /// <summary>
    /// The player has to do something (a step the run can't do, a question it won't answer, a craft that couldn't
    /// start): the run pauses, says what, and Resume carries on after it.
    /// </summary>
    /// <param name="logged">The stepper already wrote it to the run's log.</param>
    private void AwaitUser(string what, bool cutscene = false, bool logged = false)
    {
        travel.Cancel();
        if (gathering || step == Step.Gather) gatherer.Pause();
        Paused = true;
        Pausing = false;
        Waiting = what;
        WaitingForCutscene = cutscene;
        Status = cutscene ? "Paused for the cutscene" : "Waiting for you";
        DebugLog.Write("tribes", $"[!] waiting for the player{(quest != null ? $" ({quest.Quest.Number})" : "")}: {what}");
        if (!logged) Log(what, !cutscene);
        Notify(cutscene ? "Paused for a quest cutscene; Resume on the Tribes page carries on." : what, !cutscene);
    }

    public void Tick()
    {
        if (!Active || Paused) return;
        try
        {
            if (stopReason != null || Pausing && crafting)
            {
                crafter.Tick();
                if (!(crafter.IsPaused || crafter.Finished)) return;
                if (stopReason is { } reason) { stopReason = null; Finish(reason, false); return; }
                Pausing = false;
                Paused = true;
                Status = "Paused";
                Log("Paused.", false);
                return;
            }
            if (Pausing && step == Step.Retainers)
            {
                retainers.Tick();
                if (retainers.IsPaused || retainers.Finished) PauseNow();
                return;
            }
            if (Pausing && (gathering || step == Step.Gather))
            {
                gatherer.Tick();
                if (!gatherer.OnNode && !Services.Condition[ConditionFlag.Gathering]) PauseNow();
                return;
            }
            if (Pausing) { PauseNow(); return; }

            // The stepper, the crafter and the gatherer need every frame.
            if (step == Step.Tour && visitStep is Visit.Journal or Visit.New) { TickQuests(); return; }
            if (step == Step.Gather) { TickGatherCrystals(); return; }
            if (step == Step.Retainers) { TickRetainers(); return; }
            if (DateTime.UtcNow < waitUntil) return;
            switch (step)
            {
                case Step.Planning: TickPlanning(); break;
                case Step.Tour: TickTour(); break;
            }
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Allied society run tick failed");
            Finish("Something went wrong: " + e.Message, true);
        }
    }

    // ------------------------------------------------------------------ planning and crystals

    private void TickPlanning()
    {
        plan = plugin.TribePlanning.Build(excluded, excludedGivers);
        NotePlan(plan);
        if (plan.Visits.Count == 0)
        {
            Finish(Completed == 0 && AcceptedCount == 0
                ? NothingToDo(plan)
                : $"Done: {Completed} {(Completed == 1 ? "daily" : "dailies")}, {Game.AllowancesLeft} {(Game.AllowancesLeft == 1 ? "allowance" : "allowances")} left today.", false);
            return;
        }

        // Crystals the crafting dailies may use: the bags and the crystal bag, then the retainers, then gathering.
        var needs = plugin.TribePlanning.Crystals(plan).Where(n => n.Short > 0).ToList();
        if (needs.Count > 0)
        {
            DebugLog.Write("tribes", "crystals short: " + string.Join(", ", needs.Select(n => $"{n.ItemId} need {n.Needed}, have {n.OnHand}, retainers {n.Retainers}")));
            var fromThem = needs.Where(n => n.Retainers > 0 && !fromRetainers.Contains(n.ItemId)).ToList();
            if (fromThem.Count > 0 && RetainerWork(fromThem) is { Count: > 0 } work)
            {
                foreach (var n in fromThem) fromRetainers.Add(n.ItemId);
                Log("Taking crystals from your retainers: " + string.Join(", ", fromThem.Select(n => $"{Math.Min(n.Short, n.Retainers)} {Catalog!.ItemNamePlural(n.ItemId)}")) + ".", false);
                retainers.Begin(work);
                Go(Step.Retainers);
                Status = "Taking crystals from your retainers";
                return;
            }
            var toGather = needs.Where(n => !gatheredCrystals.Contains(n.ItemId) && CanGather(n.ItemId)).ToList();
            if (toGather.Count > 0)
            {
                foreach (var n in toGather)
                {
                    gatheredCrystals.Add(n.ItemId);
                    crystalWant[n.ItemId] = n.Needed;
                }
                Log("Gathering crystals: " + string.Join(", ", toGather.Select(n => $"{n.Short} {Catalog!.ItemNamePlural(n.ItemId)}")) + ".", false);
                gatherer.Begin(toGather.Select(n => new GatherTarget(n.ItemId, Supply.Gather, n.Short, (uint)n.Needed)),
                    t => Math.Max(0, crystalWant.GetValueOrDefault(t.ItemId) - Game.OnHand(t.ItemId)), () => new HashSet<uint>());
                Go(Step.Gather);
                Status = "Gathering crystals";
                return;
            }
            // Still short: the dailies not accepted yet that the crystals don't cover are left out.
            if (LeaveOutShort(plan)) { Wait(0.1); return; }
        }
        BeginVisit(plan.Visits[0]);
    }

    /// <summary>A gatherer of the player's can get the crystal somewhere (a node at its level, with a gearset).</summary>
    private bool CanGather(uint crystal)
        => Catalog!.GatherPoints(crystal).Any(p => PlanService.JobLevel(p.ClassJobId) >= p.Level && GameState.BestGearset(p.ClassJobId) >= 0
                                                    && Teleporter.CanReach(p.TerritoryId));

    /// <summary>
    /// Shares out the crystals on hand among the plan's crafting dailies in tour order, and leaves out the dailies to
    /// accept that come up short (a daily in the journal goes ahead: it may need fewer crafts than counted). True when
    /// something was left out.
    /// </summary>
    private bool LeaveOutShort(TribePlan p)
    {
        var have = p.Crystals().Keys.ToDictionary(id => id, Game.OnHand);
        var any = false;
        foreach (var q in p.Quests)
        {
            var missing = q.Crystals.Where(c => have.GetValueOrDefault(c.ItemId) < c.Amount).ToList();
            if (missing.Count == 0 || q.Accepted)
            {
                foreach (var (id, amount) in q.Crystals) have[id] = Math.Max(0, have.GetValueOrDefault(id) - amount);
                continue;
            }
            var why = "Not enough crystals for its crafts: up to " + string.Join(" and ", missing.Select(c => $"{c.Amount} {Catalog!.ItemNamePlural(c.ItemId)}"))
                      + $" (you have {string.Join(" and ", missing.Select(c => Game.OnHand(c.ItemId).ToString("N0")))}), and no more could be taken from your retainers or gathered";
            excluded[q.Quest.RowId] = why;
            Log($"Left out \"{q.Quest.Name}\": {why}.", true);
            any = true;
        }
        return any;
    }

    /// <summary>What to take from which retainer for the crystals short (as the retainers were last seen).</summary>
    private List<(ulong Retainer, string Name, List<(uint Item, int Amount)> Items)> RetainerWork(List<CrystalNeed> needs)
    {
        var result = new Dictionary<ulong, (string Name, List<(uint, int)> Items)>();
        if (!Services.PlayerState.IsLoaded || RetainerCache.For(plugin.Config, Services.PlayerState.ContentId) is not { } all) return new();
        foreach (var n in needs)
        {
            var need = n.Short;
            foreach (var (id, stock) in all.OrderByDescending(r => r.Value.Items.GetValueOrDefault(n.ItemId)))
            {
                if (need <= 0) break;
                var have = stock.Items.GetValueOrDefault(n.ItemId);
                if (have <= 0) continue;
                var take = Math.Min(have, need);
                if (!result.TryGetValue(id, out var entry)) result[id] = entry = (stock.Name, new List<(uint, int)>());
                entry.Items.Add((n.ItemId, take));
                need -= take;
            }
        }
        return result.Select(kv => (kv.Key, kv.Value.Name, kv.Value.Items)).ToList();
    }

    private void TickRetainers()
    {
        if (DateTime.UtcNow < waitUntil) return;
        retainers.Tick();
        Status = retainers.Status;
        if (!retainers.Finished) return;
        if (retainers.Failure is { } why) Log("Retainers: " + why, true);
        Go(Step.Planning);
    }

    private void TickGatherCrystals()
    {
        gatherer.Tick();
        Status = gatherer.Status;
        if (gatherer.Failure is { } why)
        {
            Log("Gathering crystals: " + why, true);
            StopGatherer();
            Go(Step.Planning);
            return;
        }
        var done = crystalWant.All(kv => Game.OnHand(kv.Key) >= kv.Value);
        // Only closed timed nodes for what's left: no waiting for their window.
        if (!done && gatherer.Waiting && !gatherer.WaitingToRetry && !gatherer.OnNode)
        {
            Log("No node for the crystals left is up now; going on with what's in the bags.", true);
            StopGatherer();
            Go(Step.Planning);
            return;
        }
        if (!done || gatherer.OnNode || gatherer.UpkeepBusy || Services.Condition[ConditionFlag.Gathering]) return;
        StopGatherer();
        Log("Gathered the crystals.", false);
        Go(Step.Planning);
    }

    private void StopGatherer()
    {
        gatherer.Pause();
        gatherer.Stop();
    }

    /// <summary>Writes the plan to the debug log when it changed, and a line of it to the run's log.</summary>
    private void NotePlan(TribePlan p)
    {
        var lines = new List<string>
        {
            $"plan: goal {p.Goal}, {p.Allowances} allowances, {p.ToAccept} to accept, {p.Count} in all" + (p.Blocked != null ? $", no new dailies: {p.Blocked}" : ""),
        };
        foreach (var v in p.Visits)
            lines.Add($"  visit {v.Tribe.Name}, giver {v.Giver.NpcId} ({v.Giver.Name}) as {ClassJobs.Abbreviation(v.ClassJob)}: "
                      + string.Join("; ", v.Quests.Select(q => $"{q.Quest.Number} \"{q.Quest.Name}\"" + (q.Accepted ? $" in the journal as {ClassJobs.Abbreviation(q.ClassJob)}{(q.Leftover ? ", left over" : "")}" : "")
                                                               + (q.Crystals.Count > 0 ? $", crystals {string.Join("+", q.Crystals.Select(c => $"{c.Amount}x{c.ItemId}"))}" : ""))));
        foreach (var l in p.LeftOut) lines.Add($"  left out {l.Quest.Number} \"{l.Quest.Name}\" ({l.Tribe.Name}): {l.Reason}");
        foreach (var n in p.Notices) lines.Add($"  notice {n.Tribe.Name}: {n.Text}");
        var text = string.Join("\n", lines);
        if (text == lastPlanLog) return;
        lastPlanLog = text;
        foreach (var l in lines) DebugLog.Write("tribes", l);

        if (p.Count == 0) return;
        var summary = $"Plan: {p.Count} {(p.Count == 1 ? "daily" : "dailies")} ({p.ToAccept} to accept) at "
                      + string.Join(", ", p.Visits.GroupBy(v => v.Tribe.Id).Select(g => $"{g.First().Tribe.Name} ({g.Sum(v => v.Quests.Count)})")) + ".";
        if (summary == lastPlanLine) return;
        lastPlanLine = summary;
        Log(summary, false);
    }

    // ------------------------------------------------------------------ the tour

    private void BeginVisit(TribeVisit v)
    {
        if (++visits > MaxVisits)
        {
            Finish($"Stopped after {MaxVisits} visits: something keeps the dailies from getting done (see the log).", true);
            return;
        }
        visit = v;
        queue.Clear();
        acceptedHere.Clear();
        skippedHere.Clear();
        chainedGivers.Clear();
        chainedGivers.Add(v.Giver.NpcId);
        pendingAccept = null;
        quest = null;
        tries = 0;
        giverTrips = 0;
        replanning = false;
        leftoversAtVisit = Leftovers();
        progressAtVisit = Completed + AcceptedCount;
        foreach (var q in v.InJournal) queue.Enqueue(q);
        var parts = new List<string>();
        if (v.ToAccept.Any())
            parts.Add($"accept {string.Join(", ", v.ToAccept.Select(q => $"\"{q.Quest.Name}\""))} as {ClassJobs.Abbreviation(v.ClassJob)}");
        if (v.InJournal.Any())
            parts.Add($"finish {string.Join(", ", v.InJournal.Select(q => $"\"{q.Quest.Name}\""))}");
        Log($"To {v.Giver.Name} ({v.Tribe.Name}): {string.Join("; ", parts)}.", false);
        DebugLog.Write("tribes", $"visit {visits}: {v.Tribe.Name} giver {v.Giver.NpcId} in {v.Giver.Territory} at {v.Giver.Position.X:0},{v.Giver.Position.Y:0},{v.Giver.Position.Z:0}, "
                                 + $"class {v.ClassJob}, to accept [{string.Join(",", v.ToAccept.Select(q => q.Quest.Number))}], in the journal [{string.Join(",", v.InJournal.Select(q => q.Quest.Number))}]");
        Go(Step.Tour);
        TravelToGiver(queue.Count > 0 ? Visit.Journal : Visit.SwitchJob);
    }

    private void TravelToGiver(Visit then)
    {
        var g = visit!.Giver;
        afterTravel = then;
        visitStep = Visit.Travel;
        travel.OnFoot = travel.NoFly = travel.KeepMount = false;
        travel.GoTo(g.Name, g.Territory, g.Position, 3f, () => Interact.Nearest(ObjectKind.EventNpc, id => id == g.NpcId)?.Position);
        Status = "On the way to " + g.Name;
    }

    /// <summary>On to a part of the visit: its tries start over, with a time limit (a short one for leaving).</summary>
    private void To(Visit next)
    {
        visitStep = next;
        tries = 0;
        deadline = DateTime.UtcNow.AddSeconds(next == Visit.Leave ? 20 : 90);
    }

    private void TickTour()
    {
        if (visit == null) { Go(Step.Planning); return; }
        switch (visitStep)
        {
            case Visit.Travel: TickTravel(); return;
            case Visit.SwitchJob: TickSwitchJob(); return;
            case Visit.Accept: TickAccept(); return;
            case Visit.Leave: TickLeave(); return;
        }
    }

    private void TickTravel()
    {
        if (QuestSteps.InCutscene) { Wait(0.5); return; }
        travel.Tick();
        Status = travel.Status;
        var g = visit!.Giver;
        if (travel.Current == QuestTravel.State.Failed)
        {
            var why = $"Couldn't get to {g.Name}: {travel.Failure}";
            excludedGivers[g.NpcId] = why;
            Log(why, true);
            visit = null;
            Go(Step.Planning);
            Wait(1);
            return;
        }
        if (travel.Current != QuestTravel.State.Arrived) return;
        To(afterTravel == Visit.SwitchJob && !visit.ToAccept.Any() ? Visit.Leave : afterTravel);
    }

    /// <summary>The visit's class on before talking to the giver: the dailies lock to the class they're accepted on.</summary>
    private void TickSwitchJob()
    {
        var job = visit!.ClassJob;
        if (GameState.CurrentJob == job)
        {
            To(Visit.Accept);
            return;
        }
        if (DateTime.UtcNow > deadline) { FailAccepts($"couldn't change to {ClassJobs.Abbreviation(job)} (the game stayed busy)"); return; }
        if (GameState.InCraftingStance) { Interact.Close("RecipeNote"); Wait(1); return; }
        if (QuestSteps.Busy) { Wait(0.5); return; }
        var set = GameState.BestGearset(job);
        if (set < 0) { FailAccepts($"there's no gearset for {ClassJobs.Abbreviation(job)} to change to"); return; }
        if (++tries > 3) { FailAccepts($"couldn't change to {ClassJobs.Abbreviation(job)}"); return; }
        DebugLog.Write("tribes", $"changing to gearset {set + 1} ({ClassJobs.Abbreviation(job)}) to accept");
        GameState.EquipGearset(set);
        Status = $"Changing to {ClassJobs.Abbreviation(job)}";
        Wait(2);
    }

    /// <summary>The dailies to accept here couldn't be: left out for today, and on.</summary>
    private void FailAccepts(string why)
    {
        var text = $"{visit!.Giver.Name}: {why}";
        foreach (var q in visit.ToAccept.Where(q => !QuestSteps.Accepted(q.Quest.RowId))) excluded.TryAdd(q.Quest.RowId, Capital(why));
        Log(text, true);
        queue.Clear();
        foreach (var q in acceptedHere) queue.Enqueue(q);
        To(queue.Count > 0 ? Visit.New : Visit.Leave);
    }

    private static readonly Lazy<string> DailyListTitle = new(() =>
        StepRules.Normal(Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(102442)?.Text.ExtractText() ?? ""));

    /// <summary>
    /// At the giver: talk, pick each planned daily in its "Daily Quests" list, accept it (declining any other offer),
    /// and answer the game's own "Commence" question for a planned daily; anything else is the player's. The class is
    /// checked again right before Accept and Commence (a daily locks to the class it's accepted on).
    /// </summary>
    private void TickAccept()
    {
        var v = visit!;
        var g = v.Giver;
        if (DeliveryWindow.SceneBusy)
        {
            // Scene tasks pending: wait, but not past the step's time limit.
            if (DateTime.UtcNow > deadline) { FailAccepts("the game stayed busy at the giver"); return; }
            Wait(0.2);
            return;
        }
        if (GameState.CurrentJob != v.ClassJob && !QuestSteps.Busy && Interact.Menu() == null && Interact.Ready("JournalAccept") == null
            && Interact.Ready("SelectYesno") == null)
        {
            To(Visit.SwitchJob);
            return;
        }
        Status = $"Accepting dailies from {g.Name}";

        // A daily just accepted: count it.
        if (pendingAccept is { } p)
        {
            if (QuestSteps.Accepted(p.Quest.RowId))
            {
                acceptedHere.Add(p);
                AcceptedCount++;
                Log($"Accepted \"{p.Quest.Name}\" as {ClassJobs.Abbreviation(GameState.CurrentJob)}.", false);
                DebugLog.Write("tribes", $"accepted {p.Quest.Number} as {GameState.CurrentJob} (accept class {QuestSteps.AcceptJob(p.Quest.RowId)}), {Game.AllowancesLeft} allowances left");
                pendingAccept = null;
                tries = 0;
            }
            else if (DateTime.UtcNow - pendingSince > TimeSpan.FromSeconds(10)) pendingAccept = null;
        }

        // A daily left over from before the reset turned up (the giver's list showed none of today's): the game takes
        // no new ones until it's done, so the day is planned again with it first.
        if (Leftovers().Except(leftoversAtVisit).ToList() is { Count: > 0 } left)
        {
            var names = left.Select(r => Catalog?.Tribes is { } td && td.Quest(r) is { } tq ? $"\"{tq.Name}\"" : $"{r - 0x10000}");
            Log($"{string.Join(", ", names)} {(left.Count == 1 ? "is" : "are")} left over from before the daily reset; planning again.", false);
            DebugLog.Write("tribes", $"leftover found at {g.Name}: [{string.Join(",", left.Select(r => r - 0x10000))}]; planning again");
            replanning = true;
            To(Visit.Leave);
            return;
        }

        if (Interact.Ready("JournalAccept") != null)
        {
            var title = StepRules.Normal(TribeData.CleanName(QuestSteps.OfferTitle() ?? ""));
            var mine = v.ToAccept.FirstOrDefault(q => !QuestSteps.Accepted(q.Quest.RowId)
                                                      && StepRules.Normal(TribeData.CleanName(q.Quest.Name)).Equals(title, StringComparison.CurrentCultureIgnoreCase));
            if (mine == null || Game.AllowancesLeft <= 0)
            {
                DebugLog.Write("tribes", $"offer \"{title}\" declined ({(mine == null ? "not in the plan" : "no allowances left")})");
                QuestSteps.Decline();
                if (mine != null) { FailAccepts("no allowances are left"); return; }
                Wait(1);
                return;
            }
            // Not on the visit's class (changed meanwhile): declined, and the class put on first.
            if (GameState.CurrentJob != v.ClassJob)
            {
                DebugLog.Write("tribes", $"offer \"{title}\" declined: on {GameState.CurrentJob}, not {v.ClassJob}");
                QuestSteps.Decline();
                To(Visit.SwitchJob);
                Wait(1);
                return;
            }
            if (QuestSteps.Accept())
            {
                DebugLog.Write("tribes", $"accept \"{title}\"");
                pendingAccept = mine;
                pendingSince = DateTime.UtcNow;
            }
            Wait(1);
            return;
        }

        if (QuestSteps.Question() is { } question)
        {
            // "Commence “…”?" for a daily of this visit's plan, on the visit's class: Yes. Anything else is the player's.
            var commenced = StepRules.Filled(question, QuestSteps.CommenceQuestion);
            var planned = commenced != null && v.ToAccept.Any(q => !QuestSteps.Accepted(q.Quest.RowId)
                                                                   && StepRules.Normal(TribeData.CleanName(q.Quest.Name)).Equals(StepRules.Normal(TribeData.CleanName(commenced)), StringComparison.CurrentCultureIgnoreCase));
            if (planned && GameState.CurrentJob == v.ClassJob)
            {
                if (QuestSteps.Yes()) DebugLog.Write("tribes", $"question \"{question}\" -> Yes");
                Wait(0.8);
                return;
            }
            if (planned)
            {
                // Not on the visit's class (changed meanwhile): No, and the class put on first.
                DebugLog.Write("tribes", $"question \"{question}\" -> No: on {GameState.CurrentJob}, not {v.ClassJob}");
                Interact.No();
                To(Visit.SwitchJob);
                Wait(1);
                return;
            }
            DebugLog.Write("tribes", $"[!] {(commenced != null ? "a daily not in the plan in the" : "unknown")} question while accepting: \"{question}\"");
            AwaitUser($"The game asks “{question}”; answer it yourself, then resume.");
            return;
        }
        if (Interact.SkipTalk()) { Wait(0.3); return; }

        var next = v.ToAccept.FirstOrDefault(q => !QuestSteps.Accepted(q.Quest.RowId) && !skippedHere.Contains(q.Quest.RowId) && q != pendingAccept);
        if (next == null)
        {
            if (pendingAccept != null) { Wait(0.3); return; }
            // The list closed before the quests start (the stepper would take it for a menu of the quest's).
            if (DateTime.UtcNow < deadline && CloseGiverWindows()) return;
            // Another giver of the society close by with dailies to accept: those first, then all the quests.
            if (Game.AllowancesLeft > 0 && NextNearby(v) is { } more)
            {
                chainedGivers.Add(more.Giver.NpcId);
                Log($"Also to {more.Giver.Name} close by: accept {string.Join(", ", more.ToAccept.Select(q => $"\"{q.Quest.Name}\""))} as {ClassJobs.Abbreviation(more.ClassJob)}.", false);
                DebugLog.Write("tribes", $"visit goes on to giver {more.Giver.NpcId} ({Vector3.Distance(more.Giver.Position, v.Giver.Position):0} yalms away) to accept [{string.Join(",", more.ToAccept.Select(q => q.Quest.Number))}] "
                                         + $"before the quests; accepted so far [{string.Join(",", acceptedHere.Select(q => q.Quest.Number))}]");
                visit = more;
                skippedHere.Clear();
                pendingAccept = null;
                giverTrips = 0;
                TravelToGiver(Visit.SwitchJob);
                return;
            }
            queue.Clear();
            foreach (var q in acceptedHere) queue.Enqueue(q);
            To(queue.Count > 0 ? Visit.New : Visit.Leave);
            return;
        }

        if (QuestSteps.Menu() is { } menu)
        {
            if (QuestSteps.PickDaily(next.Quest.Name, out var missing))
            {
                DebugLog.Write("tribes", $"picked \"{next.Quest.Name}\" in {g.Name}'s list");
                Wait(1);
                return;
            }
            if (!missing) { Wait(0.3); return; }
            var entries = menu.Entries.Select(StepRules.Normal).ToList();
            var dailies = v.Tribe.DailiesOf(g.NpcId).Select(q => StepRules.Normal(TribeData.CleanName(q.Name))).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            var isList = StepRules.Normal(menu.Prompt).Equals(DailyListTitle.Value, StringComparison.CurrentCultureIgnoreCase)
                         || entries.Any(dailies.Contains);
            if (isList)
            {
                // The list doesn't have it (taken elsewhere today, or the giver offers others): not today.
                var why = $"{g.Name}'s list didn't have it";
                excluded[next.Quest.RowId] = why;
                skippedHere.Add(next.Quest.RowId);
                Log($"\"{next.Quest.Name}\": {why}.", true);
                DebugLog.Write("tribes", $"[!] list of {g.Name}: [{string.Join(" / ", entries)}] has no \"{next.Quest.Name}\"");
                return;
            }
            // A menu in front of the list: its "Daily Quests" entry.
            var at = DailyListTitle.Value.Length > 0 ? entries.FindIndex(e => e.Equals(DailyListTitle.Value, StringComparison.CurrentCultureIgnoreCase)) : -1;
            if (at >= 0)
            {
                DebugLog.Write("tribes", $"menu of {g.Name}: [{string.Join(" / ", entries)}] -> \"{entries[at]}\"");
                menu.Select(at);
                Wait(1);
                return;
            }
            DebugLog.Write("tribes", $"[!] menu of {g.Name}: \"{menu.Prompt}\" [{string.Join(" / ", entries)}] has no daily quests");
            FailAccepts($"the menu showed no daily quests ({string.Join(" / ", entries)})");
            return;
        }

        // Nothing open: talk to the giver (once any event of the last talk is over).
        if (QuestSteps.Busy) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline || ++tries > 6) { FailAccepts("the daily quest list didn't open"); return; }
        var npc = Interact.Nearest(ObjectKind.EventNpc, id => id == g.NpcId);
        if (npc == null || Interact.Distance(npc) > 6f)
        {
            // Back to the giver, a couple of times; one who isn't found where the data puts them ends the visit.
            if (++giverTrips > MaxGiverTrips)
            {
                var why = $"{g.Name} wasn't found to talk to where the game data puts them";
                excludedGivers[g.NpcId] = why;
                DebugLog.Write("tribes", $"[!] {why} ({(npc == null ? "not in view" : $"{Interact.Distance(npc):0.0} yalms away")}) after {MaxGiverTrips} trips back; giver left out today");
                FailAccepts(why);
                return;
            }
            TravelToGiver(Visit.Accept);
            return;
        }
        Interact.With(npc);
        DebugLog.Write("tribes", $"talk to {g.Name} ({g.NpcId}) {Interact.Distance(npc):0.0} yalms away, try {tries}");
        Wait(1.5);
    }

    /// <summary>
    /// Leaving the giver the way a player does: the offer declined, the menu's last entry ("Nothing", "Cancel") when it
    /// isn't one of the society's quests (otherwise the list is closed), the talk over; then the day is planned again.
    /// </summary>
    private void TickLeave()
    {
        if (DateTime.UtcNow > deadline)
        {
            DebugLog.Write("tribes", $"leaving {visit?.Giver.Name}: windows still open after a while; going on");
            AfterVisit();
            return;
        }
        if (CloseGiverWindows()) return;
        if (Services.Condition[ConditionFlag.OccupiedInEvent] || Services.Condition[ConditionFlag.OccupiedInQuestEvent]) { Wait(0.3); return; }
        AfterVisit();
    }

    /// <summary>
    /// The giver's windows closed the way a player does: talk clicked through, an offer declined, a menu left by its
    /// own "Nothing" / "Cancel" entry (the game's texts), otherwise the menu closed. Nothing is sent while scene tasks
    /// are pending. True while something was open.
    /// </summary>
    private bool CloseGiverWindows()
    {
        if (Interact.SkipTalk()) { Wait(0.3); return true; }
        if (DeliveryWindow.SceneBusy || Interact.Ready("SelectYesno") != null) { Wait(0.3); return true; }
        if (Interact.Ready("JournalAccept") != null) { QuestSteps.Decline(); Wait(0.8); return true; }
        if (Interact.Menu() is not { Count: > 0 } menu) return false;
        var leave = StepRules.LeaveEntry(menu.Select(e => e.Text).ToList(), QuestSteps.LeaveTexts);
        if (leave >= 0) menu[leave].Select();
        else
        {
            Interact.Close("SelectIconString");
            Interact.Close("SelectString");
        }
        Wait(0.8);
        return true;
    }

    /// <summary>
    /// The next visit of the plan to the same society whose giver stands close by in the same zone (the Ixal have one
    /// giver per rank, side by side), with dailies to accept and nothing of its own in the journal: its accepts join
    /// this visit, so the quests aren't fetched one giver at a time.
    /// </summary>
    private TribeVisit? NextNearby(TribeVisit v)
    {
        var after = false;
        foreach (var w in plan.Visits)
        {
            if (!after) { after = ReferenceEquals(w, v); continue; }
            if (chainedGivers.Contains(w.Giver.NpcId) || excludedGivers.ContainsKey(w.Giver.NpcId)) continue;
            if (w.Tribe.Id != v.Tribe.Id || w.Giver.Territory != v.Giver.Territory) continue;
            if (Vector3.Distance(w.Giver.Position, v.Giver.Position) > NearbyGivers) continue;
            if (w.InJournal.Any() || !w.ToAccept.Any(q => !QuestSteps.Accepted(q.Quest.RowId) && !excluded.ContainsKey(q.Quest.RowId))) continue;
            return w;
        }
        return null;
    }

    /// <summary>The society dailies in the journal left over from before the reset (Quest sheet rows).</summary>
    private HashSet<uint> Leftovers()
        => Catalog?.Tribes is { } data ? Game.Accepted(data).Where(a => a.Leftover).Select(a => a.Quest.RowId).ToHashSet() : new HashSet<uint>();

    private void AfterVisit()
    {
        var v = visit;
        deadline = default;
        if (replanning)
        {
            replanning = false;
            DebugLog.Write("tribes", $"visit to {v?.Giver.Name} ends to plan the day again");
        }
        else if (v != null && Completed + AcceptedCount == progressAtVisit)
        {
            // Nothing came of it: not planned again today.
            foreach (var q in v.Quests)
                excluded.TryAdd(q.Quest.RowId, $"Nothing came of the visit to {v.Giver.Name} (see the log)");
            DebugLog.Write("tribes", $"[!] visit to {v.Giver.Name} made no progress; its quests are left out for today");
        }
        visit = null;
        quest = null;
        Go(Step.Planning);
        Wait(0.5);
    }

    // ------------------------------------------------------------------ the quests

    /// <summary>The visit's quests one by one, through the stepper (every frame: it, the crafter and the gatherer need it).</summary>
    private void TickQuests()
    {
        if (quest == null)
        {
            if (DateTime.UtcNow < waitUntil) return;
            if (queue.Count == 0)
            {
                // The ones in the journal done: now the accepts (when there are any). The new ones done: leave.
                To(visitStep == Visit.Journal && visit!.ToAccept.Any() ? Visit.SwitchJob : Visit.Leave);
                return;
            }
            var next = queue.Dequeue();
            if (!QuestSteps.Accepted(next.Quest.RowId))
            {
                DebugLog.Write("tribes", $"{next.Quest.Number} isn't in the journal any more; skipped");
                return;
            }
            quest = next;
            lastState = StepperState.Idle;
            equipTries = 0;
            stepper.Begin(next.Quest.RowId, new StepOptions(Settings.PauseForCutscenes, Settings.LeaveOutSay));
            Log($"Working on \"{next.Quest.Name}\" ({next.Quest.Number}).", false);
            return;
        }

        if (crafting) { TickCrafting(); return; }
        if (gathering) { TickGathering(); return; }
        // After crafting or gathering (the log closing, the node letting go), the wait before the next step.
        if (DateTime.UtcNow < waitUntil) return;
        stepper.Tick();
        var state = stepper.State;
        if (state != lastState)
        {
            DebugLog.Write("tribes", $"{quest.Quest.Number}: {lastState} -> {state}{(stepper.Problem != null ? $" ({stepper.Problem})" : "")}, {stepper.Status}");
            lastState = state;
        }
        switch (state)
        {
            case StepperState.Working:
                Status = stepper.Status;
                return;
            case StepperState.NeedsCraft:
                if (DateTime.UtcNow >= waitUntil) StartCrafting();
                return;
            case StepperState.NeedsGather:
                if (DateTime.UtcNow >= waitUntil) StartGathering();
                return;
            case StepperState.WaitingForUser:
                AwaitUser(stepper.Problem ?? "The quest needs you for a step; do it, then resume.", stepper.PausedForCutscene, logged: stepper.Problem != null);
                return;
            case StepperState.Done:
                // The stepper wrote "Finished …" to the log.
                Completed++;
                DebugLog.Write("tribes", $"{quest.Quest.Number} done; {Completed} done this run");
                quest = null;
                Wait(1);
                return;
            case StepperState.Failed:
                // The stepper wrote why to the log; the quest isn't planned again today.
                excluded[quest.Quest.RowId] = stepper.Problem ?? "The quest couldn't go on.";
                if (stepper.Problem == null) Log($"\"{quest.Quest.Name}\" couldn't go on.", true);
                stepper.Stop();
                quest = null;
                Wait(1);
                return;
            default:
                quest = null;
                return;
        }
    }

    /// <summary>The quest wants its item crafted: the recipe of the class it was accepted as, fastest rotation, no food.</summary>
    private void StartCrafting()
    {
        var need = stepper.Craft;
        if (need == null || Catalog is not { } catalog) return;
        var name = catalog.ItemName(need.Product);
        var recipe = catalog.RecipesFor(need.Product).FirstOrDefault(r => r.Job.ClassJobId() == need.ClassJob)
                     ?? (quest!.Quest.Craft?.For(need.ClassJob) is { } qr ? catalog.Recipe(qr.RecipeId) : null);
        if (recipe == null) { AwaitUser($"No recipe for {name} as {ClassJobs.Abbreviation(need.ClassJob)}; craft it yourself, then resume."); return; }
        // The facility access gone or about to run out: renewed first (the stepper talks to the supervisor).
        if (need.RequiredStatus != 0 && !QuestSteps.StatusLasts(need.RequiredStatus, StepRules.StatusRenewBelow))
        {
            DebugLog.Write("tribes", $"craft: status {need.RequiredStatus} is gone or runs out within {StepRules.StatusRenewBelow.TotalSeconds:0} seconds; renewing it first");
            stepper.Recheck("the facility access runs out soon");
            return;
        }
        // On the quest's class first (the crafter would change to its gearset, which takes the item to wear off).
        if (GameState.CurrentJob != need.ClassJob)
        {
            if (GameState.InCraftingStance) { Interact.Close("RecipeNote"); Wait(1); return; }
            var set = GameState.BestGearset(need.ClassJob);
            if (set < 0 || ++equipTries > 4) { AwaitUser($"Change to {ClassJobs.Abbreviation(need.ClassJob)} to craft {name}, then resume."); return; }
            GameState.EquipGearset(set);
            Wait(2);
            return;
        }
        if (need.RequiredEquip != 0 && !GameState.Wearing(need.RequiredEquip))
        {
            var gear = catalog.ItemName(need.RequiredEquip);
            if (GameState.InCraftingStance) { Interact.Close("RecipeNote"); Wait(1); return; }
            if (++equipTries > 4 || !GameState.Equip(need.RequiredEquip)) { AwaitUser($"Put on the {gear} to craft {name}, then resume."); return; }
            DebugLog.Write("tribes", $"putting on {need.RequiredEquip} ({gear}) to craft");
            Log($"Put on the {gear}.", false);
            Wait(1.5);
            return;
        }
        crafter.Begin(new[] { new CraftJob(recipe, Math.Max(1, need.Remaining), false, Quality: 0, UseConsumables: false, LiveRecipe: true) });
        crafting = true;
        yielding = false;
        Log($"Crafting {need.Remaining}x {name} as {ClassJobs.Abbreviation(need.ClassJob)}.", false);
        DebugLog.Write("tribes", $"craft: recipe {recipe.RecipeId}, {need.Remaining}x {need.Product} from {need.Materials}, status {need.RequiredStatus}, wear {need.RequiredEquip}");
    }

    private void TickCrafting()
    {
        crafter.Tick();
        // The stepper looks at the counts (and the facility access) while it wants the craft; once it doesn't, it waits
        // until the crafter has stopped at a safe point and left the crafting log.
        if (stepper.State == StepperState.NeedsCraft) stepper.Tick();
        Status = crafter.Status;
        if (stepper.State != StepperState.NeedsCraft)
        {
            if (crafter.Finished || crafter.IsPaused || !crafter.Started) { EndCrafting(); return; }
            if (!yielding)
            {
                yielding = true;
                crafter.RequestYield();
                DebugLog.Write("tribes", $"{quest?.Quest.Number}: the quest wants no more crafting now ({stepper.State}); stopping after this craft");
            }
            return;
        }
        if (!crafter.Finished) return;
        EndCrafting();
        var name = Catalog!.ItemName(stepper.Craft?.Product ?? 0);
        if (crafter.Failure is { } why) { AwaitUser($"Crafting {name}: {why} Craft it yourself, then resume."); return; }
        if (crafter.History.FirstOrDefault(h => h.Skipped != null) is { Skipped: { } skipped })
        {
            AwaitUser($"Couldn't craft {name}: {skipped}. Get what's missing, then resume.");
            return;
        }
        stepper.Supplied();
    }

    private void EndCrafting()
    {
        crafting = false;
        yielding = false;
        if (GameState.InCraftingStance && !crafter.MidCraft) Interact.Close("RecipeNote");
        Wait(1);
    }

    /// <summary>The quest wants its item gathered: at its own node, searched for in its area (the node data doesn't have it).</summary>
    private void StartGathering()
    {
        var need = stepper.Gather;
        if (need == null || Catalog is not { } catalog) return;
        var name = catalog.ItemName(need.ItemId);
        var known = quest!.Quest.GatherFor(need.ClassJob);
        var pointBase = need.PointBase != 0 ? need.PointBase : known?.PointBase ?? 0;
        if (pointBase == 0) { AwaitUser($"Couldn't tell where {name} is gathered; gather it yourself, then resume."); return; }
        var centre = need.Centre;
        var radius = need.Radius;
        if (new Vector2(centre.X, centre.Z) == Vector2.Zero && known != null)
        {
            centre = new Vector3(known.Centre.X, centre.Y, known.Centre.Y);
            radius = known.Radius;
        }
        var area = new GatherArea(need.Territory, centre, radius, pointBase, need.ClassJob);
        gatherer.Begin(new[] { new GatherTarget(need.ItemId, Supply.Gather, need.Remaining, 0) },
            _ => stepper.State == StepperState.NeedsGather ? stepper.Gather?.Remaining ?? 0 : 0, () => new HashSet<uint>(),
            gatherAreas: new Dictionary<uint, GatherArea> { [need.ItemId] = area });
        gathering = true;
        Log($"Gathering {need.Remaining}x {name} as {ClassJobs.Abbreviation(need.ClassJob)}.", false);
        DebugLog.Write("tribes", $"gather: {need.Remaining}x {need.ItemId}, base {pointBase}, zone {need.Territory}, centre {centre.X:0},{centre.Z:0}, radius {radius:0}");
    }

    private void TickGathering()
    {
        gatherer.Tick();
        if (stepper.State == StepperState.NeedsGather) stepper.Tick();
        Status = gatherer.Status;
        if (gatherer.Failure is { } why)
        {
            StopGatherer();
            gathering = false;
            AwaitUser($"{why} Then resume.");
            return;
        }
        var enough = stepper.State != StepperState.NeedsGather || (stepper.Gather?.Remaining ?? 0) <= 0;
        if (!enough || gatherer.OnNode || gatherer.UpkeepBusy || Services.Condition[ConditionFlag.Gathering]) return;
        StopGatherer();
        gathering = false;
        if (stepper.State == StepperState.NeedsGather) stepper.Supplied();
        Wait(1);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>What the run is doing and knows, line by line, for a bug report.</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"step {step}, visit step {visitStep}, {(Active ? "running" : "idle")}{(Paused ? ", paused" : "")}{(Pausing ? ", pausing" : "")}, status \"{Status}\"";
        if (Waiting != null) yield return $"waiting for the player{(WaitingForCutscene ? " (cutscene)" : "")}: {Waiting}";
        if (Problem != null) yield return "stopped on: " + Problem;
        yield return $"done {Completed}, accepted {AcceptedCount}, visits {visits}, crafting {crafting}, gathering {gathering}";
        if (visit is { } v) yield return $"visiting {v.Tribe.Name}, giver {v.Giver.NpcId}, class {v.ClassJob}, queue [{string.Join(",", queue.Select(q => q.Quest.Number))}], accepted here [{string.Join(",", acceptedHere.Select(q => q.Quest.Number))}]";
        yield return $"stepper: {stepper.State}{(stepper.Paused ? " (paused)" : "")}, quest {(stepper.Quest > 0 ? stepper.Quest - 0x10000 : 0)}, status \"{stepper.Status}\""
                     + (stepper.Problem != null ? $", problem \"{stepper.Problem}\"" : "") + (stepper.Plan != null ? $", plan {stepper.Plan}" : "");
        if (stepper.Craft is { } c) yield return "stepper wants crafted: " + c;
        if (stepper.Gather is { } g) yield return "stepper wants gathered: " + g;
        foreach (var (row, why) in excluded) yield return $"given up today: {row - 0x10000}: {why}";
        foreach (var (npc, why) in excludedGivers) yield return $"giver given up today: {npc}: {why}";
        if (gathering || step == Step.Gather)
            foreach (var l in gatherer.Describe()) yield return "gatherer: " + l;
        if (crafting && crafter.LastSolve.Length > 0) yield return "crafter: " + crafter.LastSolve;
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static void Notify(string text, bool problem)
    {
        Services.Notifications.AddNotification(new Notification
        {
            Title = "Provenance",
            Content = text,
            Type = problem ? NotificationType.Warning : NotificationType.Info,
        });
        Services.Chat.Print(new SeStringBuilder().AddUiForeground("[Provenance] ", 62).AddText(text).Build());
    }

    private void Log(string text, bool problem)
    {
        log.Add((DateTime.Now, text, problem));
        if (log.Count > 200) log.RemoveAt(0);
        Services.Log.Information("[Tribes] {Text}", text);
        DebugLog.Write("tribes", (problem ? "[!] " : "") + text);
    }

    private void Go(Step next)
    {
        if (next != step)
        {
            tries = 0;
            DebugLog.Write("tribes", $"step {step} -> {next}; done {Completed}, accepted {AcceptedCount}, {Game.AllowancesLeft} allowances left, job {GameState.CurrentJob}");
        }
        step = next;
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
