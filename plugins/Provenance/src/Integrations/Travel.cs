using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;

namespace Provenance.Integrations;

/// <summary>
/// Gets the character to a spot: teleport to the zone when needed, wait for vnavmesh's map, walk.
/// Watches for getting stuck: no progress for a few seconds means retry, then teleport to the zone's
/// nearest aetheryte and retry, then give up with a clear message. Ticked on the framework thread.
/// </summary>
internal sealed class Travel
{
    public enum State { Idle, Teleporting, Arriving, WaitingForMap, Walking, Arrived, Failed }

    private static readonly TimeSpan StuckAfter = TimeSpan.FromSeconds(6);

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
        trail.Clear();
        walkDeadline = DateTime.UtcNow.AddMinutes(5);
        Enter(Services.ClientState.TerritoryType == territory ? State.WaitingForMap : State.Teleporting);
    }

    public void Cancel()
    {
        if (Current == State.Walking) Vnav.Stop();
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

            case State.Arriving:
                if (Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51]) { sawLoading = true; return; }
                if (Services.ClientState.TerritoryType == territory && player != null && sawLoading) { Enter(State.WaitingForMap, 90); return; }
                if (!sawLoading && !Services.Condition[ConditionFlag.Casting] && DateTime.UtcNow > deadline - TimeSpan.FromSeconds(30))
                {
                    // The cast never started (interrupted); try again.
                    Enter(State.Teleporting, 20);
                    return;
                }
                if (DateTime.UtcNow > deadline) Fail($"Didn't arrive in {Place(territory)}.");
                return;

            case State.WaitingForMap:
                Status = "Waiting for vnavmesh's map of this area";
                if (Vnav.Ready) { Enter(State.Walking); return; }
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

        // (Re)issue the walk when idle, or when the real target came into view somewhere else.
        var retarget = walkingTo is { } w && Vector3.Distance(w, target) > 3;
        if (!Vnav.Busy || retarget)
        {
            if (retarget) Vnav.Stop();
            if (Vnav.MoveCloseTo(target, false, Math.Max(1, range - 1))) walkingTo = target;
            trail.Clear();
            return;
        }

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
        if (stuckCount == 2)
        {
            // Second time: start over from the zone's aetheryte.
            Enter(State.Teleporting, 20);
            return;
        }
        if (stuckCount >= 3) Fail($"Got stuck on the way to {what}. vnavmesh can't find a way there from here; move a bit and start again.");
    }

    /// <summary>The walkable point near the known X/Z (height from the mesh, near where the player is).</summary>
    private Vector3 Approximate(Vector3 me)
    {
        var guess = new Vector3(approx.X, me.Y, approx.Y);
        return Vnav.NearestPoint(guess, 5, 200) ?? guess;
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
