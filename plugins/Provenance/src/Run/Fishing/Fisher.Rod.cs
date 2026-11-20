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
using GameObjectStruct = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace Provenance.Run.Fishing;

// Rod fishing: the bait (own bait, else Versatile Lure; bought mid-run when out), the place to stand
// (recorded stands, the player's learned ones first), getting there and onto it (land, walk the last bit,
// a height check), facing the water, checking that a cast works (a step forward up to three times, then the
// next stand), the angler's session and what it ends with (wary: another stand 10+ yalms away; amiss:
// another fishing hole, or leave the area and come back). Follows GatherBuddy Reborn's fishing approach
// (AutoGather.cs DoFishMovement, FishRecorder; Apache-2.0).
internal sealed unsafe partial class Fisher
{
    /// <summary>What to cast with, what to buy first, or why there's no bait for this fish.</summary>
    private sealed record BaitPlan(uint Use, List<Purchase> Buy, string? Problem);

    // The place to stand.
    private Vector3 standPos;
    private float standFacing;
    /// <summary>The recorded stand in use (spot, rounded X and Z), kept when a step forward moves <see cref="standPos"/>.</summary>
    private (uint Spot, int X, int Z)? standKey;
    private readonly HashSet<(uint Spot, int X, int Z)> failedStands = new();
    private int standWalks, landPresses, landMoves, faceTries, jiggles;
    private DateTime lastJiggle;
    private DateTime walkUntil;
    private DateTime nextCheck;
    private bool standLearned;
    /// <summary>Asked the angler to stop after this catch for a fish whose window needs the fisher now.</summary>
    private bool switching;
    /// <summary>Times in a row the fisher left the area after the fish sensed something amiss (a catch resets it).</summary>
    private int amissLeaves;
    /// <summary>Sessions in a row that ended for want of bait while the bags held it.</summary>
    private int noBaitStreak;
    private Teleporter.Destination? leaveTo;
    private DateTime lingerUntil;
    private List<Purchase> buying = new();
    /// <summary>Bait that can't be bought this run, with why: not tried again.</summary>
    private readonly Dictionary<uint, string> unbuyable = new();
    /// <summary>Failed trips per bait this run (a fight, a stuck path or an NPC that didn't answer can pass).</summary>
    private readonly Dictionary<uint, int> buyFails = new();
    /// <summary>Failed trips after which a bait counts as unbuyable for the run (at once when the gil is short).</summary>
    private const int BuyTries = 2;
    /// <summary>The wait before trying a failed bait trip again.</summary>
    private static readonly TimeSpan BuyRetryAfter = TimeSpan.FromSeconds(30);
    private readonly HashSet<uint> lureNoted = new();

    private void ResetRod()
    {
        failedStands.Clear();
        unbuyable.Clear();
        buyFails.Clear();
        lureNoted.Clear();
        standKey = null;
        amissLeaves = 0;
        noBaitStreak = 0;
        switching = false;
        standLearned = false;
        leaveTo = null;
        buying = new List<Purchase>();
    }

    private static (uint Spot, int X, int Z) Key(uint spotId, Vector3 p) => (spotId, (int)MathF.Round(p.X), (int)MathF.Round(p.Z));

    // ------------------------------------------------------------------ bait

    /// <summary>
    /// The bait for a fish: its own (a mooched fish's chain starts with it) when in the main bags; else bought
    /// from a gil vendor when buying is on; else Versatile Lure when that fallback is on (in the bags, or
    /// bought). The fish caught first for Fisher's Intuition need their bait too. <paramref name="catches"/>
    /// sizes the purchase.
    /// </summary>
    private BaitPlan PlanBait(FishInfo f, int catches)
    {
        var s = Settings;
        var lure = FishAccess.VersatileLure;
        var buy = new List<Purchase>();
        bool InBags(uint id) => GameState.BagCount(id) > 0;
        VendorOffer? Shop(uint id) => s.BuyBait && !unbuyable.ContainsKey(id) ? baitShop.Offer(id) : null;

        uint use;
        if (InBags(f.Bait)) use = f.Bait;
        else if (Shop(f.Bait) is { } own)
        {
            use = f.Bait;
            buy.Add(new Purchase(f.Bait, SpotChoice.BaitToBuy(f, catches, s.SpareBait), own));
        }
        else if (s.VersatileLureFallback && InBags(lure)) use = lure;
        else if (s.VersatileLureFallback && Shop(lure) is { } lureOffer)
        {
            use = lure;
            buy.Add(new Purchase(lure, SpotChoice.BaitToBuy(f, catches, s.SpareBait), lureOffer));
        }
        else return new BaitPlan(0, new List<Purchase>(), NoBait(f.Bait));

        foreach (var (id, count) in f.Predators)
        {
            if (Data.Fish(id) is not { Spear: false } p || p.Bait == 0 || p.Bait == use || InBags(p.Bait) || buy.Any(b => b.ItemId == p.Bait)) continue;
            var amount = SpotChoice.BaitToBuy(p, Math.Max(1, count) * Math.Max(1, catches), s.SpareBait);
            if (Shop(p.Bait) is { } offer) { buy.Add(new Purchase(p.Bait, amount, offer)); continue; }
            // Versatile Lure stands in for it as well.
            if (s.VersatileLureFallback && (use == lure || InBags(lure) || buy.Any(b => b.ItemId == lure))) continue;
            if (s.VersatileLureFallback && Shop(lure) is { } stand) { buy.Add(new Purchase(lure, amount, stand)); continue; }
            return new BaitPlan(0, new List<Purchase>(), $"{NoBait(p.Bait)} (for {Name(id)}, caught first for Fisher's Intuition)");
        }
        return new BaitPlan(use, buy, null);
    }

    private string NoBait(uint baitId)
    {
        var name = baitId == 0 ? "bait" : Name(baitId);
        var why = unbuyable.TryGetValue(baitId, out var failed) ? $"no {name} in your bags, and buying it failed ({failed.TrimEnd('.')})"
            : !Settings.BuyBait ? $"no {name} in your bags, and buying bait is off in Settings"
            : $"no {name} in your bags, and no vendor you can reach sells it for gil{(plugin.Config.Buying.VendorMaxPrice > 0 ? " under your price cap" : "")}";
        return Settings.VersatileLureFallback && baitId != FishAccess.VersatileLure ? why + "; no Versatile Lure either" : why;
    }

    private void BaitStep()
    {
        if (current is not { } t || fish == null || left(t) <= 0) { Go(Step.Choose); return; }
        var plan = PlanBait(fish, Math.Max(1, left(t)));
        if (plan.Problem != null)
        {
            NoteUnreachable(t.ItemId, plan.Problem);
            ranked = null;
            Go(Step.Choose);
            return;
        }
        if (plan.Buy.Count > 0)
        {
            // In a fight the trip would fail (no teleport, no talking to the vendor): buy after it.
            if (Services.Condition[ConditionFlag.InCombat])
            {
                Status = "In combat; buying bait after the fight";
                Wait(1);
                return;
            }
            // The rod is in (Choose and every signal reel in first): off to the vendor, then back.
            buying = plan.Buy;
            log($"Buying bait: {string.Join(", ", plan.Buy.Select(p => $"{p.Amount}× {Name(p.ItemId)} from {p.Offer.NpcName}"))}.", false);
            shopper.Begin(plan.Buy);
            Go(Step.Shop);
            return;
        }
        bait = plan.Use;
        if (bait == FishAccess.VersatileLure && fish.Bait != bait && lureNoted.Add(fish.ItemId))
            log($"Fishing for {Name(fish.ItemId)} with Versatile Lure: there's no {Name(fish.Bait)} to be had.", false);
        Go(Step.Stand);
    }

    private void ShopTick()
    {
        shopper.Tick();
        Status = shopper.Status;
        if (shopper.Failure is { } why)
        {
            // A trip that failed for a passing reason (a fight, a stuck path, an NPC that didn't answer) is tried
            // again after a while. A bait that keeps failing, or that the gil left can't pay for, isn't tried again
            // this run: the next choice falls back to Versatile Lure or says why it can't.
            var missing = buying.Where(p => GameState.BagCount(p.ItemId) == 0).ToList();
            foreach (var p in missing)
            {
                var fails = buyFails[p.ItemId] = buyFails.GetValueOrDefault(p.ItemId) + 1;
                if (fails >= BuyTries || !CanAfford(p.Offer)) unbuyable.TryAdd(p.ItemId, why);
            }
            var retry = missing.Any(p => !unbuyable.ContainsKey(p.ItemId));
            log(retry ? $"Couldn't buy bait: {why.TrimEnd('.')}. Trying again in {BuyRetryAfter.TotalSeconds:0} seconds." : $"Couldn't buy bait: {why}", true);
            ranked = null;
            if (pausing) { PauseOnceIn(); return; }
            if (retry) Status = $"Couldn't buy bait; trying again in {BuyRetryAfter.TotalSeconds:0} seconds";
            Go(Step.Choose, retry ? BuyRetryAfter.TotalSeconds : 1);
            return;
        }
        if (!shopper.Finished && !shopper.IsPaused) return;
        ranked = null;
        if (pausing) { PauseOnceIn(); return; }
        if (!shopper.Finished) return; // paused, then resumed: carries on
        foreach (var p in buying) buyFails.Remove(p.ItemId);
        Go(Step.Choose, 0.5);
    }

    /// <summary>The gil in the bags (within the run's gil limit) pays for at least one of the offer.</summary>
    private bool CanAfford(VendorOffer offer)
    {
        long gil = GameState.BagCount(1);
        if (plugin.Runs.GilLeft is { } budget) gil = Math.Min(gil, budget);
        return gil >= Math.Max(1, offer.Price);
    }

    // ------------------------------------------------------------------ the place to stand

    /// <summary>Picks a place to stand at the spot (the one in use when it still works) and travels there.</summary>
    private void StartStand(IGameObject me)
    {
        if (spot is not { } s || fish == null) { Go(Step.Choose); return; }
        if (standKey is { } k && k.Spot == s.Id && !failedStands.Contains(k)) { TravelToStand(s); return; }
        var stands = Data.StandsFor(s.Id);
        var positions = stands.Select(x => new Vector3(x.X, x.Y, x.Z)).ToList();
        // In the zone: the nearest stand. From elsewhere: the one nearest the aetheryte the teleport lands at
        // (an aetheryte known only from the map has no height: count it at the stands' height).
        Vector3? from = Services.ClientState.TerritoryType == s.TerritoryId ? me.Position
            : positions.Count > 0 && Teleporter.NearestTo(s.TerritoryId, positions[0]) is { } d && Teleporter.Position(d.AetheryteId) is { } a
                ? new Vector3(a.X, positions[0].Y, a.Z) : null;
        var i = SpotStands.First(positions, from, j => failedStands.Contains(Key(s.Id, positions[j])));
        if (i < 0)
        {
            // Every stand failed: give them another chance later and leave the spot alone for a while.
            failedStands.RemoveWhere(x => x.Spot == s.Id);
            var broken = Rest(s, "no place to stand there worked", broken: true);
            log($"No place to stand at {SpotLabel(s)} worked; {AfterRest(broken)}.", true);
            Go(Step.Choose);
            return;
        }
        UseStand(s, stands[i]);
        TravelToStand(s);
    }

    private void UseStand(FishingSpotInfo s, (float X, float Y, float Z, float Facing) stand)
    {
        standPos = new Vector3(stand.X, stand.Y, stand.Z);
        standFacing = stand.Facing;
        standKey = Key(s.Id, standPos);
    }

    private void TravelToStand(FishingSpotInfo s)
    {
        standWalks = landPresses = landMoves = faceTries = jiggles = 0;
        lastJiggle = default;
        var target = standPos;
        // The exact point: the stand's height decides between a pier and the beach under it.
        travel.GoTo(SpotName(s), s.TerritoryId, Xz(target), SpotStands.StandRange, () => target);
        nextCheck = DateTime.UtcNow.AddSeconds(5);
        Go(Step.Travel);
    }

    private void TravelTick(IGameObject me)
    {
        travel.Tick();
        Status = travel.Status;
        if (travel.Current == Travel.State.Failed)
        {
            var why = travel.Failure ?? "the way there failed";
            if (Services.ClientState.TerritoryType != spot!.TerritoryId)
            {
                // Not even in the zone (the teleport failed): another stand won't help.
                var broken = Rest(spot, "couldn't get there", broken: true);
                log($"Couldn't get to {SpotLabel(spot)}: {why.TrimEnd('.')}; {AfterRest(broken)}.", true);
                Go(Step.Choose, 2);
                return;
            }
            StandFailed($"Couldn't get to the place to stand at {SpotName(spot)}: {why}");
            return;
        }
        if (travel.Current != Travel.State.Arrived)
        {
            if (DateTime.UtcNow >= nextCheck)
            {
                nextCheck = DateTime.UtcNow.AddSeconds(5);
                // The window closed on the way, or another fish's window needs the fisher first.
                if (!StillWorkable() || MoreUrgent() != null)
                {
                    travel.Cancel();
                    Vnav.Stop();
                    Go(Step.Choose);
                }
            }
            return;
        }
        landPresses = landMoves = standWalks = 0;
        Go(Step.Land);
    }

    /// <summary>This stand doesn't work (no way there, no landing, no cast): note it and take the next one.</summary>
    private void StandFailed(string why)
    {
        travel.Cancel();
        Vnav.Stop();
        if (standKey is { } k) failedStands.Add(k);
        standKey = null;
        log($"{why.TrimEnd('.')}. Trying another place to stand.", false);
        Settle(Step.Stand, 0.5);
    }

    /// <summary>On the mount: land and get off. On foot: walk onto the stand itself (a landing can slide off it).</summary>
    private void LandTick(IGameObject me)
    {
        var c = Services.Condition;
        if (c[ConditionFlag.InFlight] || c[ConditionFlag.Mounted]) { Land(me); return; }
        if (!SpotStands.At(me.Position, standPos))
        {
            if (Vnav.Busy && DateTime.UtcNow < walkUntil) { Status = "Walking onto the place to stand"; return; }
            Vnav.Stop();
            if (++standWalks > 3) { StandFailed($"Couldn't step onto the place to stand at {SpotName(spot!)}"); return; }
            Status = "Walking onto the place to stand";
            Vnav.MoveTo(standPos, c[ConditionFlag.Diving]);
            walkUntil = DateTime.UtcNow.AddSeconds(10);
            Wait(0.5);
            return;
        }
        Vnav.Stop();
        // Nobody casts while swimming.
        if (c[ConditionFlag.Swimming] || c[ConditionFlag.Diving]) { StandFailed($"The place to stand at {SpotName(spot!)} is in the water"); return; }
        faceTries = 0;
        Go(Step.Face);
    }

    /// <summary>
    /// Lands and gets off the mount (the gatherer's way): GeneralAction 23 up to eight times; after two presses
    /// in the air change nothing, fly down onto the ground at the stand, then below the player, and press again.
    /// </summary>
    private void Land(IGameObject me)
    {
        if (Vnav.Busy && DateTime.UtcNow < walkUntil) { Status = "Flying down to the ground"; return; }
        var inFlight = Services.Condition[ConditionFlag.InFlight];
        if (!inFlight || landPresses < 2)
        {
            if (++landPresses > 8) { StandFailed($"Couldn't get off the mount at {SpotName(spot!)}"); return; }
            Status = inFlight ? "Landing" : "Dismounting";
            var am = ActionManager.Instance();
            if (am != null) am->UseAction(ActionType.GeneralAction, LandOrDismount);
            Wait(1.2);
            return;
        }
        if (landMoves >= 2) { StandFailed($"Couldn't land at {SpotName(spot!)}"); return; }
        Vector3? ground = landMoves == 0 ? Vnav.NearestPoint(standPos, 2f, 5f) ?? standPos : Vnav.NearestPoint(me.Position, 5f, 30f);
        landMoves++;
        landPresses = 0;
        if (ground is { } g)
        {
            Status = "Flying down to the ground";
            Vnav.MoveTo(g, true);
            walkUntil = DateTime.UtcNow.AddSeconds(6);
        }
        Wait(0.5);
    }

    /// <summary>Turns to the facing the stand was cast with (client side, as GBR does), then lets it settle a second.</summary>
    private void FaceTick(IGameObject me)
    {
        if (SpotStands.Faces(me.Rotation, standFacing) || ++faceTries > 5)
        {
            if (faceTries > 5) Services.Log.Debug("Fisher: facing stayed at {Rotation} instead of {Facing}", me.Rotation, standFacing);
            Status = "Checking the cast";
            Go(Step.CastCheck, 1.0);
            return;
        }
        Status = "Facing the water";
        ((GameObjectStruct*)me.Address)->SetRotation(standFacing);
        Wait(0.2);
    }

    /// <summary>
    /// The cast works from here: fish. Otherwise step a yalm forward (onto the mesh) and check again, three
    /// times five seconds apart, then take the next stand (GBR's jiggle).
    /// </summary>
    private void CastCheckTick(IGameObject me)
    {
        if (current is not { } t || left(t) <= 0) { Go(Step.Choose); return; }
        var c = Services.Condition;
        if (c[ConditionFlag.InCombat]) { Status = "In combat; fishing after the fight"; Wait(1); return; }
        if (c[ConditionFlag.InFlight] || c[ConditionFlag.Mounted]) { landPresses = landMoves = 0; Go(Step.Land); return; }
        if (GameState.CurrentJob != FishAccess.Fisher) { Go(Step.Gear); return; }
        if (SetCollect(t.ItemId)) return;
        if (GameState.BagCount(bait) <= 0) { Go(Step.Bait); return; }
        if (Angler.CanCastHere())
        {
            StartSession();
            return;
        }
        if (jiggles >= 3) { StandFailed($"The line can't be cast from this place at {SpotName(spot!)}"); return; }
        var since = DateTime.UtcNow - lastJiggle;
        if (since < TimeSpan.FromSeconds(5))
        {
            Status = "Can't cast here; trying a step forward";
            Wait(5 - since.TotalSeconds);
            return;
        }
        jiggles++;
        lastJiggle = DateTime.UtcNow;
        // No mesh a yalm ahead counts as a try too.
        if (Vnav.NearestPoint(SpotStands.Ahead(me.Position, me.Rotation), 0.5f, 1f) is not { } ahead) { Wait(0.5); return; }
        standPos = ahead;
        Status = "Stepping forward to cast";
        Vnav.MoveTo(ahead, false);
        walkUntil = DateTime.UtcNow.AddSeconds(4);
        Go(Step.Jiggle, 0.3);
    }

    private void JiggleTick()
    {
        if (Vnav.Busy && DateTime.UtcNow < walkUntil) return;
        Vnav.Stop();
        faceTries = 0;
        Go(Step.Face);
    }

    // ------------------------------------------------------------------ the session

    private void StartSession()
    {
        var t = current!.Value;
        var s = spot!;
        CountSession();
        angler.Begin(new AnglerJob(fish!, s, Data.WeatherTable(s.TerritoryId), bait, Data.Fish, () => left(t), Settings.UseBuffs, Settings.UseCordials)
        {
            // The fish living here let the angler tell a missing predator apart and hook a tug only the target has.
            SpotFish = Data.FishAt(s.Id),
            // A predator whose own bait isn't in the bags may be fished with Versatile Lure only when the setting allows it.
            FallbackBait = Settings.VersatileLureFallback ? FishAccess.VersatileLure : 0,
        });
        sessionCounted = false;
        standLearned = false;
        switching = false;
        nextCheck = DateTime.UtcNow.AddSeconds(3);
        Status = $"Fishing for {Name(t.ItemId)}";
        Go(Step.Fish);
    }

    /// <summary>Every frame while the angler fishes; once its session ends (the rod is in), acts on why it ended.</summary>
    private void FishTick(IGameObject me)
    {
        if (angler.Failure is { } failure) { Fail(failure); return; }
        if (angler.Active)
        {
            if (angler.Status.Length > 0) Status = angler.Status;
            // The cast worked from here: remember the stand (this PC only).
            if (!standLearned && angler.Casts > 0) LearnStand(me);
            if (!pausing && !switching && DateTime.UtcNow >= nextCheck)
            {
                nextCheck = DateTime.UtcNow.AddSeconds(3);
                if (MoreUrgent() is { } why)
                {
                    switching = true;
                    log(why, false);
                    angler.RequestStop();
                }
            }
            return;
        }
        var signal = angler.Signal;
        if (signal == AnglerSignal.None)
        {
            // Not started yet; a session that never starts is something a retry won't fix.
            if (DateTime.UtcNow - stepSince > TimeSpan.FromSeconds(5)) Fail("The fishing didn't start; cast once by hand, then start again.");
            return;
        }
        CountSession();
        switching = false;
        if (signal != AnglerSignal.NoBait) noBaitStreak = 0;
        if (pausing) { PauseOnceIn(); return; }
        switch (signal)
        {
            case AnglerSignal.Wary:
                Wary();
                return;
            case AnglerSignal.Amiss:
                Amiss();
                return;
            case AnglerSignal.NoBait:
                // The bait that ran out can be a predator's (caught first for Fisher's Intuition), not the target's.
                var missing = angler.MissingBait != 0 ? angler.MissingBait : bait;
                // The game says there's no bait while the bags hold it (it can't be selected): retrying won't help.
                if (GameState.BagCount(missing) > 0 && ++noBaitStreak >= 3)
                {
                    Fail($"The game keeps saying there's no bait, though {Name(missing)} is in your bags. Select it once by hand, then start again.");
                    return;
                }
                // Reel in, then buy more (or fall back to Versatile Lure), then come back.
                Settle(Step.Bait);
                return;
            case AnglerSignal.CantCast:
                StandFailed($"The line can't be cast from this place at {SpotName(spot!)}");
                return;
            default:
                // Done, the window closed, or stopped for another fish: choose again.
                Settle(Step.Choose);
                return;
        }
    }

    private void LearnStand(IGameObject me)
    {
        standLearned = true;
        if (Data.LearnStand(spot!.Id, me.Position, me.Rotation)) Data.SaveLearnedStands();
    }

    /// <summary>
    /// "The fish have become wary": cast from a stand at least 10 yalms away (the nearest such; the farthest
    /// when none is that far). With no other stand that works, the hole is treated as amiss (they won't bite here).
    /// </summary>
    private void Wary()
    {
        var s = spot!;
        var stands = Data.StandsFor(s.Id);
        var positions = stands.Select(x => new Vector3(x.X, x.Y, x.Z)).ToList();
        var i = SpotStands.Relocate(positions, standPos, j => failedStands.Contains(Key(s.Id, positions[j])));
        jiggles = faceTries = 0;
        lastJiggle = default;
        if (i < 0)
        {
            log($"The fish at {SpotName(s)} turned wary, and there's no other place to stand there.", false);
            Amiss();
            return;
        }
        log($"The fish turned wary; moving to another place to stand {Vector3.Distance(positions[i], standPos):0} yalms away.", false);
        UseStand(s, stands[i]);
        Settle(Step.Stand);
    }

    /// <summary>
    /// "The fish sense something amiss": try another fishing hole for this fish that works now (this one is left
    /// alone for a while); with none, leave the area for a moment and come back (twice in a row at most, then
    /// this hole rests too).
    /// </summary>
    private void Amiss()
    {
        var s = spot!;
        var t = current!.Value;
        var now = DateTimeOffset.UtcNow;
        var here = Services.ClientState.TerritoryType;
        var lead = Lead(fish!);
        var other = Places(t.ItemId).Spots.Any(p => p.Spot.Id != s.Id && SpotChoice.Workable(Candidate(t.ItemId, fish!, p, now, here), lead));
        if (other || amissLeaves >= 2)
        {
            Rest(s, "the fish sensed something amiss");
            log(other
                ? $"The fish at {SpotLabel(s)} sense something amiss; trying another fishing hole."
                : $"The fish at {SpotLabel(s)} keep sensing something amiss; trying it again in {RestFor.TotalMinutes:0} minutes.", false);
            Settle(Step.Choose);
            return;
        }
        amissLeaves++;
        leaveTo = null;
        log($"The fish at {SpotLabel(s)} sense something amiss; leaving the area for a moment.", false);
        Settle(Step.Leave);
    }

    /// <summary>Teleports to another area (the cheapest aetheryte elsewhere), then lingers there a moment.</summary>
    private void LeaveTick()
    {
        if (leaveTo == null)
        {
            var here = Services.ClientState.TerritoryType;
            leaveTo = Teleporter.Unlocked().Where(d => d.TerritoryId != here && d.Estate == null).OrderBy(d => d.GilCost).FirstOrDefault();
            if (leaveTo == null)
            {
                // Nowhere to go: give the fish a longer moment right here instead (GBR pauses 30 seconds).
                lingerUntil = DateTime.UtcNow.AddSeconds(30);
                Go(Step.Linger);
                return;
            }
            var at = Teleporter.Position(leaveTo.AetheryteId) ?? Vector3.Zero;
            travel.GoTo(leaveTo.Name, leaveTo.TerritoryId, Xz(at), 1000f, null);
        }
        travel.Tick();
        Status = travel.Status;
        var arrived = Services.ClientState.TerritoryType == leaveTo.TerritoryId && travel.Current is not (Travel.State.Teleporting or Travel.State.Arriving);
        if (!arrived && travel.Current != Travel.State.Failed) return;
        if (!arrived) log($"Couldn't leave the area ({travel.Failure?.TrimEnd('.')}); waiting here a moment instead.", false);
        travel.Cancel();
        leaveTo = null;
        lingerUntil = DateTime.UtcNow.AddSeconds(arrived ? 10 : 30);
        Go(Step.Linger);
    }

    private void LingerTick()
    {
        var wait = lingerUntil - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            Status = $"Letting the fish settle ({Math.Ceiling(wait.TotalSeconds):0} s)";
            Wait(1);
            return;
        }
        Go(Step.Choose);
    }
}
