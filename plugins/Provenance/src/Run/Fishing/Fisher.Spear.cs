using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Core.Fishing;
using Provenance.Data;
using Provenance.Integrations;
using Provenance.Integrations.Fishing;

namespace Provenance.Run.Fishing;

// Spearfishing: the spot's nodes are gathering points (the GatheringPoint rows of its PointBase), up at one
// point at a time. Go to the nearest one in view (never back to the last two points emptied), otherwise look
// at the known positions one by one; get within reach (3.5 yalms across, 3 up or down), off the mount, open
// it, and let the SpearGame gig the wanted fish until the window closes. A fish behind swimming shadows needs
// its first predator gigged at that predator's own spot first (the shadows' parent spot). Follows GatherBuddy
// Reborn's spearfishing movement (AutoGather.cs DoNodeMovement, AutoGather.Spearfishing.cs; Apache-2.0).
internal sealed unsafe partial class Fisher
{
    /// <summary>The spot whose nodes are worked now: the target's own, or the parent spot of its swimming shadows.</summary>
    private FishingSpotInfo? pool;
    /// <summary>The fish to gig at <see cref="pool"/>.</summary>
    private List<FishInfo> wanted = new();
    /// <summary>Points emptied lately, per spot (newest last).</summary>
    private readonly Dictionary<uint, List<uint>> recentPoints = new();
    /// <summary>Known positions looked at this round without a node there.</summary>
    private readonly HashSet<(uint Spot, int X, int Z)> seen = new();
    /// <summary>Nodes (game object ids) that wouldn't open or couldn't be reached.</summary>
    private readonly HashSet<ulong> skipNodes = new();
    private ulong nodeObject;
    private uint nodePoint;
    /// <summary>The trip goes to a node in view (not to a known position to look at).</summary>
    private bool toNode;
    private Vector3 exploreAt;
    private int exploreRounds, openTries, approachWalks;
    private DateTime nextScan;
    /// <summary>Swimming shadows: the first predator to gig at its own spot, how many, and the count caught when this round began.</summary>
    private (uint Predator, int Need, int Start)? shadowPrep;
    private DateTime? shadowSearchUntil;
    private static readonly Dictionary<uint, bool> ShadowSpots = new();

    private void ResetSpear()
    {
        recentPoints.Clear();
        ResetSpearSpot();
    }

    private void ResetSpearSpot()
    {
        pool = null;
        wanted = new List<FishInfo>();
        seen.Clear();
        skipNodes.Clear();
        nodeObject = 0;
        nodePoint = 0;
        exploreRounds = 0;
        shadowPrep = null;
        shadowSearchUntil = null;
    }

    private List<uint> Recent(uint spotId) => recentPoints.TryGetValue(spotId, out var l) ? l : recentPoints[spotId] = new List<uint>();

    /// <summary>A swimming-shadows spot (SpearfishingNotebook.IsShadowNode): its node only shows after predators were gigged.</summary>
    private static bool IsShadow(FishingSpotInfo s)
    {
        if (!s.Spear) return false;
        if (ShadowSpots.TryGetValue(s.Id, out var known)) return known;
        var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.SpearfishingNotebook>().GetRowOrDefault(s.Id - FishingSpotInfo.SpearOffset);
        return ShadowSpots[s.Id] = row?.IsShadowNode == true;
    }

    /// <summary>The fish to gig at a spot: these first, then any other spearfish the order still needs that lives there.</summary>
    private List<FishInfo> WantedAt(FishingSpotInfo s, IEnumerable<FishInfo> first)
    {
        var list = first.ToList();
        foreach (var t in targets)
            if (left(t) > 0 && Data.Fish(t.ItemId) is { Spear: true } f && f.Spots.Contains(s.Id) && list.All(x => x.ItemId != f.ItemId))
                list.Add(f);
        return list;
    }

    /// <summary>
    /// Which spot's nodes to work and what to gig there. Normally the target's spot. For a fish behind swimming
    /// shadows (GBR's ParentNode rule): the shadows' spot while its node is in view (gig the other predators and
    /// the fish), otherwise the first predator's own spot until enough of it is gigged, then a look around the
    /// shadows' spot for two minutes (and back to the predator when they don't show).
    /// </summary>
    private void ChoosePool()
    {
        var s = spot!;
        var f = fish!;
        pool = s;
        wanted = WantedAt(s, new[] { f });
        if (f.Predators.Count == 0 || !IsShadow(s)) return;

        var others = f.Predators.Skip(1).Select(p => Data.Fish(p.ItemId)).OfType<FishInfo>().Append(f).ToList();
        if (Services.ClientState.TerritoryType == s.TerritoryId && SpearGame.NearestNode(Data.PointsOfBase(s.PointBase), skipNodes) != null)
        {
            wanted = WantedAt(s, others);
            return;
        }
        var (predator, need) = f.Predators[0];
        shadowPrep ??= (predator, need, caught.GetValueOrDefault(predator));
        if (caught.GetValueOrDefault(predator) - shadowPrep.Value.Start >= need)
        {
            // Enough gigged: the shadows should be up at their spot now; look for them a while.
            shadowSearchUntil ??= DateTime.UtcNow.AddMinutes(2);
            if (DateTime.UtcNow < shadowSearchUntil)
            {
                wanted = WantedAt(s, others);
                return;
            }
            log($"No swimming shadows showed up at {SpotName(s)}; gigging more {Name(predator)} first.", false);
            shadowPrep = (predator, need, caught.GetValueOrDefault(predator));
            shadowSearchUntil = null;
        }
        if (ParentSpot(predator, s) is not { } parent)
        {
            // Nowhere to gig the predator: look at the shadows' spot anyway (they may be up already).
            wanted = WantedAt(s, others);
            return;
        }
        pool = parent;
        wanted = WantedAt(parent, Data.Fish(predator) is { } p ? new[] { p } : Array.Empty<FishInfo>());
    }

    /// <summary>The predator's own spot (not a shadows' one), the shadows' zone first.</summary>
    private FishingSpotInfo? ParentSpot(uint predator, FishingSpotInfo shadow)
        => Data.Fish(predator) is { Spear: true } p
            ? FishAccess.UsableSpots(Data, p, PlanService.JobLevel(FishAccess.Fisher)).Where(x => !IsShadow(x))
                .OrderByDescending(x => x.TerritoryId == shadow.TerritoryId).FirstOrDefault()
            : null;

    /// <summary>The points of the pool a node may be up at now.</summary>
    private List<uint> AllowedPoints(FishingSpotInfo p) => SpotNodes.Allowed(Data.PointsOfBase(p.PointBase), Recent(p.Id));

    private void SpearFind(IGameObject me)
    {
        if (current is not { } t || fish == null || spot == null || left(t) <= 0) { Go(Step.Choose); return; }
        ChoosePool();
        if (SpearGame.WindowOpen) { StartSpear(); return; }
        var p = pool!;
        var here = Services.ClientState.TerritoryType == p.TerritoryId;
        var allowed = AllowedPoints(p);
        if (here && SpearGame.NearestNode(allowed, skipNodes) is { } live) { GoToNode(live); return; }

        // No node in view: look at the known positions one by one, nearest first. When the points the node may be
        // at have none on record, look around the spot's other positions (the points are close together).
        var positions = allowed.SelectMany(point => Data.Positions(point)).ToList();
        if (positions.Count == 0) positions = Data.PointsOfBase(p.PointBase).SelectMany(point => Data.Positions(point)).ToList();
        var i = SpotNodes.NextToExplore(positions, here ? me.Position : null, j => seen.Contains(Key(p.Id, positions[j])));
        if (i < 0)
        {
            seen.RemoveWhere(k => k.Spot == p.Id);
            skipNodes.Clear();
            if (positions.Count == 0 || ++exploreRounds > 3)
            {
                exploreRounds = 0;
                var broken = Rest(spot, "no spearfishing node showed up", broken: true);
                log($"No spearfishing node showed up at {SpotLabel(p)}; {AfterRest(broken)}.", true);
                Go(Step.Choose);
                return;
            }
            Status = $"No node up at {SpotName(p)} right now; looking again";
            Wait(5);
            return;
        }
        exploreAt = positions[i];
        toNode = false;
        var at = exploreAt;
        travel.GoTo(SpotName(p), p.TerritoryId, Xz(at), SpotNodes.SeenFrom / 2, () => at);
        nextCheck = DateTime.UtcNow.AddSeconds(5);
        Go(Step.SpearTravel);
    }

    private void GoToNode(IGameObject live)
    {
        nodeObject = live.GameObjectId;
        nodePoint = live.BaseId;
        toNode = true;
        openTries = approachWalks = landPresses = 0;
        travel.GoTo($"the {Name(fish!.ItemId)} node", pool!.TerritoryId, Xz(live.Position), 2.5f, () => LiveNode()?.Position);
        nextCheck = DateTime.UtcNow.AddSeconds(5);
        Go(Step.SpearTravel);
    }

    /// <summary>The node being gone to, while it's still there and can be opened.</summary>
    private IGameObject? LiveNode()
    {
        if (nodeObject == 0) return null;
        foreach (var o in Services.Objects)
            if (o.GameObjectId == nodeObject) return SpearGame.Usable(o) ? o : null;
        return null;
    }

    private void SpearTravelTick(IGameObject me)
    {
        travel.Tick();
        Status = travel.Status;
        var p = pool!;
        var here = Services.ClientState.TerritoryType == p.TerritoryId;
        if (here && DateTime.UtcNow >= nextScan)
        {
            nextScan = DateTime.UtcNow.AddSeconds(0.5);
            if (toNode && LiveNode() == null)
            {
                // Someone else took it, or it ran out: look again.
                travel.Cancel();
                Go(Step.SpearFind, 0.5);
                return;
            }
            if (!toNode && SpearGame.NearestNode(AllowedPoints(p), skipNodes) is { } live)
            {
                // A node came into view on the way: go to it instead.
                travel.Cancel();
                GoToNode(live);
                return;
            }
        }
        if (travel.Current == Travel.State.Failed)
        {
            var why = travel.Failure ?? "the way there failed";
            if (!here)
            {
                var broken = Rest(spot!, "couldn't get there", broken: true);
                log($"Couldn't get to {SpotLabel(p)}: {why.TrimEnd('.')}; {AfterRest(broken)}.", true);
                Go(Step.Choose, 2);
                return;
            }
            log($"Couldn't get to the {(toNode ? "node" : "spot")} at {SpotName(p)}: {why.TrimEnd('.')}. Trying another.", false);
            if (toNode) skipNodes.Add(nodeObject);
            else seen.Add(Key(p.Id, exploreAt));
            Go(Step.SpearFind, 1);
            return;
        }
        if (travel.Current != Travel.State.Arrived)
        {
            if (DateTime.UtcNow >= nextCheck)
            {
                nextCheck = DateTime.UtcNow.AddSeconds(5);
                if (!StillWorkable() || MoreUrgent() != null)
                {
                    travel.Cancel();
                    Vnav.Stop();
                    Go(Step.Choose);
                }
            }
            return;
        }
        if (toNode) { Go(Step.SpearApproach); return; }
        // Looked here and no node in view: every known position this close has been seen.
        foreach (var point in Data.PointsOfBase(p.PointBase))
            foreach (var pos in Data.Positions(point))
                if (SpotStands.Across(pos, me.Position) <= SpotNodes.SeenFrom) seen.Add(Key(p.Id, pos));
        seen.Add(Key(p.Id, exploreAt));
        Go(Step.SpearFind, 1);
    }

    /// <summary>Within reach (3.5 yalms across, 3 up or down) and off the mount; underwater, swim straight to it.</summary>
    private void SpearApproachTick(IGameObject me)
    {
        if (SpearGame.WindowOpen) { StartSpear(); return; }
        if (LiveNode() is not { } live) { Vnav.Stop(); Go(Step.SpearFind, 0.5); return; }
        var c = Services.Condition;
        var mounted = c[ConditionFlag.InFlight] || c[ConditionFlag.Mounted];
        if (SpotNodes.InReach(live.Position, me.Position))
        {
            Vnav.Stop();
            if (mounted)
            {
                if (++landPresses > 8) { GiveUpNode(live, "Couldn't get off the mount next to the spearfishing node"); return; }
                Status = c[ConditionFlag.InFlight] ? "Landing" : "Dismounting";
                var am = ActionManager.Instance();
                if (am != null) am->UseAction(ActionType.GeneralAction, LandOrDismount);
                Wait(1.2);
                return;
            }
            Go(Step.SpearOpen);
            return;
        }
        if (Vnav.Busy && DateTime.UtcNow < walkUntil) return;
        Vnav.Stop();
        if (approachWalks >= 3)
        {
            // As close as it gets: try opening from here.
            Go(Step.SpearOpen);
            return;
        }
        approachWalks++;
        var swim = c[ConditionFlag.Diving] || c[ConditionFlag.InFlight];
        var goal = swim ? live.Position : Vnav.NearestPoint(live.Position, 3f, 4f) ?? live.Position;
        if (!Vnav.MoveCloseTo(goal, swim, 1.5f)) Vnav.MoveTo(goal, swim);
        walkUntil = DateTime.UtcNow.AddSeconds(10);
        Status = "Moving up to the node";
        Wait(0.5);
    }

    private void SpearOpenTick(IGameObject me)
    {
        if (SpearGame.WindowOpen) { StartSpear(); return; }
        if (LiveNode() is not { } live) { Go(Step.SpearFind, 0.5); return; }
        var c = Services.Condition;
        if (c[ConditionFlag.InCombat]) { Status = "In combat; spearfishing after the fight"; Wait(1); return; }
        if (c[ConditionFlag.InFlight] || c[ConditionFlag.Mounted]) { Go(Step.SpearApproach); return; }
        if (!SpotNodes.InReach(live.Position, me.Position) && approachWalks < 3) { Go(Step.SpearApproach); return; }
        if (GameState.CurrentJob != FishAccess.Fisher) { Go(Step.Gear); return; }
        if (CollectOff()) return;
        if (++openTries > 4) { GiveUpNode(live, "Couldn't open the spearfishing node"); return; }
        Status = "Opening the node";
        Interact.With(live);
        Wait(1.2);
    }

    private void GiveUpNode(IGameObject live, string why)
    {
        Vnav.Stop();
        skipNodes.Add(live.GameObjectId);
        log($"{why}; trying another.", false);
        Go(Step.SpearFind, 1);
    }

    private void StartSpear()
    {
        if (wanted.Count == 0 && fish != null) wanted = new List<FishInfo> { fish };
        spear.Begin(wanted);
        Status = $"Spearfishing for {Name(fish!.ItemId)}";
        Go(Step.Spear);
    }

    /// <summary>Every frame while the window is open; once it closes (the node is used up), on to the next node.</summary>
    private void SpearTick()
    {
        if (!spear.Finished && SpearGame.WindowOpen)
        {
            if (spear.Status.Length > 0) Status = spear.Status;
            return;
        }
        if (spear.Active) spear.Stop();
        nodesAtSpot++;
        if (pool != null && nodePoint != 0) SpotNodes.Remember(Recent(pool.Id), nodePoint);
        // The shadows' node is gone once used: the predator has to be gigged again for the next one.
        if (pool != null && spot != null && pool.Id == spot.Id && IsShadow(spot))
        {
            shadowPrep = null;
            shadowSearchUntil = null;
        }
        nodeObject = 0;
        nodePoint = 0;
        exploreRounds = 0;
        // The next node is a new object (it may take an old one's slot): nothing to skip any more.
        skipNodes.Clear();
        if (pausing) { Settle(Step.Paused); return; }
        Go(Step.Choose, 1);
    }
}
