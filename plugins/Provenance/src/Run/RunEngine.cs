using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.ImGuiNotification;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Core.Planning;
using Provenance.Core.Run;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

internal enum RunPhase
{
    Idle,
    Gathering,
    /// <summary>Pause asked for; waits for the current node to finish.</summary>
    Pausing,
    Paused,
    Finished,
    Stopped,
}

internal sealed record RunLogEntry(DateTime Time, string Text, bool Problem);

/// <summary>
/// Runs an order. This release covers the gathering stage: Provenance's list goes into GatherBuddy
/// Reborn, auto-gather runs until the plan needs nothing more to gather, and the run watches for
/// the stop rules (death, a nearly full inventory, a plugin going away, duties).
/// </summary>
internal sealed unsafe class RunEngine : IDisposable
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

    public RunEngine(Plugin plugin) => this.plugin = plugin;

    public RunPhase Phase { get; private set; } = RunPhase.Idle;
    public string Headline { get; private set; } = "Not running";
    public string Detail { get; private set; } = "";
    public DateTime? StartedAt { get; private set; }
    public DateTime? EndedAt { get; private set; }
    public IReadOnlyList<GatherTarget> Targets { get; private set; } = Array.Empty<GatherTarget>();
    public IReadOnlyList<RunLogEntry> Log => log;

    public bool Active => Phase is RunPhase.Gathering or RunPhase.Pausing or RunPhase.Paused;

    /// <summary>Why a run can't start now, or null when it can.</summary>
    public string? StartBlocker()
    {
        var plan = plugin.Plans.Plan;
        if (Active) return "A run is already going.";
        if (plugin.Plans.Catalog == null) return "Still reading game data.";
        if (plugin.Config.Order.IsEmpty) return "Add items to the order first.";
        if (!plan.CanStart) return "Get the missing items first; they are listed at the top of the plan.";
        if (!Services.ClientState.IsLoggedIn) return "Log in first.";
        if (!GatherBuddyIpc.Available) return "GatherBuddy Reborn isn't loaded.";
        if (!Vnav.Available) return "vnavmesh isn't loaded.";
        if (plan.Count(Supply.Fish) > 0 && !Ipc.IsLoaded("AutoHook")) return "AutoHook isn't loaded; this order has fish to catch.";
        if (Stock.FreeSlots() <= MinFreeSlots) return $"Your inventory has {MinFreeSlots} or fewer free slots.";
        if (GatherTargets.Build(plan, BagCount).Count == 0)
            return "Nothing to gather for this order. Buying, monster drops and crafting arrive in the next milestones.";
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
        gbrOffSince = DateTime.MinValue;
        StartedAt = DateTime.Now;
        EndedAt = null;

        var targets = GatherTargets.Build(plugin.Plans.Plan, BagCount);
        var unknown = lists.Install(targets);
        if (lists.Error != null)
        {
            Phase = RunPhase.Stopped;
            Headline = "Couldn't start";
            Detail = lists.Error;
            Add(lists.Error, true);
            Notify(lists.Error, true);
            return;
        }
        if (unknown.Count > 0)
        {
            var catalog = plugin.Plans.Catalog!;
            Add($"GatherBuddy Reborn doesn't list {string.Join(", ", unknown.Select(catalog.ItemName))} as gatherable; get them yourself.", true);
            targets = targets.Where(t => !unknown.Contains(t.ItemId)).ToList();
        }
        Targets = targets;

        GatherBuddyIpc.SetEnabled(true);
        Phase = RunPhase.Gathering;
        Headline = "Gathering";
        Add($"Started gathering {targets.Count} {(targets.Count == 1 ? "item" : "items")} with GatherBuddy Reborn.", false);
        Notify("Run started: gathering " + targets.Count + (targets.Count == 1 ? " item." : " items."), false);
    }

    /// <summary>Soft pause: finishes the node being gathered, then stops.</summary>
    public void Pause()
    {
        if (Phase != RunPhase.Gathering) return;
        Phase = RunPhase.Pausing;
        Headline = "Pausing after this node";
        Add("Pause asked for; finishing the current node first.", false);
    }

    public void Resume()
    {
        if (Phase is not (RunPhase.Paused or RunPhase.Pausing)) return;
        if (Phase == RunPhase.Paused) GatherBuddyIpc.SetEnabled(true);
        Phase = RunPhase.Gathering;
        Headline = "Gathering";
        gbrOffSince = DateTime.MinValue;
        Add("Resumed.", false);
    }

    public void Stop(string reason = "Stopped.", bool problem = false)
    {
        if (!Active) return;
        GatherBuddyIpc.SetEnabled(false);
        lists.Uninstall();
        Phase = RunPhase.Stopped;
        EndedAt = DateTime.Now;
        Headline = problem ? "Stopped" : "Stopped by you";
        Detail = reason;
        Add(reason, problem);
        Notify(reason, problem);
    }

    private void Finish()
    {
        GatherBuddyIpc.SetEnabled(false);
        lists.Uninstall();
        Phase = RunPhase.Finished;
        EndedAt = DateTime.Now;
        Headline = "Gathering done";
        Detail = "Everything to gather for this order is in your bags. Buying, monster drops and crafting arrive in the next milestones.";
        Add("Gathering done.", false);
        Notify("Gathering done for " + plugin.Config.Order.Name + ".", false);
    }

    /// <summary>Framework thread.</summary>
    public void Tick()
    {
        if (!Active || DateTime.UtcNow < nextCheck) return;
        nextCheck = DateTime.UtcNow + CheckEvery;

        if (!Services.ClientState.IsLoggedIn) { Stop("You logged out.", true); return; }
        if (!GatherBuddyIpc.Available) { Stop("GatherBuddy Reborn was unloaded.", true); return; }
        if (!Vnav.Available) { Stop("vnavmesh was unloaded.", true); return; }
        var player = Services.Objects.LocalPlayer;
        if (player == null) return; // zoning
        if (player.IsDead) { Stop("You were knocked out. Retrying after a knock-out comes with the full run.", true); return; }
        if (Stock.FreeSlots() <= MinFreeSlots) { Stop($"Only {MinFreeSlots} inventory slots left.", true); return; }

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
            if (Phase == RunPhase.Gathering) { GatherBuddyIpc.SetEnabled(true); Headline = "Gathering"; }
        }

        if (GatherTargets.AllDone(Targets, plugin.Plans.Plan)) { Finish(); return; }

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

            case RunPhase.Gathering:
                Detail = GatherBuddyIpc.Status;
                Headline = GatherBuddyIpc.Waiting ? "Waiting for a timed node" : "Gathering";
                WatchGatherBuddy();
                break;
        }
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

    /// <summary>What GatherBuddy Reborn counts: normal-quality items in the main bags.</summary>
    private static int BagCount(uint itemId)
    {
        var inv = InventoryManager.Instance();
        return inv == null ? 0 : inv->GetInventoryItemCount(itemId, false, false, false, 0);
    }

    private void Add(string text, bool problem)
    {
        log.Insert(0, new RunLogEntry(DateTime.Now, text, problem));
        if (log.Count > 60) log.RemoveAt(log.Count - 1);
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
