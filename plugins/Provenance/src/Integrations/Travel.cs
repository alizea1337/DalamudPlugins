using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Provenance.Integrations;

/// <summary>
/// Gets the character to a spot: teleport to the zone when needed, wait for vnavmesh's map, take the
/// city's aethernet when a shard is much closer to the target than walking, then walk.
/// Watches for getting stuck: no progress for a few seconds means retry, then teleport to the zone's
/// nearest aetheryte and retry, then give up with a clear message. Ticked on the framework thread.
/// </summary>
internal sealed unsafe class Travel
{
    public enum State { Idle, Teleporting, ToCrystal, Aethernet, Arriving, WaitingForMap, Walking, Arrived, Failed }

    private static readonly string[] AethernetEntry = { "Aethernet", "Ätheryten", "éthernet", "エーテライト" };

    /// <summary>From the Security tab: escalating unstuck steps, and how long without progress counts as stuck.</summary>
    public static bool AdvancedUnstuck { get; set; } = true;
    public static TimeSpan StuckAfter { get; set; } = TimeSpan.FromSeconds(6);
    private const uint Jump = 2;
    /// <summary>Further than this, mount up first (where the zone allows mounts).</summary>
    private const float RideFrom = 40f;
    private const uint MountRoulette = 9;

    private uint territory;
    private Vector2 approx;
    private float range;
    private Func<Vector3?>? exact;
    private string what = "";
    private DateTime deadline;
    private DateTime walkDeadline;
    private DateTime nextTry;
    private bool sawLoading;
    private int stuckCount;
    private Vector3? walkingTo;
    private readonly Queue<(DateTime At, Vector3 Pos)> trail = new();
    /// <summary>Areas without an aetheryte: teleport to the city's crystal, then take the aethernet.</summary>
    private Travel? crystalTrip;
    /// <summary>Aethernet hop under way: the crystal to use and the shard to go to.</summary>
    private uint hopFrom;
    private uint hopTo;
    /// <summary>Already hopped (or the area is only reached through the aethernet): walk the rest.</summary>
    private bool hopped;
    /// <summary>The area has no aetheryte of its own; a teleport can't restart the trip.</summary>
    private bool aethernetOnly;
    private int tries;
    private DateTime mountUntil;
    private bool flying;
    /// <summary>An unstuck move (backing off, side-stepping) is playing out until then.</summary>
    private DateTime unstuckUntil;
    private static readonly Random Rng = new();

    public State Current { get; private set; } = State.Idle;
    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Done => Current is State.Arrived or State.Failed;

    /// <param name="exactPosition">The real target once it's in view (an NPC or a bell); null until then.</param>
    public void GoTo(string description, uint territoryId, Vector2 approximateXz, float withinYalms, Func<Vector3?>? exactPosition)
    {
        what = description;
        territory = territoryId;
        approx = approximateXz;
        range = withinYalms;
        exact = exactPosition;
        stuckCount = 0;
        Failure = null;
        walkingTo = null;
        mountUntil = default;
        trail.Clear();
        walkDeadline = DateTime.UtcNow.AddMinutes(5);
        hopFrom = hopTo = 0;
        hopped = false;
        aethernetOnly = false;
        crystalTrip = null;
        if (Services.ClientState.TerritoryType == territory) { Enter(State.WaitingForMap); return; }
        if (Teleporter.NearestTo(territory, null) != null) { Enter(State.Teleporting); return; }
        if (Teleporter.AethernetRoute(territory) is not { } r) { Fail($"No unlocked aetheryte or aethernet shard leads to {Place(territory)}."); return; }
        aethernetOnly = true;
        hopped = true;
        StartHop(r.Main.TerritoryId, r.Main.AetheryteId, r.Shard);
    }

    /// <summary>Walk (or teleport) to a crystal, then take the aethernet to a shard.</summary>
    private void StartHop(uint crystalTerritory, uint from, uint to)
    {
        hopFrom = from;
        hopTo = to;
        var crystal = Teleporter.Position(from) ?? Vector3.Zero;
        crystalTrip = new Travel { hopped = true };
        crystalTrip.GoTo("the aethernet", crystalTerritory, new Vector2(crystal.X, crystal.Z), 6f,
            () => Interact.Nearest(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte, id => id == from)?.Position);
        crystalTrip.hopped = true; // the walk to the crystal itself never hops
        Enter(State.ToCrystal, 120);
    }

    public void Cancel()
    {
        if (Current == State.Walking) Vnav.Stop();
        crystalTrip?.Cancel();
        Current = State.Idle;
    }

    public void Tick()
    {
        if (Done || Current == State.Idle) return;
        var player = Services.Objects.LocalPlayer;
        switch (Current)
        {
            case State.Teleporting:
                if (DateTime.UtcNow < nextTry) return;
                // Something else (GatherBuddy Reborn) may have brought us here already.
                if (Services.ClientState.TerritoryType == territory && !Services.Condition[ConditionFlag.BetweenAreas] && !Services.Condition[ConditionFlag.Casting])
                {
                    Enter(State.WaitingForMap, 90);
                    return;
                }
                var dest = Teleporter.NearestTo(territory, new Vector3(approx.X, 0, approx.Y));
                if (dest == null) { Fail($"No unlocked aetheryte leads to {Place(territory)}."); return; }
                Status = $"Teleporting to {dest.Name}";
                if (Teleporter.Teleport(dest) is { } why)
                {
                    if (DateTime.UtcNow > deadline) { Fail($"Couldn't teleport to {dest.Name}: {why}."); return; }
                    nextTry = DateTime.UtcNow.AddSeconds(1);
                    return;
                }
                sawLoading = false;
                Enter(State.Arriving, 45);
                return;

            case State.ToCrystal:
                crystalTrip!.Tick();
                Status = crystalTrip.Status;
                if (crystalTrip.Current == State.Failed) { Fail(crystalTrip.Failure!); return; }
                if (crystalTrip.Current == State.Arrived) { tries = 0; Enter(State.Aethernet, 20); }
                return;

            case State.Aethernet:
                Status = "Taking the aethernet";
                if (DateTime.UtcNow < nextTry) return;
                if (Interact.Ready("TelepotTown") != null)
                {
                    if (Teleporter.TakeAethernet(hopTo)) { sawLoading = false; Enter(State.Arriving, 40); }
                    else nextTry = DateTime.UtcNow.AddSeconds(0.5);
                    return;
                }
                if (Interact.Menu() is { Count: > 0 } menu)
                {
                    if (!Interact.Choose(AethernetEntry)) menu[0].Select();
                    nextTry = DateTime.UtcNow.AddSeconds(1);
                    return;
                }
                if (DateTime.UtcNow > deadline || ++tries > 6) { Fail("The aethernet didn't open."); return; }
                if (Interact.Nearest(Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Aetheryte, id => id == hopFrom) is { } crystalObj)
                    Interact.With(crystalObj);
                nextTry = DateTime.UtcNow.AddSeconds(1.5);
                return;

            case State.Arriving:
                if (Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51]) { sawLoading = true; return; }
                if (Services.ClientState.TerritoryType == territory && player != null && sawLoading) { Enter(State.WaitingForMap, 90); return; }
                if (!sawLoading && !Services.Condition[ConditionFlag.Casting] && DateTime.UtcNow > deadline - TimeSpan.FromSeconds(30))
                {
                    // Nothing happened (cast interrupted, window closed); try again.
                    if (hopTo != 0) { tries = 0; Enter(State.Aethernet, 20); }
                    else Enter(State.Teleporting, 20);
                    return;
                }
                if (DateTime.UtcNow > deadline) Fail($"Didn't arrive in {Place(territory)}.");
                return;

            case State.WaitingForMap:
                Status = "Waiting for vnavmesh's map of this area";
                if (Vnav.Ready)
                {
                    // A shard much closer to the target than walking: take the aethernet first.
                    if (!hopped && player != null && Teleporter.AethernetHop(territory, player.Position, approx) is { } hop)
                    {
                        hopped = true;
                        StartHop(territory, hop.From, hop.To);
                        return;
                    }
                    Enter(State.Walking);
                    return;
                }
                if (DateTime.UtcNow > deadline) Fail("vnavmesh didn't finish its map of this area.");
                return;

            case State.Walking:
                Walk(player?.Position);
                return;
        }
    }

    private void Walk(Vector3? position)
    {
        if (position is not { } me) return;
        var target = exact?.Invoke() ?? Approximate(me);
        Status = "Walking to " + what;

        if (Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(target.X, target.Z)) <= range)
        {
            Vnav.Stop();
            Current = State.Arrived;
            Status = "Arrived at " + what;
            return;
        }
        if (DateTime.UtcNow > walkDeadline) { Vnav.Stop(); Fail($"Took too long to reach {what}."); return; }

        // Backing off or side-stepping to get unstuck: let it play out, then path again.
        if (unstuckUntil != default)
        {
            if (DateTime.UtcNow < unstuckUntil && Vnav.Busy) return;
            unstuckUntil = default;
            Vnav.Stop();
            walkingTo = null;
            trail.Clear();
        }

        // A long way in an open zone: mount up first.
        if (mountUntil != default)
        {
            if (!Services.Condition[ConditionFlag.Mounted] && DateTime.UtcNow < mountUntil) return;
            mountUntil = default;
        }
        else if (!Vnav.Busy && !Services.Condition[ConditionFlag.Mounted] && Vector3.Distance(me, target) > RideFrom && TryMount())
        {
            mountUntil = DateTime.UtcNow.AddSeconds(4);
            Status = "Mounting up";
            return;
        }

        // (Re)issue the walk when idle, or when the real target came into view somewhere else.
        var retarget = walkingTo is { } w && Vector3.Distance(w, target) > 3;
        if (!Vnav.Busy || retarget)
        {
            if (retarget) Vnav.Stop();
            flying = CanFlyHere();
            if (Vnav.MoveCloseTo(target, flying, Math.Max(1, range - 1))) walkingTo = target;
            trail.Clear();
            return;
        }
        if (flying) Status = "Flying to " + what;

        // Stuck: barely moved in the last few seconds while a path is running.
        var now = DateTime.UtcNow;
        trail.Enqueue((now, me));
        while (trail.Count > 0 && now - trail.Peek().At > StuckAfter) trail.Dequeue();
        if (trail.Count < 2 || now - trail.Peek().At < StuckAfter - TimeSpan.FromSeconds(1)) return;
        if (Vector3.Distance(trail.Peek().Pos, me) > 1.0f) return;

        stuckCount++;
        trail.Clear();
        Vnav.Stop();
        walkingTo = null;
        Services.Log.Information("Travel: stuck on the way to {What} ({Count})", what, stuckCount);
        if (AdvancedUnstuck && Unstuck(me, target)) return;
        if (stuckCount == 2 && !aethernetOnly && Teleporter.NearestTo(territory, null) != null)
        {
            // Second time: start over from the zone's aetheryte.
            Enter(State.Teleporting, 20);
            return;
        }
        if (stuckCount >= 3) Fail($"Got stuck on the way to {what}. vnavmesh can't find a way there from here; move a bit and start again.");
    }

    /// <summary>
    /// Escalating steps before the teleport: 1 path again, 2 jump and back off a few yalms, 3 side-step
    /// to a random walkable spot nearby. Later steps fall through to the teleport and then giving up.
    /// </summary>
    private bool Unstuck(Vector3 me, Vector3 target)
    {
        switch (stuckCount)
        {
            case 1:
                Status = "Stuck; finding another way";
                return true; // the next tick paths again
            case 2:
            {
                Status = "Stuck; jumping and backing off";
                var am = ActionManager.Instance();
                if (am != null && !Services.Condition[ConditionFlag.Mounted]) am->UseAction(ActionType.GeneralAction, Jump);
                var away = Vector3.Normalize(new Vector3(me.X - target.X, 0, me.Z - target.Z));
                if (float.IsNaN(away.X)) away = Vector3.UnitX;
                var back = Vnav.NearestPoint(me + away * 6f, 4, 10) ?? me + away * 6f;
                Vnav.MoveTo(back, flying);
                unstuckUntil = DateTime.UtcNow.AddSeconds(3);
                stuckCount = 2;
                return true;
            }
            case 3:
            {
                Status = "Stuck; side-stepping";
                var angle = Rng.NextDouble() * Math.PI * 2;
                var side = me + new Vector3((float)Math.Cos(angle), 0, (float)Math.Sin(angle)) * 12f;
                if (Vnav.NearestPoint(side, 6, 10) is { } spot) Vnav.MoveTo(spot, flying);
                unstuckUntil = DateTime.UtcNow.AddSeconds(4);
                return true;
            }
            default:
                // 4: teleport to the zone's aetheryte (when there is one), 5 and later: give up.
                if (stuckCount == 4 && !aethernetOnly && Teleporter.NearestTo(territory, null) != null)
                {
                    Enter(State.Teleporting, 20);
                    return true;
                }
                if (stuckCount >= 4)
                {
                    Fail($"Got stuck on the way to {what}, even after jumping, backing off, side-stepping and teleporting. Move a bit and start again.");
                    return true;
                }
                return false;
        }
    }

    /// <summary>The walkable point near the known X/Z (height from the mesh, near where the player is).</summary>
    private Vector3 Approximate(Vector3 me)
    {
        var guess = new Vector3(approx.X, me.Y, approx.Y);
        return Vnav.NearestPoint(guess, 5, 200) ?? Vnav.NearestPoint(guess, 40, 300) ?? guess;
    }

    /// <summary>Mount roulette, when the game allows it here. True when the cast started.</summary>
    private static bool TryMount()
    {
        var am = ActionManager.Instance();
        if (am == null || am->GetActionStatus(ActionType.GeneralAction, MountRoulette) != 0) return false;
        return am->UseAction(ActionType.GeneralAction, MountRoulette);
    }

    /// <summary>Mounted in a zone whose aether currents are all attuned.</summary>
    private static bool CanFlyHere()
    {
        if (Services.Condition[ConditionFlag.InFlight] || Services.Condition[ConditionFlag.Diving]) return true;
        if (!Services.Condition[ConditionFlag.Mounted]) return false;
        var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(Services.ClientState.TerritoryType);
        var set = row?.AetherCurrentCompFlgSet.RowId ?? 0;
        var ps = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
        return set != 0 && ps != null && ps->IsAetherCurrentZoneComplete(set);
    }

    private void Enter(State next, double seconds = 20)
    {
        Current = next;
        deadline = DateTime.UtcNow.AddSeconds(seconds);
        nextTry = default;
    }

    private void Fail(string message)
    {
        Failure = message;
        Status = message;
        Current = State.Failed;
    }

    private static string Place(uint territoryId)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "that area";
}
