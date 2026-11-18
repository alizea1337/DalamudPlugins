using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
using Provenance.Core.Crafting;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>One recipe to craft a number of times.</summary>
internal sealed record CraftJob(RecipeInfo Recipe, int Crafts, bool FinalItem);

/// <summary>
/// Crafts a queue of recipes, one synthesis at a time: right job and gearset, repair, food and
/// medicine, a Raphael rotation for the current stats, ingredients, then the rotation action by
/// action. Ticked every frame on the framework thread. Pause and stop always land between crafts,
/// so no materials are lost.
/// </summary>
internal sealed unsafe class Crafter
{
    private enum Step { Idle, NextRecipe, LeaveStance, SwitchJob, Repair, Consumables, Solve, OpenRecipe, Ingredients, Synthesize, Acting, WaitResult, Paused, Finished, Failed }

    private const uint BasicSynthesis = 100001;
    private const uint RepairAction = 6;

    private readonly Plugin plugin;
    private readonly Raphael raphael = new();
    private readonly Action<string, bool> log;
    private readonly Queue<CraftJob> queue = new();
    private readonly HashSet<string> warnedOnce = new();
    private readonly Dictionary<uint, DateTime> lastUse = new();

    private Step step = Step.Idle;
    private Step afterLeave;
    private DateTime waitUntil;
    private DateTime deadline;
    private int attempts;
    private Task<List<uint>>? solving;
    private List<uint> macro = new();
    private int actionIndex;
    private int stepIndexBefore = -1;
    private bool actionPending;
    private bool fallback;
    private int doneThisJob;
    private bool pauseRequested;

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
    public int ActionNumber => Math.Min(actionIndex, macro.Count);
    public int ActionCount => macro.Count;
    public IReadOnlyList<CraftJob> Upcoming => queue.ToList();
    public List<(CraftJob Job, int Done, string? Skipped)> History { get; } = new();

    public bool Finished => step is Step.Finished or Step.Failed;
    public bool IsPaused => step == Step.Paused;
    /// <summary>A synthesis is under way; stopping now would lose materials.</summary>
    public bool MidCraft => step is Step.Synthesize or Step.Acting or Step.WaitResult;

    public void Begin(IEnumerable<CraftJob> jobs)
    {
        queue.Clear();
        History.Clear();
        warnedOnce.Clear();
        lastUse.Clear();
        foreach (var j in jobs.Where(j => j.Crafts > 0)) queue.Enqueue(j);
        CraftsTotal = queue.Sum(j => j.Crafts);
        CraftsDone = 0;
        Current = null;
        Failure = null;
        pauseRequested = false;
        Go(Step.NextRecipe);
    }

    /// <summary>Pause after the craft that's under way (or right away between crafts).</summary>
    public void RequestPause() => pauseRequested = true;

    public void Resume()
    {
        pauseRequested = false;
        if (step == Step.Paused) Go(Current != null && doneThisJob < Current.Crafts ? Step.Repair : Step.NextRecipe);
    }

    public void Tick()
    {
        if (Finished || step is Step.Idle or Step.Paused || DateTime.UtcNow < waitUntil) return;
        try
        {
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
        }
        if (Current == null)
        {
            if (!queue.TryDequeue(out var next))
            {
                Status = "All crafts done";
                if (GameState.InCraftingStance) { afterLeave = Step.Finished; Go(Step.LeaveStance); }
                else Go(Step.Finished);
                return;
            }
            Current = next;
            doneThisJob = 0;
            macro.Clear();
        }

        var catalog = plugin.Plans.Catalog!;
        if (MissingIngredient(Current.Recipe) is { } missing)
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
            Go(afterLeave);
            return;
        }
        if (Services.Condition[ConditionFlag.ExecutingCraftingAction] || Visible("Synthesis")) { Wait(0.3); return; }
        if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(10);
        if (DateTime.UtcNow > deadline) { Fail("Couldn't close the crafting log."); return; }
        if (Addon("RecipeNote") is var note && note != null) note->Close(true);
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
            deadline = DateTime.UtcNow.AddSeconds(8);
            Wait(0.5);
            return;
        }
        if (DateTime.UtcNow > deadline) { Fail($"Couldn't switch to {Current.Recipe.Job}."); return; }
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
        var (food, medicine) = plugin.Config.Consumables.For(Discipline.Hand);
        foreach (var (choice, isMedicine) in new[] { (food, false), (medicine, true) })
        {
            if (choice.ItemId == 0 || GameState.BuffSecondsLeft(isMedicine) > 60) continue;
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
            lastUse[choice.ItemId] = DateTime.UtcNow;
            Wait(2.5);
            return;
        }
        Go(Step.Solve);
    }

    private void Solve()
    {
        var r = Current!.Recipe;
        if (solving == null)
        {
            if (macro.Count > 0) { Go(Step.OpenRecipe); return; }
            var stats = GameState.LiveStats(Discipline.Hand);
            var c = plugin.Config.Crafting;
            var manipulation = plugin.Plans.Catalog!.ManipulationQuest.TryGetValue(r.Job.ClassJobId(), out var quest) && QuestManager.IsQuestComplete(quest);
            var request = new RaphaelRequest(r.RecipeId, GameState.JobLevel(r.Job.ClassJobId()), stats[0], stats[1], stats[2],
                manipulation, c.UseSpecialistActions && GameState.SpecialistEquipped(),
                Current.FinalItem && c.AimForHq ? null : 0);
            Status = "Working out the rotation";
            solving = raphael.Solve(request);
            Wait(0.1);
            return;
        }
        if (!solving.IsCompleted) { Wait(0.1); return; }
        var task = solving;
        solving = null;
        if (!task.IsCompletedSuccessfully)
        {
            Fail("Raphael couldn't plan this craft: " + (task.Exception?.GetBaseException().Message ?? "cancelled"));
            return;
        }
        macro = task.Result;
        Go(Step.OpenRecipe);
    }

    private void OpenRecipe()
    {
        var id = Current!.Recipe.RecipeId;
        if (SelectedRecipe() == id && Ready("RecipeNote")) { Wait(0.4); Go(Step.Ingredients, keepWait: true); return; }
        if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(10);
        if (DateTime.UtcNow > deadline) { Fail("The recipe didn't open."); return; }
        Status = "Opening the recipe";
        AgentRecipeNote.Instance()->OpenRecipeByRecipeId(id);
        Wait(1);
    }

    /// <summary>Assigns ingredients one click at a time: NQ first, HQ for the rest.</summary>
    private void Ingredients()
    {
        if (pauseRequested) { Pause(); return; }
        if (!Ready("RecipeNote") || SelectedRecipe() != Current!.Recipe.RecipeId) { Go(Step.OpenRecipe); return; }
        if (MissingIngredient(Current.Recipe) is { } missing)
        {
            var catalog = plugin.Plans.Catalog!;
            var reason = $"ran out of {catalog.ItemName(missing.ItemId)}";
            log($"Stopped crafting {catalog.ItemName(Current.Recipe.ItemId)} after {doneThisJob}: {reason}.", true);
            History.Add((Current, doneThisJob, reason));
            CraftsTotal -= Current.Crafts - doneThisJob;
            Current = null;
            Go(Step.NextRecipe);
            return;
        }

        var entry = SelectedEntry();
        if (entry == null) { Wait(0.3); return; }
        var ingredients = entry->Ingredients;
        for (var i = 0; i < ingredients.Length; i++)
        {
            ref var ing = ref ingredients[i];
            if (ing.ItemId == 0 || ing.Amount == 0) continue;
            if (ing.NQCount + ing.HQCount >= ing.Amount) continue;
            if (++attempts > 60) { Fail("Couldn't fill in the ingredients."); return; }
            var hq = GameState.BagCount(ing.ItemId, false) <= ing.NQCount;
            if (Addon("RecipeNote") is var note && note != null)
                new AddonMaster.RecipeNote((nint)note).Material((uint)i, hq);
            Wait(0.15);
            return;
        }
        attempts = 0;
        Go(Step.Synthesize);
    }

    private void Synthesize()
    {
        if (Visible("Synthesis"))
        {
            actionIndex = 0;
            actionPending = false;
            fallback = false;
            stepIndexBefore = -1;
            Status = $"Crafting {plugin.Plans.Catalog!.ItemName(Current!.Recipe.ItemId)}";
            Go(Step.Acting);
            return;
        }
        if (deadline == default)
        {
            if (Addon("RecipeNote") is var note && note != null) new AddonMaster.RecipeNote((nint)note).Synthesize();
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
        if (Services.Condition[ConditionFlag.ExecutingCraftingAction] || !GenericHelpers.IsAddonReady(synth)) { Wait(0.1); return; }

        var stepIndex = synth->AtkValuesCount > 15 ? synth->AtkValues[15].Int : -1;
        if (actionPending)
        {
            if (stepIndex == stepIndexBefore && DateTime.UtcNow < deadline) { Wait(0.1); return; }
            actionPending = false; // applied, or timed out and we move on
        }

        var job = Current!.Recipe.Job.ClassJobId();
        uint raphaelId;
        if (!fallback && actionIndex < macro.Count) raphaelId = macro[actionIndex];
        else
        {
            if (!fallback) log("The rotation ended before the item was finished; completing it with Basic Synthesis.", true);
            fallback = true;
            raphaelId = BasicSynthesis;
        }

        var (type, id) = CraftActions.For(raphaelId, job);
        CurrentAction = CraftActions.Name(raphaelId);
        if (!GameState.CanUse(type, id))
        {
            if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(2);
            if (DateTime.UtcNow < deadline) { Wait(0.1); return; }
            log($"Couldn't use {CurrentAction}; finishing with Basic Synthesis.", true);
            fallback = true;
            deadline = default;
            return;
        }
        deadline = DateTime.UtcNow.AddSeconds(6);
        stepIndexBefore = stepIndex;
        actionPending = GameState.Use(type, id);
        if (actionPending && !fallback) actionIndex++;
        Wait(0.25);
    }

    private void WaitResult()
    {
        if (Visible("Synthesis") || Services.Condition[ConditionFlag.ExecutingCraftingAction]) { Wait(0.2); return; }
        if (!Ready("RecipeNote"))
        {
            if (deadline == default) deadline = DateTime.UtcNow.AddSeconds(8);
            if (DateTime.UtcNow < deadline) { Wait(0.2); return; }
        }
        doneThisJob++;
        CraftsDone++;
        CurrentAction = "";
        var name = plugin.Plans.Catalog!.ItemName(Current!.Recipe.ItemId);
        log($"Crafted {name} ({doneThisJob} of {Current.Crafts}).", false);
        if (pauseRequested) { Pause(); return; }
        Wait(0.6);
        Go(doneThisJob < Current.Crafts ? Step.Ingredients : Step.NextRecipe, keepWait: true);
    }

    // ------------------------------------------------------------------ helpers

    private void Pause()
    {
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
        step = next;
        deadline = default;
        if (next is not Step.Ingredients) attempts = 0;
        if (!keepWait) waitUntil = DateTime.UtcNow;
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);

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
