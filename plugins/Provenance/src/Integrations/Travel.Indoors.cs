using System;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using Provenance.Core.Run;

namespace Provenance.Integrations;

/// <summary>
/// Indoors, under roofs and on raised floors. Flying straight at a target under a roof lands on the roof or outside the
/// walls, and getting off in the air drops the character straight down (into the water below a platform). So:
/// <list type="bullet">
/// <item>a walk that starts under a roof goes out on foot first, and mounts up only in the open;</item>
/// <item>a target under a roof is never flown at: the character comes down on an open spot of the walkable mesh near it
/// (<see cref="LandingSearch"/>), gets off on the ground and walks in, and vnavmesh finds the door or the stairs;</item>
/// <item>getting off in the air happens only right above the spot; beside the target, the character comes down on the
/// floor at it first (also when it arrives in the air with no floor right below at the target's height).</item>
/// </list>
/// Trips to targets in the open behave as before. A roof is something solid straight above, found with the game's own
/// collision raycast (<c>BGCollisionModule.RaycastMaterialFilter</c>, FFXIVClientStructs, MIT): once per trip for the
/// target, once per walk for the start, every few seconds while walking out, and once per point of the landing search
/// (a ring per frame).
/// </summary>
internal sealed unsafe partial class Travel
{
    private enum Descent { None, Down, GettingOff }

    /// <summary>Walking out of a building on foot: at most this long, looking for the open sky this often.</summary>
    private static readonly TimeSpan WalkOutLimit = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan OutLookEvery = TimeSpan.FromSeconds(1.5);
    /// <summary>Coming down on a floor point: at most this long, and paths there at most this often.</summary>
    private static readonly TimeSpan DescentPatience = TimeSpan.FromSeconds(20);
    private const int DescentPaths = 4;
    /// <summary>Getting off right above the floor point again after this long, when the first didn't take.</summary>
    private static readonly TimeSpan DropAgainAfter = TimeSpan.FromSeconds(3);
    /// <summary>How close to the open spot near a covered target to fly, and within how far of it the coming down starts.</summary>
    private const float LandingSpotWithin = 1f;
    private const float LandingSpotReach = 2.5f;
    /// <summary>No open spot near a covered target: the flight comes down once it's this close (on the ground).</summary>
    private const float NoSpotLandWithin = 30f;
    /// <summary>Arriving in the air beside the target: a floor right below within this much of the target's height is its floor.</summary>
    private const float FloorBelowWithin = 1.5f;

    // The trip: the target's roof (once its real position is in view) and the open spot near it.
    private bool? roofOverTarget;
    private Vector3 roofFor;
    private LandingSearch? landingSearch;
    private bool floorLooked;
    private Vector3 floorFor;
    private Vector3? floorAtTarget;

    // The walk (from the start of each, after a teleport too).
    private bool startLooked;
    private bool walkingOut;
    private DateTime walkOutUntil;
    private DateTime nextOutLook;
    /// <summary>Down on the ground near the target (or close by on foot): the rest on foot, no mount, no flying.</summary>
    private bool onFootRest;
    /// <summary>Coming down didn't work: from here on as before (never getting off in the air beside the target, though).</summary>
    private bool indoorsOff;
    private bool landFirstLooked;
    private bool dropRefusedNoted;
    private Descent descent;
    private Vector3 descentTo;
    private DateTime descentSince;
    private int descentPaths;
    private DateTime dropAt;
    private bool getOffRetried;

    private void ResetIndoors(bool trip)
    {
        if (trip)
        {
            roofOverTarget = null;
            landingSearch = null;
            floorLooked = false;
            floorAtTarget = null;
        }
        startLooked = false;
        walkingOut = false;
        onFootRest = false;
        indoorsOff = false;
        landFirstLooked = false;
        dropRefusedNoted = false;
        descent = Descent.None;
    }

    /// <summary>These rules apply: not carrying something on foot, not on a quest mount, not under water, and coming down hasn't failed.</summary>
    private bool IndoorsApplies => !OnFoot && !KeepMount && !NoFly && !indoorsOff && !Services.Condition[ConditionFlag.Diving];

    /// <summary>Walking out of a building, or the rest of the way on foot after coming down: no mount, no flying.</summary>
    private bool StayOnFoot => walkingOut || onFootRest;

    /// <summary>Mounted, on the way to a target under a roof (it's reached by the open spot near it, then on foot).</summary>
    private bool HeadingForLanding => IndoorsApplies && roofOverTarget == true && !onFootRest && Services.Condition[ConditionFlag.Mounted];

    /// <summary>Where the path goes instead of the target: the open spot near a covered target while mounted; null otherwise.</summary>
    private Vector3? LandingSpot => HeadingForLanding && landingSearch is { Done: true, Found: { } spot } ? spot : null;

    /// <summary>In the air and not right above the spot: getting off here would drop the character beside it.</summary>
    private static bool InAirBeside(Vector3 me, Vector3 spot)
        => Services.Condition[ConditionFlag.InFlight] && !Landing.RightAboveIt(me, spot);

    /// <summary>Something solid straight above the spot (a roof, a ceiling, a floor above), looking up from above head height.</summary>
    private static bool RoofOver(Vector3 at)
        => BGCollisionModule.RaycastMaterialFilter(at + new Vector3(0, Landing.RoofLookFrom, 0), Vector3.UnitY, out _, Landing.RoofLookUp);

    /// <summary>Whether a roof is over the target: looked at when its real position is first in view (again only when it moved far).</summary>
    private void LookForRoof(Vector3 real)
    {
        if (!IndoorsApplies || roofOverTarget != null && Vector3.Distance(roofFor, real) <= 5f) return;
        roofFor = real;
        roofOverTarget = RoofOver(real);
        landingSearch = null;
        Note($"{what}: {(roofOverTarget == true ? "a roof over the target" : "the target is in the open")} at {Where(real)}");
    }

    /// <summary>
    /// Out of a building on foot first; a target under a roof by the open spot near it (searched a ring per frame), or,
    /// riding on the ground or on foot close to it, straight in on foot. True when it took the tick.
    /// </summary>
    private bool Indoors(Vector3 me, Vector3 target)
    {
        if (!IndoorsApplies) return false;
        var now = DateTime.UtcNow;

        // Starting under a roof: walk out on foot, and mount up only in the open (or once there).
        if (!startLooked)
        {
            startLooked = true;
            if (!Services.Condition[ConditionFlag.InFlight] && RoofOver(me))
            {
                walkingOut = true;
                walkOutUntil = now + WalkOutLimit;
                nextOutLook = now + OutLookEvery;
                Note($"{what}: starting indoors (a roof over {Where(me)}): walking out first, no mount");
            }
        }
        if (walkingOut)
        {
            if (now < nextOutLook) return false;
            nextOutLook = now + OutLookEvery;
            var open = !RoofOver(me);
            if (!open && now < walkOutUntil) return false;
            walkingOut = false;
            Note(open
                ? $"{what}: out in the open at {Where(me)}; on as usual"
                : $"{what}: still under a roof after {WalkOutLimit.TotalSeconds:0} seconds on foot, at {Where(me)}; on as usual");
            // The walk out stops, so the mount-up (when it's far) comes next.
            Vnav.Stop();
            walkingTo = null;
            return true;
        }

        if (roofOverTarget != true || onFootRest) return false;
        var mounted = Services.Condition[ConditionFlag.Mounted];
        var away = Vector3.Distance(me, target);

        // Close by on the ground (riding or on foot): straight in on foot; vnavmesh finds the door.
        if (!Services.Condition[ConditionFlag.InFlight] && away <= RideFrom)
        {
            if (mounted)
            {
                StartDescent(me, $"the target is under a roof {away:0} yalms away; getting off here and walking in");
                return true;
            }
            onFootRest = true;
            Note($"{what}: the target is under a roof {away:0} yalms away; walking in on foot");
            return false;
        }
        // Far on foot: the mount-up comes first, then the open spot near it.
        if (!mounted) return false;

        // Around the target's real position as the roof check saw it (a new check, when it moved far, starts a new search).
        landingSearch ??= new LandingSearch(roofFor);
        if (!landingSearch.Done)
        {
            landingSearch.Step(p => Vnav.NearestPoint(p, 3f, 10f), RoofOver);
            if (landingSearch.Done)
                Note(landingSearch.Found is { } f
                    ? $"{what}: the target is under a roof: landing {Vector3.Distance(f, roofFor):0} yalms away at {Where(f)}, then walking in"
                      + $" ({landingSearch.Rings} rings, {landingSearch.Lookups} mesh lookups, {landingSearch.RoofChecks} roof checks)"
                    : $"{what}: the target is under a roof and no open spot within {Landing.Radii[^1]:0} yalms ({landingSearch.Covered} covered, {landingSearch.OffLevel} on another level);"
                      + $" flying as close as it goes, then coming down there and walking in");
            return true;
        }

        if (landingSearch.Found is { } spot)
        {
            // On the way the path goes to the spot (LandingSpot); there, come down and get off.
            if (Landing.Flat(me, spot) > LandingSpotReach) return false;
            StartDescent(spot, $"at the open spot {Where(spot)}; coming down, then walking in");
            return true;
        }

        // No open spot near it: fly on as usual and come down where the flight gets close or ends; riding on the ground
        // goes in on the ground.
        if (!Services.Condition[ConditionFlag.InFlight]) return false;
        var ended = walkingTo != null && !Vnav.Busy;
        if (Landing.Flat(me, target) > NoSpotLandWithin && !ended) return false;
        if (FloorBelow(me, target) is { } below)
        {
            StartDescent(below, $"{(ended ? "the flight ended" : "close to the target")} {Landing.Flat(me, target):0} yalms from it; coming down at {Where(below)}, then walking in");
            return true;
        }
        indoorsOff = true;
        Note($"{what}: no floor found below {Where(me)} to come down on; going on as before");
        return false;
    }

    /// <summary>
    /// Arriving in the air beside the target (not right above it): getting off drops straight down, so when there's no
    /// floor right below at the target's height (a platform's edge, the water below), the floor at the target is come
    /// down on first. Looked at once per walk. True when it started that.
    /// </summary>
    private bool LandFirst(Vector3 me, Vector3 target, Vector3? seen)
    {
        if (seen == null || landFirstLooked || !IndoorsApplies || !InAirBeside(me, target)) return false;
        landFirstLooked = true;
        if (Vnav.NearestPoint(new Vector3(me.X, target.Y, me.Z), 0.5f, FloorBelowWithin) != null) return false;
        if (FloorAt(target) is not { } floor) return false;
        StartDescent(floor, $"arriving in the air {Landing.Flat(me, target):0.0} yalms beside the target with no floor right below at its height; coming down on the floor at it, {Where(floor)}, first");
        return true;
    }

    /// <summary>
    /// The floor right at the target: the walkable point nearest it (2 yalms around, 3 up or down), else the nearest point
    /// of the first ring at its height. Looked for once per target.
    /// </summary>
    private Vector3? FloorAt(Vector3 target)
    {
        if (floorLooked && Vector3.Distance(floorFor, target) < 1f) return floorAtTarget;
        floorLooked = true;
        floorFor = target;
        floorAtTarget = Vnav.NearestPoint(target, 2f, 3f);
        if (floorAtTarget == null)
        {
            Vector3? best = null;
            foreach (var p in Landing.Ring(target, Landing.Radii[0]))
                if (Vnav.NearestPoint(p, 3f, 1.5f) is { } q && MathF.Abs(q.Y - target.Y) <= 1.5f
                    && (best is not { } b || Vector3.Distance(q, target) < Vector3.Distance(b, target)))
                    best = q;
            floorAtTarget = best;
        }
        Note($"{what}: the floor at the target: {(floorAtTarget is { } at ? Where(at) : "none found")}");
        return floorAtTarget;
    }

    /// <summary>The floor below the character: at the target's height first (not the roof over it), else whatever is below.</summary>
    private static Vector3? FloorBelow(Vector3 me, Vector3 target)
        => Vnav.NearestPoint(new Vector3(me.X, target.Y, me.Z), 4f, Landing.MaxHeightOff) ?? Vnav.NearestPoint(me, 4f, 80f);

    /// <summary>
    /// Comes down on a floor point and gets off there: flying, the path goes down to it (getting off in the air only
    /// right above it); on the ground, getting off right away. Then the rest is on foot.
    /// </summary>
    private void StartDescent(Vector3 at, string why)
    {
        Note($"{what}: {why}");
        Vnav.Stop();
        walkingTo = null;
        trail.Clear();
        landingSince = default;
        descentTo = at;
        descentSince = DateTime.UtcNow;
        descentPaths = 0;
        dropAt = default;
        getOffRetried = false;
        if (Services.Condition[ConditionFlag.InFlight])
        {
            descent = Descent.Down;
            descentPaths++;
            Vnav.MoveTo(at, true);
        }
        else if (Services.Condition[ConditionFlag.Mounted])
        {
            descent = Descent.GettingOff;
            GetOff();
        }
        else EndDescent(at);
    }

    /// <summary>Coming down and getting off. True while it takes the tick.</summary>
    private bool TickDescent(Vector3 me)
    {
        var now = DateTime.UtcNow;
        Status = "Coming down near " + what;
        switch (descent)
        {
            case Descent.Down:
                if (!Services.Condition[ConditionFlag.InFlight])
                {
                    Vnav.Stop();
                    if (!Services.Condition[ConditionFlag.Mounted]) { EndDescent(me); return true; }
                    Note($"{what}: down on the ground at {Where(me)}; getting off");
                    descent = Descent.GettingOff;
                    descentSince = now;
                    // Getting off in the air right above the spot finishes by itself once down.
                    if (dropAt == default) GetOff();
                    return true;
                }
                if (now - descentSince > DescentPatience)
                {
                    Note($"[!] {what}: couldn't come down at {Where(descentTo)} within {DescentPatience.TotalSeconds:0} seconds (still {me.Y - descentTo.Y:0} yalms up); going on as before");
                    descent = Descent.None;
                    indoorsOff = true;
                    Vnav.Stop();
                    walkingTo = null;
                    return true;
                }
                if (Vnav.Busy) return true;
                // vnavmesh stopped short of touching down: right above the spot, getting off brings the mount straight
                // down onto it.
                if (Landing.RightAboveIt(me, descentTo))
                {
                    if (dropAt == default || now - dropAt > DropAgainAfter)
                    {
                        Note($"{what}: right above {Where(descentTo)}, {me.Y - descentTo.Y:0.#} yalms up; getting off (the mount comes straight down onto it)");
                        dropAt = now;
                        GetOff();
                    }
                    return true;
                }
                if (descentPaths < DescentPaths)
                {
                    descentPaths++;
                    Vnav.MoveTo(descentTo, true);
                }
                return true;

            case Descent.GettingOff:
                if (!Services.Condition[ConditionFlag.Mounted]) { EndDescent(me); return true; }
                if (now - descentSince < TimeSpan.FromSeconds(3)) return true;
                if (!getOffRetried)
                {
                    getOffRetried = true;
                    descentSince = now;
                    GetOff();
                    return true;
                }
                Note($"{what}: still mounted after getting off twice; going on mounted, on the ground");
                EndDescent(me);
                return true;
        }
        return false;
    }

    private void EndDescent(Vector3 me)
    {
        descent = Descent.None;
        onFootRest = true;
        walkingTo = null;
        trail.Clear();
        landingSince = default;
        Note($"{what}: on the ground at {Where(me)}; the rest on foot");
    }

    /// <summary>General action "Dismount", when the game allows it now (from the air the mount comes straight down first).</summary>
    private static void GetOff()
    {
        var am = ActionManager.Instance();
        if (am != null && am->GetActionStatus(ActionType.GeneralAction, Dismount) == 0) am->UseAction(ActionType.GeneralAction, Dismount);
    }
}
