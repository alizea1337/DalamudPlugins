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
using Provenance.Core.Estimating;
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
    private enum Step { Idle, Paused, Choose, Gear, Buffs, Travel, Approach, Interact, Upkeep }

    private const uint LandOrDismount = 23;
    /// <summary>A node this close counts as the one at this spot; the last yalms are walked.</summary>
    private const float NodeHere = 15f;
    /// <summary>Walks on foot to the node before opening it from wherever the walks ended.</summary>
    private const int ApproachWalks = 2;
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
    /// <summary>Node groups (bases) given up on for a while: no landing next to the node, or it won't open.</summary>
    private readonly Dictionary<uint, DateTime> givenUp = new();
    private static readonly TimeSpan GiveUpFor = TimeSpan.FromMinutes(10);
    /// <summary>Give-ups in a row per node group; at <see cref="MaxGiveUps"/> the group is dropped for the run.</summary>
    private readonly Dictionary<uint, int> giveUps = new();
    private const int MaxGiveUps = 3;
    /// <summary>Node groups dropped for the rest of the run: they couldn't be landed at or opened again and again.</summary>
    private readonly HashSet<uint> abandoned = new();
    private Vector3 spot;
    private int interactTries;
    private int approachWalks;
    private DateTime walkUntil;
    /// <summary>Opening from the mount failed: land before trying again.</summary>
    private bool mustLand;
    /// <summary>The landing is only to drink a cordial (opening from the mount would work).</summary>
    private bool landingForCordial;
    /// <summary>Landing for a cordial failed at this node: wait for the GP instead.</summary>
    private bool noCordialThisVisit;
    private int landPresses, landMoves;
    private bool wasOnNode;
    private Vector3 lastNodePosition;
    /// <summary>The node object last opened (its game object id).</summary>
    private ulong lastNodeObject;
    /// <summary>
    /// Node objects to leave alone until a moment: the one just gathered (it stays targetable for a moment while it
    /// despawns, and opening it again fails), and one that wouldn't open.
    /// </summary>
    private readonly Dictionary<ulong, DateTime> skipNodes = new();
    private static readonly TimeSpan JustGathered = TimeSpan.FromSeconds(20);

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

    /// <summary>
    /// Collectability to aim for when the gathering window shows no tiers for an item (0: none). Custom deliveries set
    /// it; the order run never does.
    /// </summary>
    public Func<uint, int>? FallbackCollectability { get; set; }
    /// <summary>Why the gatherer gave up (a missing gearset, nothing reachable); the run stops on it.</summary>
    public string? Failure { get; private set; }
    /// <summary>Nothing can be gathered right now (only closed timed nodes, or nothing reachable).</summary>
    public bool Waiting { get; private set; }
    /// <summary>Waiting only because the nodes left were given up on for a while (not a timed window).</summary>
    public bool WaitingToRetry { get; private set; }
    public bool Running => step is not (Step.Idle or Step.Paused);
    public bool OnNode => Services.Condition[ConditionFlag.Gathering];
    /// <summary>Items the gatherer can't get anywhere (no known nodes, level, folklore, zone).</summary>
    public IReadOnlyCollection<uint> Unreachable => unreachable;
    private readonly HashSet<uint> unreachable = new();

    private GameCatalog Catalog => plugin.Plans.Catalog!;
    private Func<IReadOnlySet<uint>> reduceNow = () => new HashSet<uint>();

    /// <summary>Why the collectables gathered for reduction can't be reduced now (see <see cref="Upkeep.ReduceBlocked"/>).</summary>
    public string? ReduceBlocked => upkeep.ReduceBlocked;

    /// <summary>What the gatherer is doing and knows, line by line, for a bug report.</summary>
    public IEnumerable<string> Describe()
    {
        var now = DateTime.UtcNow;
        yield return $"step {step}, status \"{Status}\", waiting {Waiting}, waiting to retry {WaitingToRetry}, on a node {OnNode}";
        if (current is { } t) yield return $"target: {Catalog.ItemName(t.ItemId)} ({t.ItemId}), {t.Supply}, {left(t)} left";
        if (node is { } n)
        {
            yield return $"node group (base) {n.BaseId}, territory {n.TerritoryId}, {(n.Timed ? "timed" : "regular")}, level {n.Level}, points {string.Join(",", nodePoints)}";
            if (current is { } c)
                foreach (var s in BasePositions(c.ItemId, n.BaseId))
                    yield return $"  spawn spot: point {s.Point} at {s.At.X:0.0}, {s.At.Y:0.0}, {s.At.Z:0.0}{(visited.Contains((s.Point, (int)s.At.X, (int)s.At.Z)) ? " (visited)" : "")}";
            yield return $"spot travelled to: {spot.X:0.0}, {spot.Y:0.0}, {spot.Z:0.0}";
        }
        yield return "targets: " + string.Join("; ", targets.Select(x => $"{Catalog.ItemName(x.ItemId)} ({x.ItemId}) {x.Supply}, {left(x)} left"));
        foreach (var (group, until) in givenUp.Where(kv => kv.Value > now)) yield return $"group {group} given up until {until.ToLocalTime():HH:mm:ss}";
        if (abandoned.Count > 0) yield return "groups dropped for the run: " + string.Join(", ", abandoned);
        foreach (var (id, until) in skipNodes.Where(kv => kv.Value > now)) yield return $"node object {id:X} left alone until {until.ToLocalTime():HH:mm:ss}";
        if (unreachable.Count > 0) yield return "unreachable: " + string.Join(", ", unreachable.Select(id => $"{Catalog.ItemName(id)} ({id})"));
        if (upkeep.Busy) yield return "upkeep: " + upkeep.Status;
        if (ReduceBlocked is { } blocked) yield return "reduction blocked: " + blocked;
    }

    /// <summary>Extracting materia or reducing collectables right now (its windows open).</summary>
    public bool UpkeepBusy => upkeep.Busy;

    /// <param name="reduceNow">Collectables gathered to be reduced; reduced between nodes as soon as they're in the bags.</param>
    public void Begin(IEnumerable<GatherTarget> gatherTargets, Func<GatherTarget, int> leftOf, Func<IReadOnlySet<uint>> collectablesNeeded,
        Func<IReadOnlySet<uint>>? reduceNow = null)
    {
        targets = gatherTargets.Where(t => t.Supply != Supply.Fish).ToList();
        left = leftOf;
        neededCollectables = collectablesNeeded;
        this.reduceNow = reduceNow ?? (() => new HashSet<uint>());
        visited.Clear();
        givenUp.Clear();
        giveUps.Clear();
        skipNodes.Clear();
        abandoned.Clear();
        unreachable.Clear();
        lastVisitEnd = null;
        visitStart = null;
        wasOnNode = false;
        gpWaitSince = null;
        gpWaitedThisCycle = 0;
        Waiting = false;
        WaitingToRetry = false;
        Failure = null;
        Go(Step.Choose);
    }

    private void NodeClosed()
    {
        wasOnNode = false;
        OnVisitEnd();
        plan = null;
        solving = null;
        luckUsed = false;
        collectableFull = false;
        if (node != null && current != null) MarkVisitedAt(lastNodePosition);
        if (lastNodeObject != 0) skipNodes[lastNodeObject] = DateTime.UtcNow + JustGathered;
    }

    public void Pause()
    {
        if (step == Step.Idle) return;
        if (wasOnNode && !OnNode) NodeClosed();
        lastVisitEnd = null;
        EndGpWait();
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
        if (wasOnNode && !OnNode) NodeClosed();
        wasOnNode = false;
        visitStart = null;
        lastVisitEnd = null;
        EndGpWait();
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
            if (!wasOnNode) OnVisitStart();
            wasOnNode = true;
            WorkNode();
            return;
        }
        if (wasOnNode)
        {
            // The node closed: it respawns elsewhere in its group, so mark this spot and pick again.
            NodeClosed();
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
            case Step.Approach: ApproachTick(me); return;
            case Step.Interact: InteractTick(me); return;
        }
    }

    // ------------------------------------------------------------------ choosing a node

    private void Choose()
    {
        if (upkeep.Begin(neededCollectables(), reduceNow())) { Go(Step.Upkeep); return; }
        var now = DateTimeOffset.UtcNow;
        var here = Services.ClientState.TerritoryType;
        var unlocked = Teleporter.Unlocked();
        var reach = new Dictionary<uint, bool>();
        bool CanReach(uint t) => reach.TryGetValue(t, out var r) ? r : reach[t] = Teleporter.CanReach(t);

        var best = default((GatherTarget Target, GatherPoint Point, int Score)?);
        var skipped = false;
        // Nodes in view that can be opened, by their point: one right here beats a trip to another spawn spot.
        var me = Services.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        var inView = Services.Objects
            .Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable
                        && !(skipNodes.TryGetValue(o.GameObjectId, out var until) && DateTime.UtcNow < until))
            .GroupBy(o => o.BaseId)
            .ToDictionary(g => g.Key, g => g.Min(o => Vector3.Distance(o.Position, me)));
        foreach (var t in targets)
        {
            if (left(t) <= 0) continue;
            var any = false;
            var dropped = false;
            foreach (var p in Catalog.GatherPoints(t.ItemId))
            {
                if (PlanService.JobLevel(p.ClassJobId) < p.Level) continue;
                if (p.Tome != 0 && !ItemUnlocks.IsLearned(p.Tome)) continue;
                if (BasePositions(t.ItemId, p.BaseId).Count == 0 || !CanReach(p.TerritoryId)) continue;
                if (abandoned.Contains(p.BaseId)) { dropped = true; continue; }
                any = true;
                if (p.Timed && !Eorzea.IsUp(p.Mask, now)) continue;
                if (givenUp.TryGetValue(p.BaseId, out var until) && DateTime.UtcNow < until) { skipped = true; continue; }
                // Timed nodes first (short windows), then this zone, then the cheapest teleport.
                var cost = p.TerritoryId == here ? 0 : unlocked.Where(d => d.TerritoryId == p.TerritoryId).Select(d => (int)d.GilCost).DefaultIfEmpty(5000).Min();
                var score = (p.Timed ? 0 : 1_000_000) + cost;
                if (p.TerritoryId == here && inView.TryGetValue(p.PointId, out var seen) && seen < InView)
                    score -= 100_000 - (int)seen; // timed nodes still come first
                if (best is not { } b || score < b.Score) best = (t, p, score);
            }
            if (!any && unreachable.Add(t.ItemId))
                log(dropped
                    ? $"Stopped trying to gather {Catalog.ItemName(t.ItemId)}: its node couldn't be landed at or opened {MaxGiveUps} times in a row."
                    : $"No node you can reach and gather {Catalog.ItemName(t.ItemId)} at (level, folklore tome, zone, or no known position).", true);
        }

        // Every item still needed has no node the character can get to: nothing to wait for.
        var stillNeeded = targets.Where(t => left(t) > 0).ToList();
        if (stillNeeded.Count > 0 && stillNeeded.All(t => unreachable.Contains(t.ItemId)))
        {
            Fail($"Can't gather {string.Join(", ", stillNeeded.Select(t => Catalog.ItemName(t.ItemId)))}: no node you can reach (level, folklore tome, zone, no known position, or a node that couldn't be landed at or opened). Get them yourself.");
            return;
        }
        if (best is not { } pick)
        {
            Waiting = true;
            WaitingToRetry = skipped;
            lastVisitEnd = null;
            Status = skipped ? "Gave up on a node for a while; trying it again soon" : "No node is up right now";
            Wait(3);
            return;
        }
        Waiting = false;
        WaitingToRetry = false;
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
    /// <summary>The chosen node is timed and its window has closed: it won't be at any of its spots until the next one.</summary>
    private bool WindowClosed => node is { Timed: true } n && !Eorzea.IsUp(n.Mask, DateTimeOffset.UtcNow);

    /// <summary>
    /// Leaves a timed node whose window closed on the way or between its spots, and chooses again: what's up now
    /// (regular nodes, another timed one), or waiting for the next window when nothing is.
    /// </summary>
    private void LeaveClosedWindow()
    {
        travel.Cancel();
        Vnav.Stop();
        log($"The {Catalog.ItemName(current!.Value.ItemId)} node's time is over; on to what can be gathered now.", false);
        Go(Step.Choose);
    }

    private void NextSpot()
    {
        if (WindowClosed) { LeaveClosedWindow(); return; }
        var me = Services.Objects.LocalPlayer!.Position;
        // A node of this group already in view (the one next to the node just gathered): straight there.
        if (Services.ClientState.TerritoryType == node!.TerritoryId && LiveNode() is { } seen && Vector3.Distance(seen.Position, me) < InView)
        {
            spot = seen.Position;
            var standAt = Catalog.Gather.StandFor(spot);
            travel.GoTo($"the {Catalog.ItemName(current!.Value.ItemId)} node", node.TerritoryId, new Vector2(standAt.X, standAt.Z), 2.8f,
                () => LiveNode()?.Position is { } p ? Catalog.Gather.StandFor(p) : null);
            Go(Step.Travel);
            return;
        }
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

    /// <summary>A node of the group in view this far away is gone to directly.</summary>
    private const float InView = 100f;

    /// <summary>The nearest node of the chosen group that can be opened: not the one just gathered, nor one that wouldn't open.</summary>
    private IGameObject? LiveNode()
    {
        var me = Services.Objects.LocalPlayer?.Position ?? Vector3.Zero;
        var now = DateTime.UtcNow;
        return Services.Objects
            .Where(o => o.ObjectKind == ObjectKind.GatheringPoint && o.IsTargetable && nodePoints.Contains(o.BaseId)
                        && !(skipNodes.TryGetValue(o.GameObjectId, out var until) && now < until))
            .OrderBy(o => Vector3.DistanceSquared(o.Position, me))
            .FirstOrDefault();
    }

    private void TravelTick(IGameObject me)
    {
        // A timed node's window can close while flying to it, or while going round its spots.
        if (WindowClosed) { LeaveClosedWindow(); return; }
        travel.Tick();
        Status = travel.Status;
        if (travel.Current == Travel.State.Failed)
        {
            log($"Couldn't get to the {Catalog.ItemName(current!.Value.ItemId)} node: {travel.Failure}", true);
            MarkVisitedAt(spot);
            Go(Step.Choose, 2);
            return;
        }
        if (travel.Current != Travel.State.Arrived) return;
        if (LiveNode() is { } live && Across(live.Position, me.Position) < NodeHere)
        {
            interactTries = 0;
            approachWalks = 0;
            landPresses = landMoves = 0;
            mustLand = landingForCordial = noCordialThisVisit = false;
            Go(Step.Approach);
            return;
        }
        // Nothing at this spot right now: try the next one.
        MarkVisitedAt(spot);
        NextSpot();
    }

    /// <summary>
    /// The game opens a node from under 3.5 yalms across and under 3 up or down (a node set in a
    /// cliff, flown to from above, is often further); a little margin keeps clear of the edge.
    /// </summary>
    private static bool InReach(Vector3 node, Vector3 me)
        => Vector2.Distance(new Vector2(node.X, node.Z), new Vector2(me.X, me.Z)) < 3.2f && MathF.Abs(node.Y - me.Y) < 2.7f;

    /// <summary>
    /// The last yalms. In reach: open it (from the mount too; opening lands and dismounts by itself).
    /// Otherwise land, then walk up to the node until the game lets it open.
    /// </summary>
    private void ApproachTick(IGameObject me)
    {
        var live = LiveNode();
        if (live == null || Across(live.Position, me.Position) > NodeHere) { Vnav.Stop(); Go(Step.Choose); return; }
        var c = Services.Condition;
        var mounted = c[ConditionFlag.InFlight] || c[ConditionFlag.Mounted];
        if (!mounted) mustLand = landingForCordial = false;
        // Landing only for a cordial, and the GP came back meanwhile: open from where we are.
        if (landingForCordial && NeedsGpFirst() == null) mustLand = landingForCordial = false;
        if (!mustLand && InReach(live.Position, me.Position))
        {
            Vnav.Stop();
            Go(Step.Interact);
            return;
        }
        if (mounted)
        {
            Land(me, live);
            return;
        }
        if (Vnav.Busy && DateTime.UtcNow < walkUntil) return;
        Vnav.Stop();
        if (approachWalks >= ApproachWalks)
        {
            // As close as the ground gets: try opening it from here.
            Go(Step.Interact);
            return;
        }
        approachWalks++;
        Status = "Walking up to the node";
        // The walkable ground nearest the node: a node set in a wall opens from its foot.
        var goal = Vnav.NearestPoint(live.Position, 3f, 4f) ?? live.Position;
        if (!Vnav.MoveCloseTo(goal, false, 0.8f)) Vnav.MoveTo(goal, false);
        walkUntil = DateTime.UtcNow.AddSeconds(10);
        Wait(0.5);
    }

    /// <summary>
    /// Lands and gets off the mount. The game won't land against a wall or over a ledge: after two
    /// presses that change nothing, fly down onto the walkable ground below, then onto ground a few
    /// yalms away from the node, and press again; after that, try another of the node's spots.
    /// </summary>
    private void Land(IGameObject me, IGameObject live)
    {
        if (Vnav.Busy && DateTime.UtcNow < walkUntil) { Status = "Flying down to the ground"; return; }
        var inFlight = Services.Condition[ConditionFlag.InFlight];
        // High above the node (came in over a cliff edge): fly down beside it first, or the landing
        // ends up on top of the cliff.
        if (inFlight && !landingForCordial && landMoves == 0 && me.Position.Y - live.Position.Y > 3f)
        {
            landMoves++;
            landPresses = 0;
            if (Vnav.NearestPoint(live.Position, 4f, 6f) is { } beside)
            {
                Status = "Flying down to the node";
                Vnav.MoveTo(beside, true);
                walkUntil = DateTime.UtcNow.AddSeconds(8);
            }
            Wait(0.5);
            return;
        }
        if (!inFlight || landPresses < 2)
        {
            if (++landPresses > 8) { LandingFailed(live, $"Couldn't get off the mount next to the {Catalog.ItemName(current!.Value.ItemId)} node."); return; }
            Status = Services.Condition[ConditionFlag.InFlight] ? "Landing" : "Dismounting";
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, LandOrDismount);
            Wait(1.2);
            return;
        }
        // Two presses changed nothing. A landing just for a cordial never flies off (opening from the
        // mount right here still works); otherwise try the ground below, then ground a few yalms away.
        if (landingForCordial || landMoves >= 2) { LandingFailed(live, $"Couldn't land next to the {Catalog.ItemName(current!.Value.ItemId)} node."); return; }
        var away = new Vector2(me.Position.X - live.Position.X, me.Position.Z - live.Position.Z);
        away = away.LengthSquared() > 0.01f ? Vector2.Normalize(away) : Vector2.UnitX;
        var floor = landMoves == 0
            ? Vnav.NearestPoint(me.Position, 5f, 30f)
            : Vnav.NearestPoint(me.Position + new Vector3(away.X, 0, away.Y) * 5f, 5f, 30f);
        landMoves++;
        landPresses = 0;
        if (floor is { } ground)
        {
            Status = "Flying down to the ground";
            Vnav.MoveTo(ground, true);
            walkUntil = DateTime.UtcNow.AddSeconds(5);
        }
        Wait(0.5);
    }

    /// <summary>
    /// Landing didn't work. When it was only for a cordial (no moves made, still in reach), skip
    /// cordials at this node, wait for the GP on the mount and open from it (the game lands by itself
    /// then); otherwise give the node up.
    /// </summary>
    private void LandingFailed(IGameObject live, string why)
    {
        if (!landingForCordial) { GiveUp(live, why); return; }
        Vnav.Stop();
        noCordialThisVisit = true;
        mustLand = landingForCordial = false;
        landPresses = landMoves = 0;
        Go(Step.Interact);
    }

    /// <summary>XZ distance: a node above or below still counts as here; landing and walking deal with the height.</summary>
    private static float Across(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    /// <summary>
    /// Marks the known spawn spot at a position, so the next trip skips it. Only the nearest one: two nodes of a group
    /// can stand a couple of yalms apart, and the other one is still to gather.
    /// </summary>
    private void MarkVisitedAt(Vector3 position)
    {
        var nearest = BasePositions(current!.Value.ItemId, node!.BaseId)
            .Where(s => Across(s.At, position) < 3f).OrderBy(s => Across(s.At, position)).FirstOrDefault();
        if (nearest.Point != 0) visited.Add((nearest.Point, (int)nearest.At.X, (int)nearest.At.Z));
    }

    /// <summary>
    /// Gives up on this node for a while (no landing next to it, or it won't open): its node group is
    /// skipped when choosing, so the run moves on instead of flying straight back to it.
    /// </summary>
    private void GiveUp(IGameObject live, string why)
    {
        Vnav.Stop();
        MarkVisitedAt(live.Position);
        // Only this node, while another of its group is in view: that one is tried next, the group isn't given up.
        skipNodes[live.GameObjectId] = DateTime.UtcNow + GiveUpFor;
        if (LiveNode() is { } other && Vector3.Distance(other.Position, live.Position) < InView)
        {
            log($"{why} Trying the other {Catalog.ItemName(current!.Value.ItemId)} node in view.", false);
            mustLand = false;
            NextSpot();
            return;
        }
        var group = node!.BaseId;
        var times = giveUps[group] = giveUps.GetValueOrDefault(group) + 1;
        if (times >= MaxGiveUps)
        {
            // The same failure again and again: retrying every 10 minutes would never end.
            abandoned.Add(group);
            log($"{why} Not trying this node again in this run.", true);
        }
        else
        {
            log($"{why} Trying other nodes; this one again in {(int)GiveUpFor.TotalMinutes} min.", false);
            givenUp[group] = DateTime.UtcNow + GiveUpFor;
        }
        mustLand = false;
        Go(Step.Choose, 1);
    }

    private void InteractTick(IGameObject me)
    {
        var live = LiveNode();
        if (live == null || Across(live.Position, me.Position) > NodeHere) { Go(Step.Choose); return; }
        // Drifted out of reach (a landing that slid down a slope): walk up again first.
        if (!InReach(live.Position, me.Position) && approachWalks < ApproachWalks) { Go(Step.Approach); return; }
        var c = Services.Condition;
        if ((c[ConditionFlag.InFlight] || c[ConditionFlag.Mounted]) && interactTries >= 2)
        {
            // Opening didn't land us (too high, or over ground the game won't land on): land first.
            mustLand = true;
            landPresses = landMoves = 0;
            Go(Step.Approach);
            return;
        }
        if (NeedsGpFirst() is { } gp)
        {
            gpWaitSince ??= DateTime.UtcNow;
            if (plugin.Config.Run.UseCordials && !noCordialThisVisit && Cordials.AnyInBags)
            {
                if (c[ConditionFlag.InFlight] || c[ConditionFlag.Mounted])
                {
                    // Cordials can't be drunk on a mount. Land only when one will really be drunk (judged
                    // at the GP there'll be when the cooldown ends, before the GP is back by itself) and
                    // the ground below is still in reach of the node; otherwise wait up here and open
                    // from the mount.
                    var regenSeconds = (gp.Need - (int)gp.Have) / GpBudget.RegenPerSecond;
                    var readyIn = Cordials.SecondsUntilReady();
                    var gpThen = (int)(gp.Have + readyIn * GpBudget.RegenPerSecond);
                    var groundInReach = Vnav.NearestPoint(me.Position, 2f, 8f) is { } below && InReach(live.Position, below);
                    if (readyIn < regenSeconds && gpThen < gp.Need && groundInReach
                        && GpBudget.Pick(Cordials.KindsInBags(), gpThen, gp.Need, gp.Max) != null)
                    {
                        mustLand = true;
                        landingForCordial = true;
                        landPresses = landMoves = 0;
                        Go(Step.Approach);
                        return;
                    }
                }
                else if (GpBudget.Pick(Cordials.Ready(), (int)gp.Have, gp.Need, gp.Max) is { } cordial && Cordials.Drink(cordial))
                {
                    log($"Drank a {Cordials.Name(cordial)} (+{cordial.Gp} GP) instead of waiting for GP.", false);
                    Wait(1.5);
                    return;
                }
            }
            Status = $"Waiting for GP for a full collectable rotation ({gp.Have} of {gp.Need})";
            Wait(1);
            return;
        }
        EndGpWait();
        if (++interactTries > 4) { GiveUp(live, $"Couldn't open the {Catalog.ItemName(current!.Value.ItemId)} node."); return; }
        Status = "Opening the node";
        lastNodePosition = live.Position;
        lastNodeObject = live.GameObjectId;
        Interact.With(live);
        Wait(1.2);
    }

    /// <summary>
    /// A collectable gets the top reward only with the full rotation (Scrutiny, Solid Reason): on a
    /// regular node, wait for that much GP (or the most the job has). Timed nodes would close meanwhile.
    /// </summary>
    /// <summary>Standing at a node until the GP is back (not counted as gathering time).</summary>
    public bool WaitingForGp => gpWaitSince != null;
    private DateTime? gpWaitSince;
    private double gpWaitedThisCycle;

    private void EndGpWait()
    {
        if (gpWaitSince is { } since) gpWaitedThisCycle += (DateTime.UtcNow - since).TotalSeconds;
        gpWaitSince = null;
    }

    // A collectable node visit, measured to learn what one gives, spends and takes.
    private (uint ItemId, uint Gp, int Items, DateTime At)? visitStart;
    /// <summary>When the last node closed; null after a pause, a stop or a wait, so idle time isn't learned.</summary>
    private DateTime? lastVisitEnd;

    /// <summary>GP when the collectable node now open was opened (null when none is open).</summary>
    public uint? VisitStartGp => OnNode ? visitStart?.Gp : null;

    private void OnVisitStart()
    {
        EndGpWait();
        if (node != null) giveUps.Remove(node.BaseId); // it opened: the give-ups weren't in a row
        visitStart = current is { } cur && GameState.GatheredAsCollectable(cur.ItemId) && Services.Objects.LocalPlayer is { } p
            ? (cur.ItemId, p.CurrentGp, GameState.GatheredInBags(cur.ItemId), DateTime.UtcNow)
            : null;
    }

    private void OnVisitEnd()
    {
        var now = DateTime.UtcNow;
        if (visitStart is { } v && Services.Objects.LocalPlayer is { } p)
        {
            var t = plugin.Config.Timings;
            var gained = GameState.GatheredInBags(v.ItemId) - v.Items;
            if (gained > 0)
            {
                t.CollectablesPerVisit = Math.Clamp(Timings.Blend(t.CollectablesPerVisit, gained), 1, 10);
                t.GpPerCollectableVisit = Math.Clamp(Timings.Blend(t.GpPerCollectableVisit, Math.Max(0, (int)v.Gp - (int)p.CurrentGp)), 0, 1000);
                var inNode = (now - v.At).TotalSeconds;
                if (inNode is >= 3 and <= 180) t.SecondsInCollectableNode = Math.Clamp(Timings.Blend(t.SecondsInCollectableNode, inNode), 3, 180);
                // From the last node closing to this one (travel and the node), waiting for GP left out.
                // Pauses, stops and waits clear lastVisitEnd, so idle time never gets in.
                var cycle = lastVisitEnd is { } last ? (now - last).TotalSeconds - gpWaitedThisCycle : 0;
                if (cycle is >= 10 and <= 240) t.SecondsPerCollectableVisit = Math.Clamp(Timings.Blend(t.SecondsPerCollectableVisit, cycle), 15, 240);
                plugin.Config.Save();
            }
        }
        visitStart = null;
        lastVisitEnd = now;
        gpWaitedThisCycle = 0;
    }

    private (uint Have, int Need, int Max)? NeedsGpFirst()
    {
        if (!plugin.Config.Run.WaitForCollectableGp || node is not { Timed: false } || current is not { } cur || !GameState.GatheredAsCollectable(cur.ItemId)) return null;
        if (Services.Objects.LocalPlayer is not { } p) return null;
        var need = GpBudget.Need((int)p.MaxGp);
        return need > 0 && p.CurrentGp < need ? (p.CurrentGp, need, (int)p.MaxGp) : null;
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
        var (low, mid, high) = (w.Low, w.Mid, w.High);
        if (low == 0 && mid == 0 && high == 0 && FallbackCollectability?.Invoke(w.ItemId) is > 0 and var aim) low = mid = high = aim;
        var state = new CollectableState(w.Collectability, w.Integrity, w.MaxIntegrity, w.ScourGain, w.MeticulousGain, w.BrazenGainMax,
            low, mid, high, (int)p.CurrentGp, level,
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
        if (next != Step.Interact) EndGpWait();
        step = next;
        stepSince = DateTime.UtcNow;
        Wait(wait);
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
