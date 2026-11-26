using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
using Provenance.Core.Crafting;
using Provenance.Core.Estimating;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>One recipe to craft a number of times.</summary>
/// <param name="Hq">Aim for high quality (ordered items the user wants HQ); otherwise the fastest rotation.</param>
/// <param name="Quality">A quality to aim for instead (collectables); null uses <paramref name="Hq"/>.</param>
/// <param name="Cosmic">A Cosmic Exploration mission recipe: made in the mission's own crafting log, from the Cosmopouch.</param>
/// <param name="SteadyHand">Stellar Steady Hand uses the mission lends (Raphael plans with them).</param>
/// <param name="UseConsumables">Eat and take medicine as set under Settings, Crafting (the Cosmic loop only when its suggestion was taken).</param>
/// <param name="LiveRecipe">
/// A recipe the game may adjust to the crafter's level (allied society quest recipes: "the difficulty of this quest has
/// been synced to your current level"): its numbers are read from the game and logged next to the sheet's, and the
/// rotation is worked out for the game's when they differ.
/// </param>
internal sealed record CraftJob(RecipeInfo Recipe, int Crafts, bool Hq, int? Quality = null, bool Cosmic = false, int SteadyHand = 0, bool UseConsumables = true,
    bool LiveRecipe = false);

/// <summary>
/// Crafts a queue of recipes, one synthesis at a time: right job and gearset, repair, food and
/// medicine, a Raphael rotation for the current stats, ingredients, then the rotation action by
/// action. Ticked every frame on the framework thread. Pause and stop always land between crafts,
/// so no materials are lost.
/// </summary>
internal sealed unsafe class Crafter
{
    private enum Step { Idle, NextRecipe, LeaveStance, SwitchJob, Repair, Consumables, Solve, OpenRecipe, Ingredients, Synthesize, Acting, WaitResult, Paused, WaitingForMaterials, Finished, Failed }

    private const uint BasicSynthesis = 100001;
    private const uint RepairAction = 6;

    private readonly Plugin plugin;
    private Raphael raphael => plugin.Raphael;
    private DateTime synthStarted;
    private DateTime lastCraftEnded;
    private int actionsUsed;
    private readonly Action<string, bool> log;
    /// <summary>Recipes still to craft, in order (ingredients before the items that use them).</summary>
    private readonly List<CraftJob> pending = new();
    private readonly HashSet<string> warnedOnce = new();
    private readonly Dictionary<uint, DateTime> lastUse = new();

    private Step step = Step.Idle;
    private Step afterLeave;
    private DateTime waitUntil;
    private DateTime deadline;
    private int attempts;
    /// <summary>Clicks on HQ for a slot the game had already filled with NQ ones (each should swap one).</summary>
    private int swaps;
    /// <summary>Swap clicks before giving up on them (when they don't take, the craft goes ahead with what's in).</summary>
    private const int MaxSwaps = 20;
    private Task<RaphaelSolution>? solving;
    private RaphaelRequest? solvingFor;
    private List<uint> macro = new();
    /// <summary>The solution <see cref="macro"/> came from (null: none, or Basic Synthesis alone).</summary>
    private RaphaelSolution? planned;
    /// <summary>The request <see cref="planned"/> was solved for.</summary>
    private RaphaelRequest? plannedFor;
    /// <summary>What the serve process was asked to prepare for <see cref="planned"/> (null: this rotation isn't re-planned).</summary>
    private RaphaelRequest? serveRequest;
    /// <summary>The numbers of the rotation <see cref="serveRequest"/> is for.</summary>
    private SimSettings? serveSim;
    /// <summary>A re-plan sent to Raphael for the step at <see cref="actionIndex"/>, waited for up to <see cref="Replanning.Wait"/>.</summary>
    private (long Id, Task<ServeAnswer> Answer, DateTime Since, CraftCondition Condition)? replanning;
    /// <summary>A re-plan began with actions that take no step (Heart and Soul): the actions up to here are on its step still, not re-planned again.</summary>
    private int replanCoversUntil = -1;
    /// <summary>The first action of the rotation a full re-plan wrote (-1: none this craft).</summary>
    private int replannedFrom = -1;
    /// <summary>This craft's actions: the rotation, with the changes made on Good and Excellent steps.</summary>
    private List<uint> rotation = new();
    /// <summary>Follows this craft with the simulator to react to Good and Excellent steps (null: not reacting this craft).</summary>
    private CraftWatch? watch;
    /// <summary>The quality the rotation of this craft was planned to reach (Normal steps throughout).</summary>
    private int rotationQuality = -1;
    /// <summary>The action of the rotation the game's numbers were last checked before (each one once).</summary>
    private int watchedAt = -1;
    /// <summary>The action of the rotation a reaction put in (not from the plan); -1 for none.</summary>
    private int addedAt = -1;
    /// <summary>The condition of the step the next action goes on.</summary>
    private CraftCondition stepCondition;
    /// <summary>Since when the game's numbers differ from the prediction (they can lag a moment behind the window).</summary>
    private DateTime differsSince;
    /// <summary>The quality and step number the Synthesis window showed last.</summary>
    private int seenQuality = -1, seenStep = -1;
    /// <summary>A rotation that falls short of HQ, waiting for its HQ chance before the run log says so.</summary>
    private (RaphaelRequest Request, RaphaelSolution Solution, string Text, string Hint, bool React, DateTime Since)? qualityNote;
    /// <summary>The HQ ingredients the rotation in use was worked out for (null: not an HQ craft).</summary>
    private IReadOnlyList<int>? macroHq;
    private int actionIndex;
    private int stepIndexBefore = -1;
    private bool actionPending;
    private bool fallback;
    private int doneThisJob;
    private bool pauseRequested;
    private bool leaveStanceOnPause;
    private DateTime lastEquip;

    /// <summary>A <see cref="CraftJob.LiveRecipe"/> job: the game's numbers for the recipe when they differ from the sheet's (null: the sheet's).</summary>
    private LiveRecipe? live;
    /// <summary>The crafting log's numbers for the recipe were compared with the sheet's (once per job).</summary>
    private bool liveChecked;
    /// <summary>The synthesis window's numbers were compared with the ones the rotation was worked out for (this craft).</summary>
    private bool windowChecked;
    /// <summary>The synthesis window showed other numbers: the next craft gets a rotation for them.</summary>
    private bool solveAgainAfterCraft;

    /// <summary>A specialist action (Heart and Soul, Quick Innovation) was refused this session: rotations leave them out from then on.</summary>
    private bool specialistRefused;

    /// <summary>
    /// Actions a rotation can't do without: they save or restore durability, so going on without one could break the
    /// item. Any other action the game refuses is skipped, and Basic Synthesis finishes whatever progress is left.
    /// </summary>
    private static readonly HashSet<uint> Essential = new() { 4574, 4631, 4639, 100003, 100467, 100475 };

    /// <summary>Raphael's ids of the specialist actions (Heart and Soul, Quick Innovation).</summary>
    private static readonly HashSet<uint> SpecialistActions = new() { 100419, 100459 };

    /// <summary>A rotation can use both specialist actions, one Crafter's Delineation each: specialist rotations only with this many in the bags.</summary>
    private const int DelineationsPerCraft = 2;

    /// <summary>
    /// While timed nodes are still to be gathered: craft only recipes whose materials are in the bags and
    /// keep the rest for later instead of skipping them.
    /// </summary>
    public bool DeferMissing { get; set; }

    /// <summary>Raised after every finished craft, so the run can account for the materials it used.</summary>
    public event Action<RecipeInfo>? Crafted;

    public Crafter(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public CraftJob? Current { get; private set; }
    public int CraftsDone { get; private set; }
    public int CraftsTotal { get; private set; }
    public int DoneThisRecipe => doneThisJob;
    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public string CurrentAction { get; private set; } = "";
    public int ActionNumber => Math.Min(actionIndex, Steps.Count);
    public int ActionCount => Steps.Count;
    /// <summary>The actions of the craft under way, or the rotation between crafts.</summary>
    private List<uint> Steps => MidCraft ? rotation : macro;
    public IReadOnlyList<CraftJob> Upcoming => pending.ToList();
    /// <summary>The last rotation worked out: recipe, stats, HQ ingredients, steps and the quality it reaches (bug report).</summary>
    public string LastSolve { get; private set; } = "";
    /// <summary>The quality the rotation in use ends at (-1: unknown), so a Cosmic mission can note what it scored with.</summary>
    public int LastQuality { get; private set; } = -1;

    /// <summary>Stellar Steady Hand used since <see cref="Begin"/>: a mission lends its uses for all its crafts together.</summary>
    public int SteadyHandUsed { get; private set; }

    private const uint StellarSteadyHand = 46843;
    public List<(CraftJob Job, int Done, string? Skipped)> History { get; } = new();

    public bool Finished => step is Step.Finished or Step.Failed;
    public bool IsPaused => step == Step.Paused;
    /// <summary>Nothing left that can be crafted with what's in the bags right now (only while deferring).</summary>
    public bool WaitingForMaterials => step == Step.WaitingForMaterials;
    public bool Started => step != Step.Idle;

    /// <summary>Whether any recipe still to craft has its materials in the bags.</summary>
    public bool HasReadyWork()
        => (Current != null && doneThisJob < Current.Crafts && MissingIngredient(Current.Recipe) == null)
           || pending.Any(j => MissingIngredient(j.Recipe) == null);
    /// <summary>A synthesis is under way; stopping now would lose materials.</summary>
    public bool MidCraft => step is Step.Synthesize or Step.Acting or Step.WaitResult;

    public void Begin(IEnumerable<CraftJob> jobs)
    {
        pending.Clear();
        History.Clear();
        warnedOnce.Clear();
        lastUse.Clear();
        pending.AddRange(jobs.Where(j => j.Crafts > 0));
        CraftsTotal = pending.Sum(j => j.Crafts);
        CraftsDone = 0;
        Current = null;
        Failure = null;
        pauseRequested = false;
        // Nothing of an earlier list carries over: a rotation still being worked out, the one in use, its quality.
        solving = null;
        solvingFor = null;
        settling = null;
        macro.Clear();
        macroHq = null;
        planned = null;
        plannedFor = null;
        serveRequest = null;
        replanning = null;
        raphael.Server.End("a new crafting list");
        watch = null;
        qualityNote = null;
        LastQuality = -1;
        SteadyHandUsed = 0;
        ForgetLive();
        Go(Step.NextRecipe);
    }

    private void ForgetLive()
    {
        live = null;
        liveChecked = false;
        windowChecked = false;
        solveAgainAfterCraft = false;
    }

    /// <summary>Pause after the craft that's under way (or right away between crafts).</summary>
    public void RequestPause() => pauseRequested = true;

    /// <summary>Pause after the current craft and close the crafting log, so gathering can take over.</summary>
    public void RequestYield()
    {
        pauseRequested = true;
        leaveStanceOnPause = true;
        if (step == Step.WaitingForMaterials) Pause();
    }

    public void Resume()
    {
        pauseRequested = false;
        leaveStanceOnPause = false;
        if (step is Step.Paused or Step.WaitingForMaterials)
            Go(Current != null && doneThisJob < Current.Crafts ? Step.Repair : Step.NextRecipe);
    }

    /// <summary>Gathering is done: craft everything left, skipping recipes whose materials are missing.</summary>
    public void FinalPass()
    {
        DeferMissing = false;
        Resume();
    }

    /// <summary>Getting off the mount before crafting: since when, and how often the button was pressed.</summary>
    private DateTime dismountSince;
    private int dismountPresses;

    /// <summary>
    /// Nothing about crafting works on a mount (the job change, the log, the synthesis), and the run can arrive
    /// mounted or in the air (after travelling ahead to a timed node). Gets off first, landing when flying; when a
    /// landing doesn't take, flies down to the ground below and tries again. True while still on the mount.
    /// </summary>
    private bool GetOffMount()
    {
        var c = Services.Condition;
        if (!c[ConditionFlag.Mounted] && !c[ConditionFlag.InFlight])
        {
            dismountSince = default;
            dismountPresses = 0;
            return false;
        }
        var now = DateTime.UtcNow;
        if (dismountSince == default) dismountSince = now;
        if (now - dismountSince > TimeSpan.FromSeconds(40)) { Fail("Couldn't get off the mount to craft."); return true; }
        if (Integrations.Vnav.Busy) { Status = "Flying down to the ground to craft"; return true; }
        if (c[ConditionFlag.InFlight] && dismountPresses > 0 && dismountPresses % 3 == 0
            && Services.Objects.LocalPlayer is { } me && Integrations.Vnav.NearestPoint(me.Position, 5f, 60f) is { } ground)
        {
            Integrations.Vnav.MoveTo(ground, true);
            dismountPresses++;
            Wait(1);
            return true;
        }
        dismountPresses++;
        Status = c[ConditionFlag.InFlight] ? "Landing to craft" : "Getting off the mount to craft";
        ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
        Wait(1.5);
        return true;
    }

    public void Tick()
    {
        // The run ended: a note still waiting for its HQ chance isn't said any more.
        if (Finished || step == Step.Idle) qualityNote = null;
        if (Finished || step is Step.Idle or Step.Paused) return;
        // Crafting: the serve process stays (it ends a few minutes after the last tick, in a pause or once the run stopped).
        if (serveRequest != null && serveSim != null && step != Step.WaitingForMaterials) raphael.Server.Prepare(serveRequest, serveSim);
        if (DateTime.UtcNow < waitUntil) return;
        if (step is Step.SwitchJob or Step.Repair or Step.Consumables or Step.OpenRecipe or Step.Ingredients or Step.Synthesize && GetOffMount()) return;
        if (step == Step.WaitingForMaterials)
        {
            // Something gathered or bought in the meantime may have made a recipe craftable.
            if (pauseRequested) Pause();
            else if (HasReadyWork()) Go(Step.NextRecipe);
            else Wait(1);
            return;
        }
        try
        {
            FlushQualityNote();
            switch (step)
            {
                case Step.NextRecipe: NextRecipe(); break;
                case Step.LeaveStance: LeaveStance(); break;
                case Step.SwitchJob: SwitchJob(); break;
                case Step.Repair: Repair(); break;
                case Step.Consumables: Consumables(); break;
                case Step.Solve: Solve(); break;
                case Step.OpenRecipe: OpenRecipe(); break;
                case Step.Ingredients: Ingredients(); break;
                case Step.Synthesize: Synthesize(); break;
                case Step.Acting: Acting(); break;
                case Step.WaitResult: WaitResult(); break;
            }
        }
        catch (Exception e)
        {
            Services.Log.Error(e, "Crafter step {Step} failed", step);
            Fail("Crafting hit an error: " + e.Message);
        }
    }

    // ------------------------------------------------------------------ steps

    private void NextRecipe()
    {
        if (pauseRequested) { Pause(); return; }
        if (Current != null && doneThisJob >= Current.Crafts)
        {
            History.Add((Current, doneThisJob, null));
            Current = null;
            plugin.Config.Save();
        }
        if (Current == null)
        {
            if (pending.Count == 0)
            {
                Status = "All crafts done";
                if (GameState.InCraftingStance) { afterLeave = Step.Finished; Go(Step.LeaveStance); }
                else Go(Step.Finished);
                return;
            }
            // While deferring, take the first recipe that can be crafted now; otherwise strictly in order.
            var next = DeferMissing ? pending.FirstOrDefault(j => Missing(j) == null) : pending[0];
            if (next == null)
            {
                Status = "Waiting for materials";
                CurrentAction = "";
                Go(Step.WaitingForMaterials);
                return;
            }
            pending.Remove(next);
            Current = next;
            doneThisJob = 0;
            macro.Clear();
            ForgetLive();
            serveRequest = null;
            raphael.Server.End("another recipe");
        }

        var catalog = plugin.Plans.Catalog!;
        if (Missing(Current) is { } missing)
        {
            var reason = $"missing {missing.Amount}× {catalog.ItemName(missing.ItemId)} in your bags";
            log($"Skipped {catalog.ItemName(Current.Recipe.ItemId)}: {reason}.", true);
            History.Add((Current, doneThisJob, reason));
            CraftsTotal -= Current.Crafts - doneThisJob;
            Current = null;
            Go(Step.NextRecipe);
            return;
        }

        Status = "Getting ready for " + catalog.ItemName(Current.Recipe.ItemId);
        var job = Current.Recipe.Job.ClassJobId();
        if (GameState.CurrentJob != job)
        {
            afterLeave = Step.SwitchJob;
            Go(Step.LeaveStance);
        }
        else Go(Step.Repair);
    }

    /// <summary>Closes the recipe log so the job can change, items can be used or gear repaired.</summary>
    private void LeaveStance()
    {
        if (!GameState.InCraftingStance)
        {
            if (afterLeave == Step.Paused) { Status = "Paused"; CurrentAction = ""; }
            Go(afterLeave);
            return;
        }
        if (Services.Condition[ConditionFlag.ExecutingCraftingAction] || Visible("Synthesis")) { Wait(0.3); return; }
        if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(10);
        if (DateTime.UtcNow > deadline) { Fail("Couldn't close the crafting log."); return; }
        if (Addon("RecipeNote") is var note && note != null) note->Close(true);
        if (Addon(CosmicNotebook) is var cosmic && cosmic != null) cosmic->Close(true);
        Wait(0.5);
    }

    private void SwitchJob()
    {
        var job = Current!.Recipe.Job.ClassJobId();
        if (GameState.CurrentJob == job) { Wait(0.8); Go(Step.Repair, keepWait: true); return; }
        if (deadline == default)
        {
            var gearset = GameState.BestGearset(job);
            if (gearset < 0) { Fail($"There's no gearset saved for {Current.Recipe.Job}. Save one and start again."); return; }
            Status = $"Switching to {Current.Recipe.Job}";
            GameState.EquipGearset(gearset);
            deadline = DateTime.UtcNow.AddSeconds(15);
            lastEquip = DateTime.UtcNow;
            Wait(0.5);
            return;
        }
        if (DateTime.UtcNow > deadline) { Fail($"Couldn't switch to {Current.Recipe.Job}."); return; }
        // Still busy a moment ago (gathering, fishing, an animation): the change didn't take, so ask again.
        if (DateTime.UtcNow - lastEquip > TimeSpan.FromSeconds(2.5) && !Services.Condition[ConditionFlag.Gathering]
            && !Services.Condition[ConditionFlag.Fishing] && !Services.Condition[ConditionFlag.Casting])
        {
            GameState.EquipGearset(GameState.BestGearset(job));
            lastEquip = DateTime.UtcNow;
        }
        Wait(0.3);
    }

    private void Repair()
    {
        var threshold = plugin.Config.Crafting.RepairBelow;
        if (threshold <= 0 || GameState.LowestGearCondition() >= threshold)
        {
            CloseIfOpen("Repair");
            Go(Step.Consumables);
            return;
        }
        if (GameState.InCraftingStance) { afterLeave = Step.Repair; Go(Step.LeaveStance); return; }
        if (deadline == default)
        {
            Status = "Repairing gear";
            log($"Gear below {threshold}%; repairing.", false);
            deadline = DateTime.UtcNow.AddSeconds(15);
        }
        if (DateTime.UtcNow > deadline)
        {
            CloseIfOpen("Repair");
            Fail("Couldn't repair. Self-repair needs Dark Matter of a high enough grade in your bags.");
            return;
        }
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("SelectYesno", out var yesno) && GenericHelpers.IsAddonReady(yesno))
        {
            new AddonMaster.SelectYesno((nint)yesno).Yes();
            Wait(1.5);
            return;
        }
        if (GenericHelpers.TryGetAddonByName<AtkUnitBase>("Repair", out var repair) && GenericHelpers.IsAddonReady(repair))
        {
            new AddonMaster.Repair((nint)repair).RepairAll();
            Wait(1);
            return;
        }
        GameState.Use(ActionType.GeneralAction, RepairAction);
        Wait(1);
    }

    private void Consumables()
    {
        if (Current is { UseConsumables: false }) { Go(Step.Solve); return; }
        var (food, medicine) = plugin.Config.Consumables.For(Discipline.Hand);
        foreach (var (choice, isMedicine) in new[] { (food, false), (medicine, true) })
        {
            if (choice.ItemId == 0 || GameState.BuffSecondsLeft(isMedicine) > 60)
            {
                if (choice.ItemId != 0 && warnedOnce.Add("active" + choice.ItemId + (isMedicine ? "m" : "f")))
                    DebugLog.Write("crafter", $"{(isMedicine ? "medicine" : "food")} {choice.ItemId}: already active, {GameState.BuffSecondsLeft(isMedicine):0} s left");
                continue;
            }
            var hq = choice.Hq && GameState.BagCount(choice.ItemId, true) > 0;
            if (GameState.BagCount(choice.ItemId, hq) == 0)
            {
                if (warnedOnce.Add("none" + choice.ItemId))
                    log($"No {plugin.Plans.Catalog!.ItemName(choice.ItemId)} left; crafting without it.", true);
                continue;
            }
            // One try at a time; if the buff doesn't show up, carry on rather than loop.
            if (lastUse.TryGetValue(choice.ItemId, out var tried) && DateTime.UtcNow - tried < TimeSpan.FromSeconds(20)) continue;
            if (GameState.InCraftingStance) { afterLeave = Step.Consumables; Go(Step.LeaveStance); return; }
            Status = isMedicine ? "Taking medicine" : "Eating";
            GameState.UseItem(choice.ItemId, hq);
            DebugLog.Write("crafter", $"{(isMedicine ? "medicine" : "food")} {choice.ItemId}{(hq ? " HQ" : "")}: used ({GameState.BagCount(choice.ItemId, hq)} in the bags before)");
            lastUse[choice.ItemId] = DateTime.UtcNow;
            Wait(2.5);
            return;
        }
        Go(Step.Solve);
    }

    /// <summary>Stats seen last tick while waiting for them to settle after a gearset change or food.</summary>
    private int[]? settling;
    private DateTime settledAt;

    private void Solve()
    {
        var r = Current!.Recipe;
        if (solving == null)
        {
            if (macro.Count > 0) { Go(Step.OpenRecipe); return; }
            var stats = GameState.LiveStats(Discipline.Hand);
            // Right after a gearset change or food the game can still show the previous numbers: wait
            // until they are on the right job and have stayed the same for a moment.
            if (GameState.CurrentJob != r.Job.ClassJobId() || stats[0] <= 0 || settling == null || !stats.SequenceEqual(settling))
            {
                settling = stats;
                settledAt = DateTime.UtcNow.AddSeconds(1.5);
                Status = "Reading your stats";
                Wait(0.3);
                return;
            }
            if (DateTime.UtcNow < settledAt) { Wait(0.2); return; }
            settling = null;
            Services.Log.Information("Solving {Recipe} at Craftsmanship {C}, Control {Co}, CP {Cp}, level {L}", r.RecipeId, stats[0], stats[1], stats[2], GameState.JobLevel(r.Job.ClassJobId()));
            var c = plugin.Config.Crafting;
            var manipulation = plugin.Plans.Catalog!.ManipulationQuest.TryGetValue(r.Job.ClassJobId(), out var quest) && QuestManager.IsQuestComplete(quest);
            var hqIn = Current.Cosmic ? null : HqIngredients(Current);
            var specialist = c.UseSpecialistActions && GameState.SpecialistEquipped() && !Current.Cosmic && !specialistRefused;
            if (specialist && GameState.Delineations < DelineationsPerCraft)
            {
                specialist = false;
                if (warnedOnce.Add("delineations"))
                    log($"Fewer than {DelineationsPerCraft} {plugin.Plans.Catalog!.ItemName(GameState.Delineation)} in the bags ({GameState.Delineations}): "
                        + "specialist actions use one each, so the rotations leave them out.", true);
            }
            var request = new RaphaelRequest(r.RecipeId, GameState.JobLevel(r.Job.ClassJobId()), stats[0], stats[1], stats[2],
                manipulation, specialist,
                Current.Quality ?? (Current.Hq ? null : 0), hqIn, Math.Max(0, Current.SteadyHand - SteadyHandUsed),
                Live: Current.LiveRecipe ? live : null);
            Status = "Working out the rotation";
            DebugLog.Write("crafter", $"solve recipe {r.RecipeId} ({plugin.Plans.Catalog!.ItemName(r.ItemId)}), level {request.Level}, stats {request.Craftsmanship}/{request.Control}/{request.Cp}, "
                                      + $"Steady Hand {request.SteadyHand}, target {request.TargetQuality?.ToString() ?? "max"}, specialist {request.Specialist}, manipulation {request.Manipulation}, cosmic {Current.Cosmic}"
                                      + (request.Live is { } l ? $", the game's numbers ({l})" : ""));
            raphael.ForgetFailure(request);
            macroHq = hqIn;
            solvingFor = request;
            solving = raphael.Solve(request, forCraft: true);
            Wait(0.1);
            return;
        }
        if (!solving.IsCompleted) { Wait(0.1); return; }
        var task = solving;
        solving = null;
        if (!task.IsCompletedSuccessfully)
        {
            var why = task.Exception?.GetBaseException().Message ?? "cancelled";
            // No quality wanted: Basic Synthesis finishes the item just the same.
            if ((Current.Quality ?? (Current.Hq ? -1 : 0)) == 0)
            {
                log($"{why} Finishing {plugin.Plans.Catalog!.ItemName(r.ItemId)} with Basic Synthesis instead.", true);
                macro = new List<uint> { BasicSynthesis };
                planned = null;
                Go(Step.OpenRecipe);
                return;
            }
            Fail("Raphael couldn't plan this craft: " + why);
            return;
        }
        var solution = task.Result;
        macro = solution.Actions.ToList();
        planned = solution;
        plannedFor = solvingFor;
        // Re-planning on Good and Excellent steps: the serve process prepares while the recipe opens and the ingredients go in.
        WarmReplanner();
        LastQuality = solution.FinalQuality;
        DebugLog.Write("crafter", $"rotation {macro.Count} steps, quality {solution.FinalQuality} of {solution.MaxQuality}: {string.Join(" ", macro)}");
        var name = plugin.Plans.Catalog!.ItemName(r.ItemId);
        if (solvingFor is { } asked)
            LastSolve = $"{name} (recipe {r.RecipeId}{(Current.Hq ? ", HQ" : "")}): Craftsmanship {asked.Craftsmanship}, Control {asked.Control}, CP {asked.Cp}, level {asked.Level}, "
                        + $"HQ ingredients {(asked.AnyHqIngredients ? string.Join("/", asked.HqIngredients!) : "none")}, {solution.Actions.Count} steps, quality {solution.FinalQuality} of {solution.MaxQuality}";
        // Aiming for HQ but even the best rotation falls short: say so once, with what would help.
        if (Current.Hq && Current.Quality == null && !Current.Cosmic && solution is { ReachesMax: false, MaxQuality: > 0, FinalQuality: >= 0 }
            && warnedOnce.Add($"quality {r.RecipeId} {solution.FinalQuality}"))
        {
            var allHq = macroHq != null && Current.Recipe.Ingredients.Where(i => !Items.IsCrystal(i.ItemId)).Select(i => i.Amount).SequenceEqual(macroHq);
            var head = $"{name}: the best rotation reaches {solution.FinalQuality:N0} of {solution.MaxQuality:N0} quality";
            var hint = allHq ? "Food and medicine (Character page) or better gear raise it." : "More HQ ingredients, food and medicine (Character page) or better gear raise it.";
            // How often it comes out HQ with Good and Excellent steps is worked out in the background; said once it's known.
            if (solvingFor is { } asked2 && solution.Simulable && CraftActions.NormalConditions(r.RecipeId))
                qualityNote = (asked2, solution, head, hint, plugin.Config.Crafting.ReactToConditions, DateTime.UtcNow);
            else log($"{head}, so it comes out HQ only sometimes. {hint}", true);
        }
        Go(Step.OpenRecipe);
    }

    /// <summary>
    /// The HQ ingredients a craft of this job gets, per ingredient of the recipe in its order (crystals left out, as
    /// Raphael takes them): the HQ ones in the bags go in first (see <see cref="Ingredients"/>). Null when the craft
    /// doesn't aim for HQ.
    /// </summary>
    private static int[]? HqIngredients(CraftJob job)
        => job.Hq && job.Quality == null
            ? job.Recipe.Ingredients.Where(i => !Items.IsCrystal(i.ItemId)).Select(i => Math.Min(i.Amount, GameState.BagCount(i.ItemId, true))).ToArray()
            : null;

    private void OpenRecipe()
    {
        var id = Current!.Recipe.RecipeId;
        if (Current.Cosmic) { OpenCosmicRecipe(id); return; }
        if (SelectedRecipe() == id && Ready("RecipeNote"))
        {
            // A quest recipe the game adjusts: once the log shows it, plan for the game's numbers if they differ.
            if (Current.LiveRecipe && !liveChecked)
            {
                liveChecked = true;
                if (CheckLiveRecipe())
                {
                    macro.Clear();
                    Go(Step.Solve);
                    return;
                }
            }
            Wait(0.4);
            Go(Step.Ingredients, keepWait: true);
            return;
        }
        if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(10);
        if (DateTime.UtcNow > deadline) { Fail("The recipe didn't open."); return; }
        Status = "Opening the recipe";
        AgentRecipeNote.Instance()->OpenRecipeByRecipeId(id);
        Wait(1);
    }

    /// <summary>The mission's crafting log (Cosmic Exploration).</summary>
    private const string CosmicNotebook = "WKSRecipeNotebook";

    /// <summary>
    /// A mission recipe: the game opens the mission's own crafting log for it, listing only the mission's recipes;
    /// the right one is picked by its row (as Artisan does it).
    /// </summary>
    private void OpenCosmicRecipe(uint id)
    {
        var notebook = Addon(CosmicNotebook);
        var ready = notebook != null && GenericHelpers.IsAddonReady(notebook);
        if (ready && SelectedRecipe() == id) { Wait(0.4); Go(Step.Ingredients, keepWait: true); return; }
        if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(12);
        if (DateTime.UtcNow > deadline) { Fail("The mission's crafting log didn't open on its recipe."); return; }
        Status = "Opening the mission's recipe";
        if (ready && RecipeRow(id) is >= 0 and var row)
        {
            Callback.Fire(notebook, false, 0, row);
            Wait(0.6);
            return;
        }
        AgentRecipeNote.Instance()->OpenRecipeByRecipeId(id);
        Wait(1);
    }

    /// <summary>The row of a recipe in the list the open crafting log shows, or -1.</summary>
    private static int RecipeRow(uint id)
    {
        var note = RecipeNote.Instance();
        if (note == null || note->RecipeList == null) return -1;
        var list = note->RecipeList->RecipesSpan;
        for (var i = 0; i < list.Length && i < note->RecipeList->RecipeCount; i++)
            if (list[i].RecipeId == id) return i;
        return -1;
    }

    /// <summary>A mission recipe's ingredients come from the Cosmopouch: HQ ones first (they start the craft with quality), then NQ.</summary>
    private void CosmicIngredients()
    {
        if (macro.Count == 0) { Go(Step.Solve); return; }
        if (!Ready(CosmicNotebook) || SelectedRecipe() != Current!.Recipe.RecipeId) { Go(Step.OpenRecipe); return; }
        var entry = SelectedEntry();
        if (entry == null) { Wait(0.3); return; }
        var ingredients = entry->Ingredients;
        var full = true;
        for (var i = 0; i < ingredients.Length; i++)
        {
            ref var ing = ref ingredients[i];
            if (ing.ItemId != 0 && ing.Amount != 0 && ing.NQCount + ing.HQCount < ing.Amount) full = false;
        }
        if (full) { attempts = 0; Go(Step.Synthesize); return; }
        if (++attempts > 6)
        {
            // Nothing left to put in: the mission's supplies are used up.
            Fail("The mission's supplies ran out before the craft could start.");
            return;
        }
        var notebook = new AddonMaster.WKSRecipeNotebook((nint)Addon(CosmicNotebook));
        if (attempts % 2 == 1) notebook.HQItemInput();
        else notebook.NQItemInput();
        Wait(0.4);
    }

    /// <summary>
    /// Assigns ingredients one click at a time. Aiming for HQ: the HQ ones first (they start the craft with
    /// quality); otherwise NQ first and HQ for the rest.
    /// </summary>
    private void Ingredients()
    {
        if (pauseRequested) { Pause(); return; }
        if (Current!.Cosmic) { CosmicIngredients(); return; }
        if (!Ready("RecipeNote") || SelectedRecipe() != Current!.Recipe.RecipeId) { Go(Step.OpenRecipe); return; }
        if (Missing(Current) is { } missing)
        {
            var catalog = plugin.Plans.Catalog!;
            if (DeferMissing)
            {
                // More is still being gathered: come back to the rest of this recipe later.
                pending.Insert(0, Current with { Crafts = Current.Crafts - doneThisJob });
                if (doneThisJob > 0) History.Add((Current with { Crafts = doneThisJob }, doneThisJob, null));
                Current = null;
                Go(Step.NextRecipe);
                return;
            }
            var reason = $"ran out of {catalog.ItemName(missing.ItemId)}";
            log($"Stopped crafting {catalog.ItemName(Current.Recipe.ItemId)} after {doneThisJob}: {reason}.", true);
            History.Add((Current, doneThisJob, reason));
            CraftsTotal -= Current.Crafts - doneThisJob;
            Current = null;
            Go(Step.NextRecipe);
            return;
        }

        // Fewer HQ ingredients at hand than the rotation was worked out for (the last ones went into the previous
        // craft): work it out again for what goes in now.
        if (HqIngredients(Current) is { } hqNow && (macroHq == null || !hqNow.SequenceEqual(macroHq)))
        {
            macro.Clear();
            Go(Step.Solve);
            return;
        }

        var entry = SelectedEntry();
        if (entry == null) { Wait(0.3); return; }
        var ingredients = entry->Ingredients;
        var hqFirst = Current.Hq && Current.Quality == null;
        // Swaps that didn't take once won't for the rest of the run either.
        var swapsWork = !warnedOnce.Contains("hq swap");
        for (var i = 0; i < ingredients.Length; i++)
        {
            ref var ing = ref ingredients[i];
            if (ing.ItemId == 0 || ing.Amount == 0) continue;
            // Aiming for HQ, every HQ one in the bags goes in. The game may have filled the slot with NQ ones
            // already: a click on HQ then swaps one of them for an HQ one.
            var hqWanted = hqFirst ? Math.Min(ing.Amount, GameState.BagCount(ing.ItemId, true)) : 0;
            var full = ing.NQCount + ing.HQCount >= ing.Amount;
            if (full && (ing.HQCount >= hqWanted || swaps >= MaxSwaps || !swapsWork)) continue;
            if (++attempts > 60) { Fail("Couldn't fill in the ingredients."); return; }
            if (full) swaps++;
            var hq = hqFirst
                ? ing.HQCount < hqWanted
                : GameState.BagCount(ing.ItemId, false) <= ing.NQCount;
            if (Addon("RecipeNote") is var note && note != null)
                new AddonMaster.RecipeNote((nint)note).Material((uint)i, hq);
            Wait(0.15);
            return;
        }
        if (swaps >= MaxSwaps && warnedOnce.Add("hq swap"))
            log("The NQ ingredients the game put in didn't swap for HQ ones, so this craft starts with less quality.", true);
        attempts = 0;
        swaps = 0;
        Go(Step.Synthesize);
    }

    private void Synthesize()
    {
        if (Visible("Synthesis"))
        {
            synthStarted = DateTime.UtcNow;
            actionsUsed = 0;
            // Time between two crafts of the same recipe, for estimates.
            if (lastCraftEnded != default && doneThisJob > 0 && synthStarted - lastCraftEnded < TimeSpan.FromSeconds(30))
            {
                var t = plugin.Config.Timings;
                t.SecondsBetweenCrafts = Math.Clamp(Timings.Blend(t.SecondsBetweenCrafts, (synthStarted - lastCraftEnded).TotalSeconds), 1, 20);
            }
            actionIndex = 0;
            actionPending = false;
            fallback = false;
            stepIndexBefore = -1;
            windowChecked = false;
            rotation = macro.ToList();
            rotationQuality = planned?.FinalQuality ?? -1;
            watch = StartWatch(out var notWatching);
            watchedAt = -1;
            addedAt = -1;
            // A re-plan still out from a craft that ended early: its answer isn't wanted.
            if (replanning is { } stale) raphael.Server.GiveUp(stale.Id);
            replanning = null;
            replanCoversUntil = -1;
            replannedFrom = -1;
            if (watch != null) WarmReplanner();
            differsSince = default;
            seenQuality = seenStep = -1;
            DebugLog.Write("crafter", $"synthesis started: recipe {Current!.Recipe.RecipeId}, craft {doneThisJob + 1} of {Current.Crafts}, rotation {macro.Count} steps; "
                                      + (watch != null ? "reacting to Good and Excellent steps" : "not reacting: " + notWatching));
            Status = $"Crafting {plugin.Plans.Catalog!.ItemName(Current!.Recipe.ItemId)}";
            Go(Step.Acting);
            return;
        }
        if (deadline == default)
        {
            if (Current!.Cosmic)
            {
                if (Addon(CosmicNotebook) is var cosmic && cosmic != null) new AddonMaster.WKSRecipeNotebook((nint)cosmic).Synthesize();
            }
            else if (Addon("RecipeNote") is var note && note != null) new AddonMaster.RecipeNote((nint)note).Synthesize();
            deadline = DateTime.UtcNow.AddSeconds(8);
            Wait(0.5);
            return;
        }
        if (DateTime.UtcNow > deadline)
        {
            if (++attempts > 2) { Fail("The synthesis didn't start."); return; }
            deadline = default;
        }
        Wait(0.3);
    }

    /// <summary>Plays the rotation: one action, then wait until the game has applied it.</summary>
    private void Acting()
    {
        var synth = Addon("Synthesis");
        if (synth == null || !synth->IsVisible)
        {
            if (Services.Condition[ConditionFlag.ExecutingCraftingAction]) { Wait(0.2); return; }
            Go(Step.WaitResult);
            return;
        }
        if (synth->AtkValuesCount > 15)
        {
            seenQuality = synth->AtkValues[9].Int;
            seenStep = synth->AtkValues[15].Int;
        }
        if (Services.Condition[ConditionFlag.ExecutingCraftingAction] || !GenericHelpers.IsAddonReady(synth)) { Wait(0.1); return; }

        var stepIndex = synth->AtkValuesCount > 15 ? synth->AtkValues[15].Int : -1;
        if (actionPending)
        {
            if (stepIndex == stepIndexBefore && DateTime.UtcNow < deadline) { Wait(0.1); return; }
            actionPending = false; // applied, or timed out and we move on
        }
        // Sitting down at the start of a craft, and every action's animation, lock actions for a moment; one sent then is
        // refused. Wait the lock out (the game keeps it), and give the very first action a second after the window opens.
        if (ActionManager.Instance()->AnimationLock > 0 || actionIndex == 0 && !fallback && DateTime.UtcNow - synthStarted < TimeSpan.FromSeconds(1))
        {
            Wait(0.1);
            return;
        }
        if (Current!.LiveRecipe && !windowChecked)
        {
            windowChecked = true;
            CheckSynthesisWindow(synth);
        }

        // Reacting to Good and Excellent steps: the game's numbers first, then maybe a change to the rest of the rotation.
        if (replanning != null && !ReplanArrived()) return;
        if (watch is { Active: true } && !fallback && actionIndex < rotation.Count && watchedAt != actionIndex && !WatchStep(synth)) return;

        var job = Current!.Recipe.Job.ClassJobId();
        uint raphaelId;
        if (!fallback && actionIndex < rotation.Count) raphaelId = rotation[actionIndex];
        else
        {
            if (!fallback)
            {
                log("The rotation ended before the item was finished; completing it with Basic Synthesis. The next craft gets a fresh rotation.", true);
                // Planned on stats that weren't right: plan the next one again from the live numbers.
                macro = new List<uint>();
                planned = null;
            }
            fallback = true;
            StopWatching("the rotation ended before the item was finished");
            // Finished another way than planned: the quality it ends at isn't known.
            LastQuality = -1;
            raphaelId = BasicSynthesis;
        }

        var (type, id) = CraftActions.For(raphaelId, job);
        CurrentAction = CraftActions.Name(raphaelId);
        if (!GameState.CanUse(type, id))
        {
            // The first action gets a little longer: the craft can take a moment to accept actions.
            if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(actionIndex == 0 ? 4 : 2);
            if (DateTime.UtcNow < deadline) { Wait(0.1); return; }
            var why = $"the game says {GameState.ActionStatus(type, id)}, step {actionIndex + 1} of {rotation.Count}";
            if (SpecialistActions.Contains(raphaelId) && !specialistRefused)
            {
                specialistRefused = true;
                log("Specialist actions are left out of the rotations from now on (one was refused).", true);
            }
            // The rest of this rotation was planned with that action in it (and a first-step action like Reflect only
            // works first): the next craft gets a rotation worked out afresh, instead of the same one going wrong again.
            // Not for an action a reaction put in: the plan itself is fine.
            var putIn = !fallback && (actionIndex == addedAt || replannedFrom >= 0 && actionIndex >= replannedFrom);
            if (!putIn) solveAgainAfterCraft = true;
            StopWatching($"the game refused {CurrentAction}");
            DebugLog.Write("crafter", $"[!] step {actionIndex + 1}/{rotation.Count} {CurrentAction} ({id}) refused: {GameState.ActionStatus(type, id)}; "
                                      + $"step in the window {stepIndex}, animation lock {ActionManager.Instance()->AnimationLock:0.00}, specialist {GameState.SpecialistEquipped()}, "
                                      + $"delineations {GameState.Delineations}; "
                                      + (putIn ? "a reaction put it in, the rotation stays" : "the next craft gets a new rotation"));
            // Not one the rotation hangs on: go on without it, and Basic Synthesis finishes what progress is left at the end.
            if (!fallback && !Essential.Contains(raphaelId))
            {
                log($"Couldn't use {CurrentAction} ({why}); going on without it.", true);
                LastQuality = -1;
                actionIndex++;
                deadline = default;
                return;
            }
            log($"Couldn't use {CurrentAction} ({why}); finishing with Basic Synthesis.", true);
            fallback = true;
            LastQuality = -1;
            deadline = default;
            return;
        }
        deadline = DateTime.UtcNow.AddSeconds(6);
        stepIndexBefore = stepIndex;
        actionPending = GameState.Use(type, id);
        DebugLog.Write("crafter", $"step {actionIndex + 1}/{rotation.Count} {CurrentAction} ({id}){(fallback ? " fallback" : "")}: {(actionPending ? "sent" : "not sent")}");
        if (actionPending) actionsUsed++;
        if (actionPending && raphaelId == StellarSteadyHand) SteadyHandUsed++;
        if (actionPending && !fallback)
        {
            if (watch is { Active: true }) watch.Used(raphaelId, stepCondition);
            actionIndex++;
        }
        Wait(0.25);
    }

    private void WaitResult()
    {
        if (Visible("Synthesis") || Services.Condition[ConditionFlag.ExecutingCraftingAction]) { Wait(0.2); return; }
        if (!Ready(Current!.Cosmic ? CosmicNotebook : "RecipeNote"))
        {
            // After a mission's last craft the game doesn't open its crafting log again: a moment is enough there.
            if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(Current.Cosmic ? 1.5 : 8);
            if (DateTime.UtcNow < deadline) { Wait(0.2); return; }
        }
        doneThisJob++;
        CraftsDone++;
        CurrentAction = "";
        lastCraftEnded = DateTime.UtcNow;
        if (actionsUsed > 0 && synthStarted != default)
        {
            // Seconds per action, measured on this craft, for estimates.
            var t = plugin.Config.Timings;
            var perAction = (lastCraftEnded - synthStarted).TotalSeconds / actionsUsed;
            if (perAction is > 0.5 and < 10) t.SecondsPerAction = Timings.Blend(t.SecondsPerAction, perAction);
        }
        var name = plugin.Plans.Catalog!.ItemName(Current!.Recipe.ItemId);
        log($"Crafted {name} ({doneThisJob} of {Current.Crafts}).", false);
        if (watch is { } w)
        {
            watch = null;
            // The window shows the last action's result when its step number moved on after it; otherwise the simulator's
            // numbers hold as long as they matched the game's.
            var updated = seenStep != stepIndexBefore;
            var final = updated ? seenQuality : w.Active ? w.PredictedQuality : -1;
            DebugLog.Write("crafter", $"reacted {w.GoodReactions}× on Good, {w.ExcellentReactions}× on Excellent ({w.Replans} re-planned); final quality {final} (window {seenQuality}{(updated ? "" : " from before the last action")}, "
                                      + $"simulator {w.PredictedQuality}{(w.Active ? "" : ", stopped: " + w.Stopped)}), planned {rotationQuality}");
            if (ConditionReaction.Summary(w.GoodReactions, w.ExcellentReactions, final, rotationQuality, w.Replans) is { } reacted) log(reacted, false);
        }
        if (Current.Cosmic && macro.Contains(StellarSteadyHand)) macro.Clear();
        DebugLog.Write("crafter", $"crafted {name} ({doneThisJob} of {Current.Crafts}), {actionsUsed} actions, rotation quality {LastQuality}, Steady Hand used {SteadyHandUsed}");
        Crafted?.Invoke(Current.Recipe);
        if (solveAgainAfterCraft)
        {
            // The synthesis window showed numbers the rotation wasn't worked out for, or the game refused one of its
            // actions: a new one for the next craft.
            solveAgainAfterCraft = false;
            macro.Clear();
        }
        // The delineations ran low: the next craft's rotation leaves the specialist actions out.
        if (macro.Any(SpecialistActions.Contains) && GameState.Delineations < DelineationsPerCraft) macro.Clear();
        if (pauseRequested) { Pause(); return; }
        Wait(0.6);
        // A dropped rotation (the mission's Steady Hand uses changed) is worked out again before the next craft.
        Go(doneThisJob < Current.Crafts ? macro.Count == 0 ? Step.Solve : Step.Ingredients : Step.NextRecipe, keepWait: true);
    }

    // ------------------------------------------------------------------ reacting to Good and Excellent steps

    // Crafting buffs by status id (as Artisan reads them, RawInformation/Character/Buffs.cs).
    private const uint InnerQuietStatus = 251, WasteNotStatus = 252, GreatStridesStatus = 254, WasteNot2Status = 257, ManipulationStatus = 1164,
        InnovationStatus = 2189, MuscleMemoryStatus = 2191, VenerationStatus = 2226, HeartAndSoulStatus = 2665, TrainedPerfectionStatus = 3813;

    /// <summary>
    /// A watch for this craft when it reacts to Good and Excellent steps: the setting is on, the craft wants quality (an HQ
    /// craft or a collectable), it's a normal recipe (not expert, not a Cosmic Exploration mission), and the simulator plays
    /// the rotation to the quality Raphael said. Otherwise null, and why.
    /// </summary>
    private CraftWatch? StartWatch(out string why)
    {
        if (!Reacting(out why)) return null;
        var solution = planned!;
        return CraftWatch.For(solution.Sim!, solution.InitialQuality, solution.MaxQuality, macro, solution.FinalQuality, out why);
    }

    /// <summary>
    /// Whether this recipe's crafts react to Good and Excellent steps (the setting is on, the craft wants quality, a normal
    /// recipe, and Raphael said what the rotation was worked out for); otherwise why not.
    /// </summary>
    private bool Reacting(out string why)
    {
        var job = Current!;
        why = "";
        if (!plugin.Config.Crafting.ReactToConditions) { why = "turned off"; return false; }
        if (job.Cosmic || job.SteadyHand > 0) { why = "a Cosmic Exploration mission"; return false; }
        if ((job.Quality ?? (job.Hq ? -1 : 0)) == 0) { why = "no quality wanted"; return false; }
        if (!CraftActions.NormalConditions(job.Recipe.RecipeId)) { why = "an expert recipe, or one with other conditions"; return false; }
        if (planned is not { Simulable: true } solution || !solution.Actions.SequenceEqual(macro))
        {
            why = "Raphael didn't say what this rotation was worked out for";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Gets the serve process ready for this recipe's rotation (it prepares in the background; the same rotation again
    /// changes nothing). The request is the one the rotation was solved for, with the options raphael-cli kept (it drops the
    /// specialist actions, then Manipulation, when it fails with them).
    /// </summary>
    private void WarmReplanner()
    {
        if (Current == null || !Reacting(out _) || plannedFor is not { } asked || planned?.Sim is not { } sim)
        {
            serveRequest = null;
            return;
        }
        serveRequest = asked with { Specialist = asked.Specialist && sim.HeartAndSoul, Manipulation = asked.Manipulation && sim.Manipulation };
        serveSim = sim;
        raphael.Server.Prepare(serveRequest, sim);
    }

    private void StopWatching(string why)
    {
        if (watch is not { Active: true } w) return;
        w.Stop(why);
        DebugLog.Write("crafter", $"[!] no more reacting this craft: {why}");
    }

    /// <summary>
    /// Before the next action: the game's numbers against the simulator's, then on a Good or Excellent step maybe a change to
    /// the rest of the rotation. False while waiting a moment for numbers that differ (the buffs can lag behind the window).
    /// </summary>
    private bool WatchStep(AtkUnitBase* synth)
    {
        var w = watch!;
        if (synth->AtkValuesCount <= 15)
        {
            StopWatching("the Synthesis window has fewer values than expected");
            return true;
        }
        var reading = ReadStep(synth);
        if (w.Compare(reading, out var condition) is { } differs)
        {
            if (differsSince == default) differsSince = DateTime.UtcNow;
            if (DateTime.UtcNow - differsSince < TimeSpan.FromSeconds(1.5)) { Wait(0.1); return false; }
            differsSince = default;
            StopWatching($"before step {actionIndex + 1} the game shows {differs}; the game: {reading}; the simulator: {w.Predicted}");
            return true;
        }
        differsSince = default;
        watchedAt = actionIndex;
        stepCondition = condition;
        var rest = rotation.GetRange(actionIndex, rotation.Count - actionIndex);
        // The step a re-plan already began with Heart and Soul or Quick Innovation: its actions stand.
        if (actionIndex <= replanCoversUntil || !w.CanReact(condition, rest)) return true;
        var blocked = serveRequest == null ? "not prepared for this rotation" : Replanning.Blocked(w.Predicted, raphael.Server.NotReady(serveRequest));
        if (blocked == null && raphael.Server.Solve(serveRequest!, w.Predicted, condition) is { } sent)
        {
            replanning = (sent.Id, sent.Answer, DateTime.UtcNow, condition);
            Wait(0.05);
            return false;
        }
        CheapReaction(condition, blocked ?? "Raphael is busy");
        return true;
    }

    /// <summary>
    /// Waiting for Raphael's re-plan of this step: true once it's settled (the new rest of the rotation, or the cheap reaction
    /// after an error, no rotation or <see cref="Replanning.Wait"/> without an answer).
    /// </summary>
    private bool ReplanArrived()
    {
        var (id, answer, since, condition) = replanning!.Value;
        var w = watch!;
        if (!answer.IsCompleted)
        {
            if (DateTime.UtcNow - since < Replanning.Wait) { Wait(0.05); return false; }
            replanning = null;
            raphael.Server.GiveUp(id);
            CheapReaction(condition, $"no answer within {Replanning.Wait.TotalSeconds:0} seconds");
            return true;
        }
        replanning = null;
        var a = answer.Result;
        var waited = (int)(DateTime.UtcNow - since).TotalMilliseconds;
        if (a.Outcome != ServeOutcome.Solved)
        {
            CheapReaction(condition, a.Outcome == ServeOutcome.NoSolution ? "Raphael found no rotation from here" : "Raphael failed: " + a.Error);
            return true;
        }
        var rest = rotation.GetRange(actionIndex, rotation.Count - actionIndex);
        var state = Replanning.Summary(w.Predicted, w.Settings);
        if (w.Replan(condition, rest, a.Actions, out var quality, out var beaten, out var why) is not { } actions)
        {
            DebugLog.Write("crafter", $"step {actionIndex + 1} is {condition}: re-planned in {waited} ms ({state}), not taken: {why} "
                                      + $"(predicted quality {w.Shown(quality)}, cheap reaction {w.Shown(beaten)})");
            CheapReaction(condition, "the re-plan wasn't taken");
            return true;
        }
        var changed = !actions.SequenceEqual(rest);
        if (changed)
        {
            rotation.RemoveRange(actionIndex, rotation.Count - actionIndex);
            rotation.AddRange(actions);
            replannedFrom = replannedFrom < 0 ? actionIndex : Math.Min(replannedFrom, actionIndex);
            var lead = RaphaelSim.FromIds(actions) is { } sim ? Replanning.LeadingNonSteps(sim) : 0;
            if (lead > 0) replanCoversUntil = actionIndex + lead;
        }
        DebugLog.Write("crafter", $"step {actionIndex + 1} is {condition}: re-planned in {waited} ms (Raphael {a.Ms} ms; {state}): "
                                  + (changed ? $"{actions.Count} actions from here, {string.Join(" ", actions.Take(3).Select(CraftActions.Name))}…" : "the rotation stays")
                                  + $"; predicted quality {w.Shown(quality)}, cheap reaction {w.Shown(beaten)}");
        return true;
    }

    /// <summary>The cheap reaction (<see cref="ConditionReaction"/>) takes this step, with why it wasn't re-planned (debug log).</summary>
    private void CheapReaction(CraftCondition condition, string why)
    {
        var w = watch!;
        DebugLog.Write("crafter", $"step {actionIndex + 1} is {condition}: cheap reaction ({why})");
        if (w.React(condition, rotation.GetRange(actionIndex, rotation.Count - actionIndex)) is { } edit)
        {
            var was = rotation[actionIndex];
            rotation.RemoveRange(actionIndex, rotation.Count - actionIndex);
            rotation.AddRange(edit.Actions);
            if (edit.Choice.Added) addedAt = actionIndex;
            DebugLog.Write("crafter", $"step {actionIndex + 1} is {condition}: planned {CraftActions.Name(was)}, now {edit.Choice.Edit}; "
                                      + $"predicted quality {w.Shown(edit.Choice.KeptQuality)} -> {w.Shown(edit.Choice.ChosenQuality)}");
        }
    }

    /// <summary>
    /// The step as the game shows it: progress, quality, durability and condition from the Synthesis window (AtkValues 5, 9,
    /// 7 and 12, as Artisan reads them), the CP and the crafting buffs from the player.
    /// </summary>
    private static CraftReading ReadStep(AtkUnitBase* synth)
    {
        var values = synth->AtkValues;
        var progress = values[5].Int;
        var quality = values[9].Int;
        var durability = values[7].Int;
        var condition = values[12].Int;
        var me = Services.Objects.LocalPlayer;
        int Param(uint id) => me?.StatusList.FirstOrDefault(s => s.StatusId == id) is { } status ? status.Param : 0;
        bool Has(uint id) => me?.StatusList.Any(s => s.StatusId == id) == true;
        var wasteNot = Param(WasteNot2Status);
        if (wasteNot == 0) wasteNot = Param(WasteNotStatus);
        return new CraftReading(progress, quality, durability, (int)(me?.CurrentCp ?? 0), condition, Param(InnerQuietStatus), Param(InnovationStatus),
            Param(VenerationStatus), Param(GreatStridesStatus), wasteNot, Param(ManipulationStatus), Param(MuscleMemoryStatus),
            Has(HeartAndSoulStatus), Has(TrainedPerfectionStatus));
    }

    /// <summary>The run log's note about a rotation that falls short of HQ, once its HQ chance is known (or after a few seconds without).</summary>
    private void FlushQualityNote()
    {
        if (qualityNote is not { } note) return;
        var estimate = raphael.HqEstimateFor(note.Request, note.Solution, note.React);
        if (estimate == null && DateTime.UtcNow - note.Since < TimeSpan.FromSeconds(5)) return;
        qualityNote = null;
        log(estimate is { } e
            ? $"{note.Text}. Estimated with the Good and Excellent steps that come along: {HqChance.Describe(e.HqPercent, note.React && raphael.Server.Supported != false)}. {note.Hint}"
            : $"{note.Text}, so it comes out HQ only sometimes. {note.Hint}", true);
    }

    // ------------------------------------------------------------------ recipes the game adjusts

    /// <summary>
    /// The recipe's numbers in the sheet: its recipe level, and progress, quality and durability with the recipe's
    /// factors applied (as the game computes them).
    /// </summary>
    private static LiveRecipe? SheetNumbers(uint recipeId)
    {
        if (Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Recipe>().GetRowOrDefault(recipeId) is not { } r) return null;
        if (r.RecipeLevelTable.ValueNullable is not { } level) return null;
        return Scaled(r, (int)r.RecipeLevelTable.RowId, level);
    }

    private static LiveRecipe Scaled(Lumina.Excel.Sheets.Recipe r, int rlvl, Lumina.Excel.Sheets.RecipeLevelTable level)
        => new(rlvl, (int)(level.Difficulty * (long)r.DifficultyFactor / 100), (int)(level.Quality * (long)r.QualityFactor / 100),
            (int)(level.Durability * (long)r.DurabilityFactor / 100));

    /// <summary>
    /// With the recipe open in the crafting log: compares the recipe level the game uses with the sheet's and writes
    /// both to the debug log. A different level (the quest syncing the recipe to the crafter's) gives the numbers of that
    /// level to plan with. True when they changed what the rotation was worked out for.
    /// </summary>
    private bool CheckLiveRecipe()
    {
        var entry = SelectedEntry();
        var id = Current!.Recipe.RecipeId;
        if (entry == null || SheetNumbers(id) is not { } sheet) return false;
        var rlvl = (int)entry->RecipeLevelTableId;
        DebugLog.Write("crafter", $"recipe {id} in the crafting log: recipe level {rlvl}, difficulty {entry->Difficulty}, quality {entry->Quality}, durability {entry->Durability}, "
                                  + $"class level {entry->ClassJobLevel}; the sheet: {sheet}");
        if (rlvl <= 0 || rlvl == sheet.RecipeLevel) return false;
        var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Recipe>().GetRow(id);
        if (Services.Data.GetExcelSheet<Lumina.Excel.Sheets.RecipeLevelTable>().GetRowOrDefault((uint)rlvl) is not { } level) return false;
        var numbers = Scaled(row, rlvl, level);
        if (live == numbers) return false;
        live = numbers;
        DebugLog.Write("crafter", $"[!] recipe {id}: the game uses recipe level {rlvl}, not the sheet's {sheet.RecipeLevel}; planning with {numbers}");
        return true;
    }

    /// <summary>
    /// At the start of a synthesis: the progress, quality and durability the synthesis window shows, next to the sheet's
    /// and the ones the rotation was worked out for (debug log). When the window's differ, the next craft gets a
    /// rotation for them; this one finishes with Basic Synthesis if its rotation falls short.
    /// </summary>
    private void CheckSynthesisWindow(AtkUnitBase* addon)
    {
        var synth = (FFXIVClientStructs.FFXIV.Client.UI.AddonSynthesis*)addon;
        var progress = Number(synth->MaxProgress);
        var quality = Number(synth->MaxQuality);
        var durability = Number(synth->StartingDurability);
        var id = Current!.Recipe.RecipeId;
        var sheet = SheetNumbers(id);
        var planned = live ?? sheet;
        DebugLog.Write("crafter", $"recipe {id} at the start of the synthesis: progress {progress}, quality {quality}, durability {durability}; "
                                  + $"the sheet: {sheet?.ToString() ?? "unknown"}; planned with {planned?.ToString() ?? "unknown"}");
        if (progress <= 0 || durability <= 0 || planned is not { } p) return;
        if (p.Progress == progress && p.Durability == durability && (quality <= 0 || p.Quality == quality)) return;
        var rlvl = SelectedEntry() is var e && e != null && e->RecipeLevelTableId > 0 ? (int)e->RecipeLevelTableId : p.RecipeLevel;
        live = new LiveRecipe(rlvl, progress, quality > 0 ? quality : p.Quality, durability);
        solveAgainAfterCraft = true;
        DebugLog.Write("crafter", $"[!] recipe {id}: the synthesis window's numbers differ from the plan's; the next craft is planned with {live}");
    }

    /// <summary>A number a text node shows ("3,500" → 3500); 0 when it has none.</summary>
    private static int Number(AtkTextNode* node)
    {
        if (node == null) return 0;
        var digits = new string(node->NodeText.ToString().Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : 0;
    }

    // ------------------------------------------------------------------ helpers

    private void Pause()
    {
        if (leaveStanceOnPause && GameState.InCraftingStance)
        {
            Status = "Closing the crafting log";
            afterLeave = Step.Paused;
            Go(Step.LeaveStance);
            return;
        }
        Status = "Paused";
        CurrentAction = "";
        Go(Step.Paused);
    }

    private void Fail(string message)
    {
        Failure = message;
        Status = message;
        CurrentAction = "";
        solving = null;
        Go(Step.Failed);
    }

    private void Go(Step next, bool keepWait = false)
    {
        if (next == Step.Finished) Status = "All crafts done";
        if (next is Step.Finished or Step.Failed)
        {
            serveRequest = null;
            raphael.Server.End(next == Step.Finished ? "all crafts done" : "crafting stopped");
        }
        step = next;
        deadline = default;
        if (next is not Step.Ingredients) { attempts = 0; swaps = 0; }
        if (!keepWait) waitUntil = DateTime.UtcNow;
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);

    private static Ingredient? Missing(CraftJob job) => job.Cosmic ? null : MissingIngredient(job.Recipe);

    private static Ingredient? MissingIngredient(RecipeInfo recipe)
    {
        foreach (var ing in recipe.Ingredients)
            if (GameState.BagCount(ing.ItemId) < ing.Amount) return ing;
        return null;
    }

    private static AtkUnitBase* Addon(string name)
        => GenericHelpers.TryGetAddonByName<AtkUnitBase>(name, out var a) ? a : null;

    private static bool Visible(string name) => Addon(name) is var a && a != null && a->IsVisible;

    private static bool Ready(string name) => Addon(name) is var a && a != null && GenericHelpers.IsAddonReady(a);

    private static void CloseIfOpen(string name)
    {
        if (Addon(name) is var a && a != null && a->IsVisible) a->Close(true);
    }

    private static RecipeNote.RecipeEntry* SelectedEntry()
    {
        var note = RecipeNote.Instance();
        return note == null || note->RecipeList == null ? null : note->RecipeList->SelectedRecipe;
    }

    private static uint SelectedRecipe() => SelectedEntry() is var e && e != null ? e->RecipeId : 0u;
}
