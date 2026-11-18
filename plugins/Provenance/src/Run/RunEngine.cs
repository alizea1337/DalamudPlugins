using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using Provenance.Core.Estimating;
using Provenance.Core.Planning;
using Provenance.Core.Run;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

internal enum RunPhase
{
    Idle,
    Running,
    /// <summary>Pause asked for; waits for the current node or craft to finish.</summary>
    Pausing,
    Paused,
    /// <summary>Stop asked for during a craft; waits for it to finish so no materials are lost.</summary>
    Stopping,
    Finished,
    Stopped,
}

internal enum RunStage { Learn, Gather, Craft }

internal sealed record RunLogEntry(DateTime Time, string Text, bool Problem);

/// <summary>
/// Runs an order: first the gathering step (GatherBuddy Reborn with Provenance's list), then the
/// crafting step (Provenance's crafter with Raphael rotations), and watches the stop rules (death,
/// a nearly full inventory, plugins going away, duties) throughout.
/// </summary>
internal sealed class RunEngine : IDisposable
{
    private const int MinFreeSlots = 2;
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(500);

    private readonly Plugin plugin;
    private readonly GatherBuddyLists lists = new();
    private readonly List<RunLogEntry> log = new();
    private DateTime nextCheck = DateTime.MinValue;
    private DateTime gbrOffSince = DateTime.MinValue;
    private int reEnables;
    private bool pausedForDuty;
    private string? pendingStop;
    private double gatherActiveSeconds;
    private readonly Queue<uint> toLearn = new();
    private DateTime learnDeadline;
    private bool learnUsed;
    private DateTime lastGatherTick;
    private bool pendingStopProblem;

    public RunEngine(Plugin plugin)
    {
        this.plugin = plugin;
        Crafter = new Crafter(plugin, Add);
    }

    public RunPhase Phase { get; private set; } = RunPhase.Idle;
    public RunStage Stage { get; private set; } = RunStage.Gather;
    public string Headline { get; private set; } = "Not running";
    public string Detail { get; private set; } = "";
    public DateTime? StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public IReadOnlyList<GatherTarget> Targets { get; private set; } = Array.Empty<GatherTarget>();
    public Crafter Crafter { get; }
    public IReadOnlyList<RunLogEntry> Log => log;

    public bool Active => Phase is RunPhase.Running or RunPhase.Pausing or RunPhase.Paused or RunPhase.Stopping;

    /// <summary>Recipes to craft; ordered items marked HQ get a quality rotation, everything else the fastest.</summary>
    private List<CraftJob> CraftJobs()
    {
        var hqItems = plugin.Config.Order.Lines.Where(o => o.Hq).Select(o => o.ItemId).ToHashSet();
        return plugin.Plans.Plan.CraftSteps.Where(l => l.Recipe != null && l.Crafts > 0)
            .Select(l => new CraftJob(l.Recipe!, l.Crafts, l.IsTarget && l.Recipe!.CanHq && hqItems.Contains(l.ItemId))).ToList();
    }

    /// <summary>Why a run can't start now, or null when it can.</summary>
    public string? StartBlocker()
    {
        var plan = plugin.Plans.Plan;
        if (Active) return "A run is already going.";
        if (plugin.Plans.Catalog == null) return "Still reading game data.";
        if (plugin.Config.Order.IsEmpty) return "Add items to the order first.";
        if (!plan.CanStart) return "Get the missing items first; they are listed at the top of the plan.";
        if (!Services.ClientState.IsLoggedIn) return "Log in first.";
        if (Stock.FreeSlots() <= MinFreeSlots) return $"Your inventory has {MinFreeSlots} or fewer free slots.";
        var gather = GatherTargets.Build(plan, GbrCount).Count > 0;
        var craft = plan.CraftSteps.Any(l => l.Crafts > 0);
        if (!gather && !craft) return "Nothing to gather or craft for this order.";
        if (gather && !GatherBuddyIpc.Available) return "GatherBuddy Reborn isn't loaded.";
        if (gather && !Vnav.Available) return "vnavmesh isn't loaded.";
        if (gather && plan.Count(Supply.Fish) > 0 && !Ipc.IsLoaded("AutoHook")) return "AutoHook isn't loaded; this order has fish to catch.";
        if (craft && Raphael.ExecutablePath() == null) return "raphael-cli.exe wasn't found; reinstall Provenance (or install GatherBuddy Reborn, which ships it).";
        return null;
    }

    public void Start()
    {
        if (StartBlocker() is { } why)
        {
            Notify("Can't start: " + why, true);
            return;
        }

        log.Clear();
        reEnables = 0;
        pausedForDuty = false;
        pendingStop = null;
        gbrOffSince = DateTime.MinValue;
        StartedAt = DateTime.Now;
        EndedAt = null;
        Detail = "";
        Phase = RunPhase.Running;

        // Books and tomes come first: recipes and legendary nodes need them.
        toLearn.Clear();
        foreach (var l in plugin.Plans.Plan.Learn.Where(l => GameState.BagCount(l.ItemId) > 0)) toLearn.Enqueue(l.ItemId);
        if (toLearn.Count > 0) StartLearning();
        else StartAfterLearning();
        if (Active) Notify("Run started for " + plugin.Config.Order.Name + ".", false);
    }

    private void StartLearning()
    {
        Stage = RunStage.Learn;
        Headline = "Learning";
        learnDeadline = default;
        Add($"Reading {toLearn.Count} {(toLearn.Count == 1 ? "book" : "books")} first.", false);
    }

    private void StartAfterLearning()
    {
        var targets = GatherTargets.Build(plugin.Plans.Plan, GbrCount);
        if (targets.Count > 0) StartGathering(targets);
        else StartCrafting();
    }

    private void StartGathering(List<GatherTarget> targets)
    {
        Stage = RunStage.Gather;
        var unknown = lists.Install(targets);
        if (lists.Error != null)
        {
            End(RunPhase.Stopped, "Couldn't start", lists.Error, true);
            return;
        }
        if (unknown.Count > 0)
        {
            var catalog = plugin.Plans.Catalog!;
            Add($"GatherBuddy Reborn doesn't list {string.Join(", ", unknown.Select(catalog.ItemName))} as gatherable; get them yourself.", true);
            targets = targets.Where(t => !unknown.Contains(t.ItemId)).ToList();
        }
        Targets = targets;
        gatherActiveSeconds = 0;
        lastGatherTick = DateTime.UtcNow;
        GatherBuddyIpc.SetEnabled(true);
        Headline = "Gathering";
        Add($"Gathering {targets.Count} {(targets.Count == 1 ? "item" : "items")} with GatherBuddy Reborn.", false);
    }

    private void StartCrafting()
    {
        Stage = RunStage.Craft;
        var jobs = CraftJobs();
        if (jobs.Count == 0) { Finish(); return; }
        Crafter.Begin(jobs);
        Headline = "Crafting";
        Add($"Crafting {jobs.Sum(j => j.Crafts)} items across {jobs.Count} {(jobs.Count == 1 ? "recipe" : "recipes")}.", false);
    }

    /// <summary>Soft pause: finishes the node being gathered or the item being crafted, then stops.</summary>
    public void Pause()
    {
        if (Phase != RunPhase.Running) return;
        Phase = RunPhase.Pausing;
        if (Stage == RunStage.Craft)
        {
            Crafter.RequestPause();
            Headline = "Pausing after this craft";
        }
        else Headline = "Pausing after this node";
        Add("Pause asked for; finishing what's under way first.", false);
    }

    public void Resume()
    {
        if (Phase is not (RunPhase.Paused or RunPhase.Pausing)) return;
        if (Stage == RunStage.Learn) { Phase = RunPhase.Running; Add("Resumed.", false); return; }
        if (Stage == RunStage.Craft) Crafter.Resume();
        else if (Phase == RunPhase.Paused) GatherBuddyIpc.SetEnabled(true);
        Phase = RunPhase.Running;
        Headline = Stage == RunStage.Craft ? "Crafting" : "Gathering";
        gbrOffSince = DateTime.MinValue;
        Add("Resumed.", false);
    }

    /// <summary>
    /// Ends the run. During a craft it waits for that craft to finish (materials are lost otherwise),
    /// unless <paramref name="now"/> is set because the game or a plugin went away.
    /// </summary>
    public void Stop(string reason = "Stopped.", bool problem = false, bool now = false)
    {
        if (!Active) return;
        if (!now && Stage == RunStage.Craft && Crafter.MidCraft)
        {
            pendingStop = reason;
            pendingStopProblem = problem;
            Crafter.RequestPause();
            Phase = RunPhase.Stopping;
            Headline = "Stopping after this craft";
            Add("Stopping once the current craft is finished.", problem);
            return;
        }
        GatherBuddyIpc.SetEnabled(false);
        lists.Uninstall();
        End(RunPhase.Stopped, problem ? "Stopped" : "Stopped by you", reason, problem);
    }

    private void Finish()
    {
        GatherBuddyIpc.SetEnabled(false);
        lists.Uninstall();
        var skipped = Crafter.History.Where(h => h.Skipped != null).ToList();
        var detail = skipped.Count == 0
            ? "Everything for this order is gathered and crafted."
            : $"Done, but {skipped.Count} {(skipped.Count == 1 ? "recipe was" : "recipes were")} skipped: " + string.Join("; ", skipped.Select(h => $"{plugin.Plans.Catalog!.ItemName(h.Job.Recipe.ItemId)} ({h.Skipped})")) + ". Buying from vendors arrives in a later milestone.";
        End(RunPhase.Finished, "Order done", detail, skipped.Count > 0);
    }

    private void End(RunPhase phase, string headline, string detail, bool problem)
    {
        Phase = phase;
        EndedAt = DateTime.Now;
        Headline = headline;
        Detail = detail;
        Add(detail, problem);
        Notify(phase == RunPhase.Finished ? headline + " for " + plugin.Config.Order.Name + ". " + detail : detail, problem);
    }

    /// <summary>Framework thread, every frame.</summary>
    public void Tick()
    {
        if (!Active) return;

        // The crafter needs every frame; the checks below only twice a second.
        if (Stage == RunStage.Craft && Phase is RunPhase.Running or RunPhase.Pausing or RunPhase.Stopping) Crafter.Tick();
        if (DateTime.UtcNow < nextCheck) return;
        nextCheck = DateTime.UtcNow + CheckEvery;

        if (!Services.ClientState.IsLoggedIn) { Stop("You logged out.", true, now: true); return; }
        var player = Services.Objects.LocalPlayer;
        if (player == null) return; // zoning
        if (player.IsDead) { Stop("You were knocked out. Retrying after a knock-out comes with the full run.", true, now: true); return; }
        if (Stock.FreeSlots() <= MinFreeSlots && Phase != RunPhase.Stopping) { Stop($"Only {MinFreeSlots} inventory slots left.", true); return; }

        if (Stage == RunStage.Learn) TickLearning();
        else if (Stage == RunStage.Gather) TickGathering();
        else TickCrafting();
    }

    /// <summary>Uses each book or tome from the bags and waits until the game counts it as learned.</summary>
    private unsafe void TickLearning()
    {
        if (Phase is RunPhase.Pausing) { Phase = RunPhase.Paused; Headline = "Paused"; return; }
        if (Phase != RunPhase.Running) return;
        if (!toLearn.TryPeek(out var item)) { plugin.Plans.Invalidate(); StartAfterLearning(); return; }
        var name = plugin.Plans.Catalog!.ItemName(item);
        Headline = "Learning";
        Detail = "Reading " + name;

        if (Learned(item))
        {
            Add($"Learned {name}.", false);
            toLearn.Dequeue();
            learnDeadline = default;
            learnUsed = false;
            plugin.Plans.Invalidate();
            return;
        }
        if (ECommons.GenericHelpers.TryGetAddonByName<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("SelectYesno", out var yesno)
            && ECommons.GenericHelpers.IsAddonReady(yesno))
        {
            new ECommons.UIHelpers.AddonMasterImplementations.AddonMaster.SelectYesno((nint)yesno).Yes();
            return;
        }
        if (learnDeadline == default)
        {
            if (GameState.InCraftingStance || Services.Condition[ConditionFlag.Gathering]) return;
            learnDeadline = DateTime.UtcNow.AddSeconds(12);
            learnUsed = GameState.UseItem(item, false);
            return;
        }
        if (DateTime.UtcNow > learnDeadline) Stop($"Couldn't read {name}. Use it from your bags yourself, then start again.", true);
    }

    /// <summary>Master books by their recipe book flag, folklore tomes by the item's learned state.</summary>
    private bool Learned(uint item)
    {
        var book = plugin.Plans.Catalog!.MasterBooks.FirstOrDefault(b => b.ItemId == item);
        return book != null ? PlanService.IsBookLearned(book.Id) : ItemUnlocks.IsLearned(item);
    }

    private void TickGathering()
    {
        if (!GatherBuddyIpc.Available) { Stop("GatherBuddy Reborn was unloaded.", true, now: true); return; }
        if (!Vnav.Available) { Stop("vnavmesh was unloaded.", true, now: true); return; }

        // A duty pop takes over: switch gathering off, carry on once the duty is over.
        var inDuty = Services.Condition[ConditionFlag.BoundByDuty] || Services.Condition[ConditionFlag.BoundByDuty56];
        if (inDuty && !pausedForDuty)
        {
            pausedForDuty = true;
            GatherBuddyIpc.SetEnabled(false);
            Headline = "Paused for a duty";
            Add("Duty started; gathering paused until it ends.", false);
            return;
        }
        if (pausedForDuty)
        {
            if (inDuty) return;
            pausedForDuty = false;
            Add("Duty over; carrying on.", false);
            if (Phase == RunPhase.Running) { GatherBuddyIpc.SetEnabled(true); Headline = "Gathering"; }
        }

        // Time actually spent gathering (not paused, not waiting for timed nodes), for the gathering rate.
        var now = DateTime.UtcNow;
        if (Phase == RunPhase.Running && !GatherBuddyIpc.Waiting) gatherActiveSeconds += (now - lastGatherTick).TotalSeconds;
        lastGatherTick = now;

        if (GatherTargets.AllDone(Targets, GbrCount))
        {
            GatherBuddyIpc.SetEnabled(false);
            lists.Uninstall();
            LearnGatheringRate();
            Add("Gathering done.", false);
            if (Phase == RunPhase.Pausing)
            {
                Phase = RunPhase.Paused;
                Stage = RunStage.Craft;
                Headline = "Paused";
                Crafter.Begin(CraftJobs());
                Crafter.RequestPause();
                return;
            }
            StartCrafting();
            return;
        }

        var busy = Services.Condition[ConditionFlag.Gathering] || Services.Condition[ConditionFlag.ExecutingGatheringAction] || Services.Condition[ConditionFlag.Fishing];
        switch (Phase)
        {
            case RunPhase.Pausing when !busy:
                GatherBuddyIpc.SetEnabled(false);
                Phase = RunPhase.Paused;
                Headline = "Paused";
                Detail = "Resume to carry on gathering.";
                Add("Paused.", false);
                break;

            case RunPhase.Running:
                Detail = GatherBuddyIpc.Status;
                Headline = GatherBuddyIpc.Waiting ? "Waiting for a timed node" : "Gathering";
                WatchGatherBuddy();
                break;
        }
    }

    private void TickCrafting()
    {
        Detail = Crafter.Status;
        if (Crafter.Finished)
        {
            if (Crafter.Failure is { } failure) Stop(failure, true, now: true);
            else if (pendingStop != null) Stop(pendingStop, pendingStopProblem, now: true);
            else Finish();
            return;
        }
        if (Crafter.IsPaused)
        {
            if (Phase == RunPhase.Stopping) { Stop(pendingStop ?? "Stopped.", pendingStopProblem, now: true); return; }
            if (Phase == RunPhase.Pausing)
            {
                Phase = RunPhase.Paused;
                Headline = "Paused";
                Detail = "Resume to carry on crafting.";
                Add("Paused.", false);
            }
        }
    }

    private void LearnGatheringRate()
    {
        var gathered = Targets.Where(t => t.Supply != Supply.Fish).Sum(t => t.Remaining);
        if (gatherActiveSeconds < 60 || gathered < 10 || Targets.Any(t => t.Supply == Supply.Fish)) return;
        var t = plugin.Config.Timings;
        t.GatheredPerMinute = Math.Clamp(Timings.Blend(t.GatheredPerMinute, gathered / (gatherActiveSeconds / 60)), 1, 120);
        plugin.Config.Save();
    }

    /// <summary>Estimated time until the run is done, or null when not running.</summary>
    public TimeSpan? TimeLeft()
    {
        if (!Active) return null;
        var plans = plugin.Plans;
        var crafting = TimeSpan.Zero;
        if (Stage == RunStage.Craft)
        {
            if (Crafter.Current is { } cur) crafting += plans.PerCraft(cur.Recipe, cur.Hq) * Math.Max(0, cur.Crafts - Crafter.DoneThisRecipe);
            foreach (var job in Crafter.Upcoming) crafting += plans.PerCraft(job.Recipe, job.Hq) * job.Crafts;
            return crafting;
        }
        var t = plugin.Config.Timings;
        var left = Targets.Where(x => x.Supply != Supply.Fish).Sum(Left);
        var fish = Targets.Where(x => x.Supply == Supply.Fish).Sum(Left);
        return TimeSpan.FromMinutes(left / Math.Max(0.1, t.GatheredPerMinute) + fish / Math.Max(0.05, t.FishPerMinute)) + plans.Estimate.Crafting;
    }

    /// <summary>GatherBuddy Reborn can switch itself off; turn it back on a few times before giving up.</summary>
    private void WatchGatherBuddy()
    {
        if (GatherBuddyIpc.Enabled)
        {
            gbrOffSince = DateTime.MinValue;
            return;
        }
        if (gbrOffSince == DateTime.MinValue) { gbrOffSince = DateTime.UtcNow; return; }
        if (DateTime.UtcNow - gbrOffSince < TimeSpan.FromSeconds(5)) return;

        var status = GatherBuddyIpc.Status;
        if (reEnables >= 3)
        {
            Stop("GatherBuddy Reborn keeps switching auto-gather off" + (status.Length > 0 ? ": " + status : "."), true);
            return;
        }
        reEnables++;
        gbrOffSince = DateTime.MinValue;
        Add("GatherBuddy Reborn switched auto-gather off" + (status.Length > 0 ? $" ({status})" : "") + "; switching it back on.", true);
        GatherBuddyIpc.SetEnabled(true);
    }

    /// <summary>Still to gather for one target right now.</summary>
    public static int Left(GatherTarget t) => GatherTargets.Left(t, GbrCount);

    /// <summary>What GatherBuddy Reborn counts: normal-quality items in the main bags.</summary>
    private static int GbrCount(uint itemId) => GameState.BagCount(itemId, false);

    private void Add(string text, bool problem)
    {
        log.Insert(0, new RunLogEntry(DateTime.Now, text, problem));
        if (log.Count > 80) log.RemoveAt(log.Count - 1);
    }

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

    public void Dispose()
    {
        if (!Active) return;
        GatherBuddyIpc.SetEnabled(false);
        lists.Uninstall();
    }
}
