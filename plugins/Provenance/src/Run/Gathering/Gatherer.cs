using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
using Provenance.Core.Gathering;
using Provenance.Core.Planning;
using Provenance.Core.Run;
using Provenance.Core.Timing;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run.Gathering;

/// <summary>
/// Provenance's own gathering (replacing GatherBuddy Reborn's auto-gather): picks the next node for
/// what the order still needs, changes into the gatherer's gearset, travels there (teleport, aethernet,
/// mount, fly), finds the node among its known spawn spots, opens it and works the gathering window
/// with the rotation solver (or the collectable rotation), always to the end of the node. Between nodes
/// it extracts materia and reduces spare collectables. Fishing is not handled here.
/// The approach follows GatherBuddy Reborn's AutoGather (Apache-2.0). Framework thread.
/// </summary>
internal sealed unsafe class Gatherer
{
    private enum Step { Idle, Paused, Choose, Gear, Buffs, Travel, Interact, Upkeep }

    private const uint LandOrDismount = 23;
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly Travel travel = new();
    private readonly Upkeep upkeep;
    private List<GatherTarget> targets = new();
    private Func<GatherTarget, int> left = _ => 0;
    private Func<IReadOnlySet<uint>> neededCollectables = () => new HashSet<uint>();

    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime stepSince;
    private GatherTarget? current;
    private GatherPoint? node;
    /// <summary>All GatheringPoint rows of the chosen node (base): the node spawns at one of them at a time.</summary>
    private HashSet<uint> nodePoints = new();
    private readonly HashSet<(uint Point, int X, int Z)> visited = new();
    private Vector3 spot;
    private int interactTries;
    private bool wasOnNode;
    private Vector3 lastNodePosition;

    // Inside a node
    private IEnumerator<GatherAction>? plan;
    private Task<IReadOnlyList<GatherAction>>? solving;
    private int lastIntegrity;
    private bool luckUsed;
    private bool collectableFull;

    public Gatherer(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
        upkeep = new Upkeep(plugin, log);
    }

    public string Status { get; private set; } = "";
    /// <summary>Why the gatherer gave up (a missing gearset, nothing reachable); the run stops on it.</summary>
    public string? Failure { get; private set; }
    /// <summary>Nothing can be gathered right now (only closed timed nodes, or nothing reachable).</summary>
    public bool Waiting { get; private set; }
    public bool Running => step is not (Step.Idle or Step.Paused);
    public bool OnNode => Services.Condition[ConditionFlag.Gathering];
    /// <summary>Items the gatherer can't get anywhere (no known nodes, level, folklore, zone).</summary>
    public IReadOnlyCollection<uint> Unreachable => unreachable;
    private readonly HashSet<uint> unreachable = new();

    private GameCatalog Catalog => plugin.Plans.Catalog!;

    public void Begin(IEnumerable<GatherTarget> gatherTargets, Func<GatherTarget, int> leftOf, Func<IReadOnlySet<uint>> collectablesNeeded)
    {
        targets = gatherTargets.Where(t => t.Supply != Supply.Fish).ToList();
        left = leftOf;
        neededCollectables = collectablesNeeded;
        visited.Clear();
        unreachable.Clear();
        Waiting = false;
        Failure = null;
        Go(Step.Choose);
    }

    public void Pause()
    {
        if (step == Step.Idle) return;
        travel.Cancel();
        Vnav.Stop();
        step = Step.Paused;
        Status = "Paused";
    }

    public void Resume()
    {
        if (step == Step.Paused) Go(Step.Choose);
    }

    public void Stop()
    {
        travel.Cancel();
        Vnav.Stop();
        step = Step.Idle;
        Status = "";
    }

    public void Tick()
    {
        if (step is Step.Idle or Step.Paused || DateTime.UtcNow < waitUntil) return;
        var me = Services.Objects.LocalPlayer;
        if (me == null || me.IsDead || Services.Condition[ConditionFlag.BetweenAreas]) return;

        LearnVisibleNodes();
        if (OnNode)
        {
            wasOnNode = true;
            WorkNode();
            return;
        }
        if (wasOnNode)
        {
            // The node closed: it respawns elsewhere in its group, so mark this spot and pick again.
            wasOnNode = false;
            plan = null;
            solving = null;
            luckUsed = false;
            collectableFull = false;
            if (node != null) visited.Add((node.PointId, (int)lastNodePosition.X, (int)lastNodePosition.Z));
            Go(Step.Choose, 0.8);
            return;
        }

        switch (step)
        {
            case Step.Upkeep:
                upkeep.Tick();
                Status = upkeep.Status;
                if (!upkeep.Busy) Go(Step.Choose);
                return;
            case Step.Choose: Choose(); return;
            case Step.Gear: Gear(); return;
            case Step.Buffs: Buffs(); return;
            case Step.Travel: TravelTick(me); return;
            case Step.Interact: InteractTick(me); return;
        }
    }

    // ------------------------------------------------------------------ choosing a node

    private void Choose()
    {
        if (upkeep.Begin(neededCollectables())) { Go(Step.Upkeep); return; }
        var now = DateTimeOffset.UtcNow;
        var here = Services.ClientState.TerritoryType;
        var unlocked = Teleporter.Unlocked();
        var reach = new Dictionary<uint, bool>();
        bool CanReach(uint t) => reach.TryGetValue(t, out var r) ? r : reach[t] = Teleporter.CanReach(t);

        var best = default((GatherTarget Target, GatherPoint Point, int Score)?);
        foreach (var t in targets)
        {
            if (left(t) <= 0) continue;
            var any = false;
            foreach (var p in Catalog.GatherPoints(t.ItemId))
            {
                if (PlanService.JobLevel(p.ClassJobId) < p.Level) continue;
                if (p.Tome != 0 && !ItemUnlocks.IsLearned(p.Tome)) continue;
                if (BasePositions(t.ItemId, p.BaseId).Count == 0 || !CanReach(p.TerritoryId)) continue;
                any = true;
                if (p.Timed && !Eorzea.IsUp(p.Mask, now)) continue;
                // Timed nodes first (short windows), then this zone, then the cheapest teleport.
                var cost = p.TerritoryId == here ? 0 : unlocked.Where(d => d.TerritoryId == p.TerritoryId).Select(d => (int)d.GilCost).DefaultIfEmpty(5000).Min();
                var score = (p.Timed ? 0 : 1_000_000) + cost;
                if (best is not { } b || score < b.Score) best = (t, p, score);
            }
            if (!any && unreachable.Add(t.ItemId))
                log($"No node you can reach and gather {Catalog.ItemName(t.ItemId)} at (level, folklore tome, zone, or no known position).", true);
        }

        // Every item still needed has no node the character can get to: nothing to wait for.
        var stillNeeded = targets.Where(t => left(t) > 0).ToList();
        if (stillNeeded.Count > 0 && stillNeeded.All(t => unreachable.Contains(t.ItemId)))
        {
            Fail($"Can't gather {string.Join(", ", stillNeeded.Select(t => Catalog.ItemName(t.ItemId)))}: no node you can reach (level, folklore tome, zone, or no known position). Get them yourself.");
            return;
        }
        if (best is not { } pick)
        {
            Waiting = true;
            Status = "No node is up right now";
            Wait(3);
            return;
        }
        Waiting = false;
        current = pick.Target;
        node = pick.Point;
        nodePoints = Catalog.GatherPoints(pick.Target.ItemId).Where(p => p.BaseId == pick.Point.BaseId).Select(p => p.PointId).ToHashSet();
        Go(Step.Gear);
    }

    /// <summary>Known spawn spots of a node (every GatheringPoint of the base), each with where to stand.</summary>
    private List<(uint Point, Vector3 At)> BasePositions(uint itemId, uint baseId)
        => Catalog.GatherPoints(itemId).Where(p => p.BaseId == baseId)
            .SelectMany(p => Catalog.Gather.Positions(p.PointId).Select(pos => (p.PointId, pos))).ToList();

    // ------------------------------------------------------------------ getting ready

    private void Gear()
    {
        var job = node!.ClassJobId;
        Status = $"Changing to {ClassJobs.Abbreviation(job)}";
        if (GameState.CurrentJob == job) { Go(Step.Buffs); return; }
        if (DateTime.UtcNow - stepSince > TimeSpan.FromSeconds(10)) { Fail($"Couldn't change to {ClassJobs.Abbreviation(job)}."); return; }
        var set = GameState.BestGearset(job);
        if (set < 0) { Fail($"No gearset saved for {ClassJobs.Abbreviation(job)}."); return; }
        if (!Services.Condition[ConditionFlag.InCombat]) GameState.EquipGearset(set);
        Wait(1.5);
    }

    /// <summary>Prospect/Triangulate (shows nodes on the minimap), Stealth, and Truth for timed nodes; on foot only.</summary>
    private void Buffs()
    {
        var job = node!.ClassJobId;
        if (!Services.Condition[ConditionFlag.Mounted] && !Services.Condition[ConditionFlag.InCombat])
        {
            foreach (var (action, status) in new[] { GatherActions.Prospect(job), GatherActions.Sneak(job) }
                         .Concat(node.Timed ? new[] { GatherActions.Truth(job) } : Array.Empty<(uint, uint)>()))
            {
                if (GatherActions.HasStatus(status)) continue;
                if (GatherActions.Use(action)) { Wait(1.2); return; }
            }
        }
        NextSpot();
    }

    /// <summary>Travels to the nearest unvisited spawn spot of the node; once the node is in view, to the node itself.</summary>
    private void NextSpot()
    {
        var me = Services.Objects.LocalPlayer!.Position;
        var spots = BasePositions(current!.Value.ItemId, node!.BaseId);
        var open = spots.Where(s => !visited.Contains((s.Point, (int)s.At.X, (int)s.At.Z))).ToList();
        if (open.Count == 0)
        {
            // Every spot seen: the group respawned. Start over.
            foreach (var s in spots) visited.Remove((s.Point, (int)s.At.X, (int)s.At.Z));
            open = spots;
        }
        var inZone = Services.ClientState.TerritoryType == node.TerritoryId;
        var target = open.OrderBy(s => inZone ? Vector3.Distance(s.At, me) : 0).First();
        spot = target.At;
        var stand = Catalog.Gather.StandFor(spot);
        travel.GoTo($"the {Catalog.ItemName(current.Value.ItemId)} node", node.TerritoryId, new Vector2(stand.X, stand.Z), 2.8f,
            () => LiveNode()?.Position is { } p ? Catalog.Gather.StandFor(p) : null);
        Go(Step.Travel);
    }

    private IGameObject? LiveNode() => Interact.Nearest(ObjectKind.GatheringPoint, id => nodePoints.Contains(id));

    private void TravelTick(IGameObject me)
    {
        travel.Tick();
        Status = travel.Status;
        if (travel.Current == Travel.State.Failed)
        {
            log($"Couldn't get to the {Catalog.ItemName(current!.Value.ItemId)} node: {travel.Failure}", true);
            visited.Add((node!.PointId, (int)spot.X, (int)spot.Z));
            Go(Step.Choose, 2);
            return;
        }
        if (travel.Current != Travel.State.Arrived) return;
        if (LiveNode() is { } live && Vector3.Distance(live.Position, me.Position) < 6)
        {
            interactTries = 0;
            Go(Step.Interact);
            return;
        }
        // Nothing at this spot right now: try the next one.
        visited.Add((node!.PointId, (int)spot.X, (int)spot.Z));
        foreach (var p in nodePoints) visited.Add((p, (int)spot.X, (int)spot.Z));
        NextSpot();
    }

    private void InteractTick(IGameObject me)
    {
        var live = LiveNode();
        if (live == null || Vector3.Distance(live.Position, me.Position) > 6) { Go(Step.Choose); return; }
        var c = Services.Condition;
        if (c[ConditionFlag.InFlight] || c[ConditionFlag.Mounted])
        {
            Status = "Landing";
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, LandOrDismount);
            Wait(1);
            return;
        }
        if (++interactTries > 4) { visited.Add((node!.PointId, (int)live.Position.X, (int)live.Position.Z)); Go(Step.Choose); return; }
        Status = "Opening the node";
        lastNodePosition = live.Position;
        Interact.With(live);
        Wait(1.2);
    }

    // ------------------------------------------------------------------ inside a node

    private void WorkNode()
    {
        var c = Services.Condition;
        if (c[ConditionFlag.ExecutingGatheringAction] || c[ConditionFlag.Casting]) { Wait(0.1); return; }
        if (CollectableWindow.Open() is { } masterpiece) { WorkCollectable(masterpiece); return; }
        if (GatheringWindow.Open() is not { } window) { Wait(0.1); return; }

        if (lastIntegrity > 0 && window.Integrity > lastIntegrity + 1) plan = null;
        lastIntegrity = window.Integrity;
        var job = GameState.CurrentJob;
        var slots = window.Slots();
        var (useSkills, slot) = PickSlot(slots);
        if (slot == null)
        {
            // Nothing worth taking; leave the node.
            Interact.Close("Gathering");
            Wait(1);
            return;
        }
        Status = $"Gathering {Catalog.ItemName(slot.ItemId)}";

        // Luck of the Pioneer reveals hidden items when the wanted one is hidden.
        var wantHidden = current is { } cur && Catalog.GatherPoints(cur.ItemId).Any(p => p.Hidden) && slots.All(s => s.ItemId != cur.ItemId);
        if (wantHidden && !luckUsed && !window.AnyUnhidden() && GatherActions.Learned(GatherAction.Luck, GameState.JobLevel(job)))
        {
            luckUsed = true;
            if (GatherActions.Use(GatherAction.Luck, job)) { Wait(0.6); return; }
        }

        if (!useSkills || slot.Collectable) { window.Gather(slot.Index); Wait(0.5); return; }

        // Wise to the World after Solid Reason's proc: integrity back for free; plan again.
        if (window.Integrity < window.MaxIntegrity && GatherActions.HasStatus(GatherActions.EurekaMoment)
            && GatherActions.Learned(GatherAction.Wise, GameState.JobLevel(job)))
        {
            plan = null;
            if (GatherActions.Use(GatherAction.Wise, job)) { Wait(0.6); return; }
        }

        if (plan == null)
        {
            solving ??= GatherSolver.SolveAsync(SolverInput(slot, window, node?.Timed ?? false));
            if (!solving.IsCompleted) { Status = "Working out the best actions"; Wait(0.05); return; }
            plan = solving.IsCompletedSuccessfully ? solving.Result.GetEnumerator() : Enumerable.Empty<GatherAction>().GetEnumerator();
            solving = null;
        }
        if (!plan.MoveNext())
        {
            // Plan used up (integrity left over, e.g. after a proc): gather, and plan again next time.
            plan = null;
            window.Gather(slot.Index);
            Wait(0.5);
            return;
        }
        if (plan.Current == GatherAction.Gather)
        {
            window.Gather(slot.Index);
            Wait(0.5);
            return;
        }
        if (!GatherActions.Use(plan.Current, job)) window.Gather(slot.Index);
        Wait(0.6);
    }

    /// <summary>
    /// The slot to take: the current item while more is needed, else another wanted item in this node;
    /// with nothing needed any more the node is still gathered to the end (the wanted item, crystals,
    /// anything else). UseSkills is false for leftovers.
    /// </summary>
    private (bool UseSkills, GatherSlot? Slot) PickSlot(List<GatherSlot> slots)
    {
        bool Wanted(GatherSlot s) => targets.Any(t => t.ItemId == s.ItemId && left(t) > 0) && GameState.BagCount(s.ItemId) < 9999 - Math.Max(1, s.Yield);
        if (current is { } cur && slots.FirstOrDefault(s => s.ItemId == cur.ItemId && Wanted(s)) is { } mine) return (true, mine);
        if (slots.FirstOrDefault(Wanted) is { } other) return (true, other);
        if (current is { } c2 && slots.FirstOrDefault(s => s.ItemId == c2.ItemId) is { } same) return (false, same);
        if (slots.FirstOrDefault(s => Items.IsCrystal(s.ItemId)) is { } crystal) return (false, crystal);
        return (false, slots.FirstOrDefault(s => !s.Collectable));
    }

    private GatherSolverInput SolverInput(GatherSlot slot, GatheringWindow window, bool timedNode)
    {
        var p = Services.Objects.LocalPlayer!;
        var job = GameState.CurrentJob;
        var level = GameState.JobLevel(job);
        var stats = GameState.LiveStats(Discipline.Land);
        var glvl = (int)(Catalog.GatherPoints(slot.ItemId).FirstOrDefault()?.GatheringItemLevel ?? 0);
        var (baseGathering, basePerception) = Catalog.Gather.BaseStats(glvl);
        var bountiful = GatherActions.BountifulBonus(stats[0], baseGathering);
        var active = new HashSet<GatherAction>();
        foreach (var a in new[] { GatherAction.TwelvesBounty, GatherAction.Bountiful, GatherAction.Yield1, GatherAction.Yield2, GatherAction.GivingLand,
                     GatherAction.Gift1, GatherAction.Gift2, GatherAction.Tidings })
            if (GatherActions.HasStatus(GatherActions.Status(a))) active.Add(a);
        var bountifulOn = GatherActions.HasStatus(GatherActions.BountifulIIStatus) || active.Contains(GatherAction.Bountiful);
        if (bountifulOn) active.Add(GatherAction.Bountiful);
        return new GatherSolverInput
        {
            PlayerLevel = level,
            MaxGp = (int)p.MaxGp,
            Gp = (int)p.CurrentGp,
            MaxIntegrity = window.MaxIntegrity,
            Integrity = window.Integrity,
            Yield = slot.Yield - (bountifulOn ? bountiful : 0) + (slot.GivingLand ? 20 : 0) - (slot.Bonus ? 1 : 0),
            BoonChance = Math.Max(0, slot.BoonChance),
            BaseBoonChance = slot.BoonChance > 0 ? GatherActions.BaseBoonChance(stats[1], basePerception) : 0,
            BoonYield = active.Contains(GatherAction.Tidings) ? 2 : 1,
            BountifulYield = bountiful,
            IsCrystal = Items.IsCrystal(slot.ItemId),
            IsRare = slot.Rare,
            RegularNode = !timedNode,
            GpRegenPerHit = level >= 80 ? 6 : 5,
            GpRegenPerTick = level >= 83 ? 8 : level >= 80 ? 7 : 6,
            GivingLandCooldown = GatherActions.GivingLandCooldown(job),
            Learned = GatherActions.LearnedForSolver(level),
            Active = active,
        };
    }

    private void WorkCollectable(CollectableWindow w)
    {
        var p = Services.Objects.LocalPlayer!;
        var job = GameState.CurrentJob;
        var level = GameState.JobLevel(job);
        if (w.Integrity <= 0) { Wait(0.2); return; }
        if (!collectableFull) collectableFull = p.CurrentGp >= CollectableRotation.FullRotationGp;
        var itemsLeft = current is { } cur ? left(cur) : 1;
        var state = new CollectableState(w.Collectability, w.Integrity, w.MaxIntegrity, w.ScourGain, w.MeticulousGain, w.BrazenGainMax,
            w.Low, w.Mid, w.High, (int)p.CurrentGp, level,
            GatherActions.HasStatus(GatherActions.EurekaMoment), GatherActions.HasStatus(GatherActions.Status(GatherAction.Scrutiny)),
            GatherActions.HasStatus(GatherActions.CollectorsHighStandard), GatherActions.HasStatus(GatherActions.CollectorsStandard), itemsLeft);
        var acts = new CollectableActions(
            GatherActions.Level(GatherAction.Scour), GatherActions.Cost(GatherAction.Scour),
            GatherActions.Level(GatherAction.Brazen), GatherActions.Cost(GatherAction.Brazen),
            GatherActions.Level(GatherAction.Meticulous), GatherActions.Cost(GatherAction.Meticulous),
            GatherActions.Level(GatherAction.Scrutiny), GatherActions.Cost(GatherAction.Scrutiny),
            GatherActions.Level(GatherAction.SolidAge), GatherActions.Cost(GatherAction.SolidAge),
            GatherActions.Level(GatherAction.Wise));
        var next = CollectableRotation.Next(state, acts, collectableFull);
        Status = $"Collectable {Catalog.ItemName(w.ItemId)}: {w.Collectability}";
        if (!GatherActions.Use(next, job)) GatherActions.Use(GatherAction.Collect, job);
        Wait(0.6);
    }

    // ------------------------------------------------------------------ learning positions

    private DateTime nextLearn;

    /// <summary>Remembers where gathering points were seen (kept on this PC only, never uploaded).</summary>
    private void LearnVisibleNodes()
    {
        if (DateTime.UtcNow < nextLearn) return;
        nextLearn = DateTime.UtcNow.AddSeconds(3);
        var data = Catalog.Gather;
        var any = false;
        foreach (var o in Services.Objects.Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable))
            any |= data.Learn(o.BaseId, o.Position);
        if (any) data.SaveLearned();
    }

    private void Fail(string message)
    {
        Failure = message;
        Status = message;
        travel.Cancel();
        Vnav.Stop();
        step = Step.Idle;
    }

    private void Go(Step next, double wait = 0)
    {
        step = next;
        stepSince = DateTime.UtcNow;
        Wait(wait);
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
