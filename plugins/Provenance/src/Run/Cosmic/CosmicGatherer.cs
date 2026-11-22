using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Core.Cosmic;
using Provenance.Integrations;
using Provenance.Core.Fishing;
using Provenance.Integrations.Fishing;
using Provenance.Run.Gathering;
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Provenance.Run.Cosmic;

/// <summary>
/// The gatherer's half of a two-class mission: gather a raw material from the nodes the mission puts up around its
/// map marker (miner, botanist), or fish it at the hole's recorded stands with the mission's bait (fisher, through the
/// order run's angler: the two never run together). It ends once the Cosmopouch holds the amount asked for.
/// Ticked on the framework thread.
/// </summary>
internal sealed unsafe class CosmicGatherer
{
    private enum Step { Idle, ToArea, Prospect, FindNode, ToNode, Open, Gathering, ToStand, Land, Fishing, Settle, Done, Failed }

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly Travel travel = new();
    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime deadline;
    private uint item;
    private int wanted;
    private CosmicMission mission = null!;
    private IGameObject? node;
    private readonly HashSet<ulong> visited = new();
    private int searches;
    private int stand;
    private Angler? angler;
    /// <summary>Stopped while fishing: the rod still has to come in (see <see cref="WindDown"/>).</summary>
    private bool windingDown;
    private DateTime windDownUntil;

    public CosmicGatherer(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Finished => step is Step.Done or Step.Failed;

    /// <summary>Gathers (or fishes) until the Cosmopouch holds <paramref name="target"/> of <paramref name="itemId"/>.</summary>
    public void Begin(CosmicMission m, uint itemId, int target)
    {
        mission = m;
        item = itemId;
        wanted = target;
        Failure = null;
        windingDown = false;
        visited.Clear();
        searches = 0;
        stand = 0;
        if (Have >= wanted) { step = Step.Done; return; }
        if (m.ClassJob == 18) { StartFishing(); return; }
        if (m.Marker is not { } marker) { Fail("The mission's map marker wasn't found."); return; }
        travel.GoTo("the mission area", m.Planet.Territory(), new Vector2(marker.X, marker.Z), MathF.Max(15, marker.Radius * 0.5f), null);
        step = Step.ToArea;
        Status = "Going to the mission area";
    }

    public void Cancel()
    {
        travel.Cancel();
        if (angler is { Active: true })
        {
            angler.RequestStop();
            windingDown = true;
            windDownUntil = DateTime.UtcNow.AddSeconds(30);
        }
        if (!Finished) step = Step.Idle;
    }

    /// <summary>After a stop mid-fishing: keeps the angler going until the line is in, then stows the rod.</summary>
    public void WindDown()
    {
        if (!windingDown || angler == null) return;
        if (angler.Active && DateTime.UtcNow < windDownUntil) { angler.Tick(); return; }
        if (angler.Active) angler.Stop();
        if (Services.Condition[ConditionFlag.Fishing]) RodGame.Use(FishingIds.Quit);
        windingDown = false;
    }

    /// <summary>What's gathered so far: in the bags or the Cosmopouch.</summary>
    private int Have => GameState.BagCount(item);

    public void Tick()
    {
        if (Finished || step == Step.Idle || DateTime.UtcNow < waitUntil) return;
        switch (step)
        {
            case Step.ToArea:
            case Step.ToNode:
            case Step.ToStand:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed) { Fail(travel.Failure ?? "Couldn't get there."); return; }
                if (travel.Current != Travel.State.Arrived) return;
                step = step switch { Step.ToArea => Step.Prospect, Step.ToNode => Step.Open, _ => Step.Land };
                deadline = DateTime.UtcNow.AddSeconds(8);
                return;
            case Step.Land: Land(); return;
            case Step.Settle: Settle(); return;
            case Step.Prospect: Prospect(); return;
            case Step.FindNode: FindNode(); return;
            case Step.Open: Open(); return;
            case Step.Gathering: Gathering(); return;
            case Step.Fishing: Fishing(); return;
        }
    }

    // ------------------------------------------------------------------ gathering (miner, botanist)

    /// <summary>The mission warns when Prospect or Triangulate is off: the nodes only show with it.</summary>
    private void Prospect()
    {
        var job = GameState.CurrentJob;
        var (action, status) = GatherActions.Prospect(job);
        if (!GatherActions.HasStatus(status) && GameState.CanUse(ActionType.Action, action)) GatherActions.Use(action);
        step = Step.FindNode;
        Wait(1);
    }

    private void FindNode()
    {
        if (Have >= wanted) { Done(); return; }
        var marker = mission.Marker!;
        var center = new Vector2(marker.X, marker.Z);
        node = Services.Objects
            .Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable && !visited.Contains(o.GameObjectId)
                        && Vector2.Distance(new Vector2(o.Position.X, o.Position.Z), center) <= marker.Radius + 40)
            .OrderBy(o => Interact.Distance(o))
            .FirstOrDefault();
        if (node == null)
        {
            // None in view: try another part of the area (the nodes show within about a hundred yalms).
            if (++searches > 6) { Fail("No gathering points showed up in the mission area."); return; }
            visited.Clear();
            var angle = searches * 2.1f;
            var to = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * marker.Radius * 0.6f;
            travel.GoTo("the mission area", mission.Planet.Territory(), to, 15, null);
            step = Step.ToArea;
            Status = "Looking for the mission's gathering points";
            return;
        }
        searches = 0;
        var target = node;
        travel.GoTo("a gathering point", mission.Planet.Territory(), new Vector2(target.Position.X, target.Position.Z), 2.5f, () => target.Position);
        step = Step.ToNode;
        Status = "Going to a gathering point";
    }

    private void Open()
    {
        if (GatheringWindow.Open() != null) { step = Step.Gathering; Wait(0.4); return; }
        if (Services.Condition[ConditionFlag.Mounted])
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
            Wait(1.2);
            return;
        }
        if (DateTime.UtcNow > deadline || node == null || !node.IsTargetable)
        {
            if (node != null) visited.Add(node.GameObjectId);
            step = Step.FindNode;
            return;
        }
        Interact.With(node);
        Status = "Opening the gathering point";
        Wait(1);
    }

    private void Gathering()
    {
        var window = GatheringWindow.Open();
        if (window == null)
        {
            // The point is spent (or closed): the next one, unless that's enough.
            if (Services.Condition[ConditionFlag.Gathering]) { Wait(0.3); return; }
            if (node != null) visited.Add(node.GameObjectId);
            step = Step.FindNode;
            Wait(0.5);
            return;
        }
        if (Have >= wanted) { Interact.Close("Gathering"); Done(); return; }
        if (Services.Condition[ConditionFlag.ExecutingGatheringAction]) { Wait(0.2); return; }
        var slot = window.Slots().FirstOrDefault(s => s.ItemId == item);
        if (slot == null)
        {
            // Not this point's item: leave it.
            if (node != null) visited.Add(node.GameObjectId);
            Interact.Close("Gathering");
            step = Step.FindNode;
            Wait(0.8);
            return;
        }
        Status = $"Gathering ({Have} of {wanted})";
        window.Gather(slot.Index);
        Wait(1.2);
    }

    // ------------------------------------------------------------------ fishing (fisher)

    private void StartFishing()
    {
        var gather = plugin.Plans.Catalog?.Gather;
        var fish = gather?.Fish(item);
        var spot = fish?.Spots.Select(id => gather!.Spot(id)).FirstOrDefault(s => s is { Stands.Count: > 0 });
        if (fish == null || spot == null) { Fail("No recorded place to stand is known for this fish."); return; }
        var s = spot.Stands[stand % spot.Stands.Count];
        travel.GoTo("the fishing hole", spot.TerritoryId, new Vector2(s.X, s.Z), 1.2f, () => new Vector3(s.X, s.Y, s.Z));
        step = Step.ToStand;
        Status = "Going to the fishing hole";
    }

    /// <summary>Off the mount and facing the water before the first cast; another stand when the line can't be cast here.</summary>
    private void Land()
    {
        if (Services.Condition[ConditionFlag.Mounted])
        {
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
            Status = "Getting off the mount";
            Wait(1.2);
            return;
        }
        var gather = plugin.Plans.Catalog!.Gather;
        var fish = gather.Fish(item)!;
        var spot = fish.Spots.Select(gather.Spot).First(s => s is { Stands.Count: > 0 })!;
        var s = spot.Stands[stand % spot.Stands.Count];
        if (Services.Objects.LocalPlayer is { } me) ((GameObjectStruct*)me.Address)->SetRotation(s.Facing);
        if (!Angler.CanCastHere())
        {
            if (DateTime.UtcNow < deadline) { Wait(0.3); return; }
            if (++stand > 6) { Fail("The line couldn't be cast at any of the hole's stands."); return; }
            StartFishing();
            return;
        }
        step = Step.Fishing;
        BeginAngling();
    }

    private void BeginAngling()
    {
        var gather = plugin.Plans.Catalog!.Gather;
        var fish = gather.Fish(item)!;
        var spot = fish.Spots.Select(gather.Spot).First(s => s is { Stands.Count: > 0 })!;
        var s = spot.Stands[stand % spot.Stands.Count];
        if (Services.Objects.LocalPlayer is { } me) ((GameObjectStruct*)me.Address)->SetRotation(s.Facing);
        angler = plugin.Runs.Fish.Angler;
        var bait = mission.Bait != 0 ? mission.Bait : fish.Bait;
        angler.Begin(new AnglerJob(fish, spot, gather.WeatherTable(spot.TerritoryId), bait, gather.Fish, () => wanted - Have,
            plugin.Config.Fishing.UseBuffs, false) { SpotFish = gather.FishAt(spot.Id), FallbackBait = 0 });
        Status = "Fishing";
    }

    private void Fishing()
    {
        if (angler == null) { Fail("Fishing didn't start."); return; }
        angler.Tick();
        Status = $"Fishing ({Have} of {wanted})";
        if (angler.Active) return;
        if (Have >= wanted) { Done(); return; }
        switch (angler.Signal)
        {
            case AnglerSignal.Wary:
            case AnglerSignal.Amiss:
            case AnglerSignal.CantCast:
                // Another stand of the hole.
                if (++stand > 6) { Fail("Couldn't keep fishing at this hole."); return; }
                StartFishing();
                return;
            case AnglerSignal.NoBait:
                Fail("The mission's bait ran out.");
                return;
            default:
                Fail(angler.Failure ?? "Fishing stopped.");
                return;
        }
    }

    private void Done()
    {
        Status = "Putting the tools away";
        step = Step.Settle;
        deadline = DateTime.UtcNow.AddSeconds(12);
        Wait(0.5);
    }

    private void Settle()
    {
        var c = Services.Condition;
        if (GatheringWindow.Open() != null) Interact.Close("Gathering");
        if (angler is { Active: true }) { angler.Tick(); Wait(0.2); return; }
        if (c[ConditionFlag.Fishing] && DateTime.UtcNow < deadline) { RodGame.Use(FishingIds.Quit); Wait(1); return; }
        if ((c[ConditionFlag.Gathering] || c[ConditionFlag.ExecutingGatheringAction] || c[ConditionFlag.Fishing]) && DateTime.UtcNow < deadline) { Wait(0.3); return; }
        Status = "Gathered";
        step = Step.Done;
        Wait(1.5);
    }

    private void Fail(string why)
    {
        travel.Cancel();
        Failure = why;
        step = Step.Failed;
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
