using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Provenance.Core.Run;
using Provenance.Core.Tribes;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run.Tribes;

/// <summary>
/// Gets the character to a spot of an allied-society quest, through the portals the trip needs: into a sub-zone
/// (Zirgorteh the Open-armed, Elysion) and back out, and across a zone to the parts only a portal reaches (A-4 Research
/// for the Omicrons, Hoper's Hold for the Loporrits). Each leg is an ordinary <see cref="Travel"/> (teleport, aethernet,
/// walk or fly with vnavmesh); at a portal it talks to the holder and answers the warp's own question. Ticked on the
/// framework thread.
/// </summary>
/// <remarks>
/// Which portal and where it stands come from the shipped portals.json (tools/QuestPortals, the game's layout files);
/// the question is the warp's <c>Warp.Question</c> in the client's language, the only one answered Yes here.
/// </remarks>
internal sealed class QuestTravel
{
    public enum State { Idle, Travelling, ToPortal, AtPortal, Arrived, Failed }

    /// <summary>Legs in one trip at most (a portal, the walk on, another portal ...).</summary>
    private const int MaxLegs = 6;
    /// <summary>Talks to a portal before it counts as not working.</summary>
    private const int PortalTries = 3;

    /// <summary>Quest NPCs stand indoors and on raised floors: the buildings rules are on for every Tribes trip.</summary>
    private readonly Travel travel = new() { RoofAware = true };
    private readonly HashSet<(uint, uint)> broken = new();
    private string what = "";
    private uint territory;
    private Vector3 target;
    private float within;
    /// <summary>The target's height is known (a Level row, a spawn point, an object): the trip goes for that height.</summary>
    private bool heightKnown;
    private Func<Vector3?>? exact;
    private PortalLeg? leg;
    private int legs;
    private int tries;
    private DateTime nextTry;
    private DateTime deadline;
    private bool sawLoading;

    public State Current { get; private set; } = State.Idle;
    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Done => Current is State.Arrived or State.Failed;

    /// <summary>Walk only (carrying something): no mount, no flying, no teleport.</summary>
    public bool OnFoot { get => travel.OnFoot; set => travel.OnFoot = value; }
    /// <summary>Riding a quest mount that may not fly.</summary>
    public bool NoFly { get => travel.NoFly; set => travel.NoFly = value; }
    /// <summary>Riding a quest mount: arrive by the distance on the ground, never dismount on the way.</summary>
    public bool KeepMount { get => travel.KeepMount; set => travel.KeepMount = value; }
    /// <summary>A trip to an enemy: stuck a second time it fails (the enemy is left) instead of starting over from the aetheryte.</summary>
    public bool FailFast { get => travel.FailFast; set => travel.FailFast = value; }

    /// <summary>The question the portal being taken asks (answered Yes), while at it; null otherwise.</summary>
    public string? ExpectedQuestion => Current == State.AtPortal && leg != null ? Portals.Question(leg.Portal.Warp) : null;

    /// <summary>On the way through a portal (the character jumps; the Security check expects it).</summary>
    public bool AtPortal => Current == State.AtPortal;

    /// <param name="exactPosition">The real target once it's in view; null until then.</param>
    /// <param name="knownHeight">
    /// <paramref name="at"/>'s height is the target's (a Level row, a spawn point, an object's position); false when
    /// it's only a guess (a gathering area from the map), and the player's own height is gone by as before.
    /// </param>
    public void GoTo(string description, uint territoryId, Vector3 at, float withinYalms, Func<Vector3?>? exactPosition, bool knownHeight = true)
    {
        what = description;
        territory = territoryId;
        target = at;
        within = withinYalms;
        heightKnown = knownHeight;
        exact = exactPosition;
        legs = 0;
        broken.Clear();
        Failure = null;
        Plan();
    }

    public void Cancel()
    {
        travel.Cancel();
        if (Current is not (State.Idle or State.Arrived or State.Failed)) Note($"{what}: cancelled while {Current}");
        Current = State.Idle;
    }

    public void Tick()
    {
        switch (Current)
        {
            case State.Travelling:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed) { Fail(travel.Failure ?? "the trip failed"); return; }
                if (travel.Current == Travel.State.Arrived) Plan(afterTrip: true);
                return;

            case State.ToPortal:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed) { Fail("couldn't get to the portal: " + travel.Failure); return; }
                if (travel.Current != Travel.State.Arrived) return;
                tries = 0;
                sawLoading = false;
                nextTry = default;
                deadline = DateTime.UtcNow.AddSeconds(60);
                Current = State.AtPortal;
                return;

            case State.AtPortal:
                TickPortal();
                return;
        }
    }

    /// <summary>The next leg from where the character is: done, a portal first, or an ordinary trip.</summary>
    /// <param name="afterTrip">The ordinary trip just arrived (it already came down where it had to).</param>
    private void Plan(bool afterTrip = false)
    {
        var me = Services.Objects.LocalPlayer?.Position;
        var here = Services.ClientState.TerritoryType;
        if (me == null) { Fail("no character"); return; }
        var seen = exact?.Invoke();
        var spot = seen ?? target;
        // Up on a ledge or down below the real target isn't there (the game won't talk that far up or down). In the air
        // beside it isn't either: getting off there would drop the character beside it, so the trip comes down first.
        // A spot whose height is known, not in view, is there only within Travel's reach of that height (at the foot
        // of the cliff a spawn point stands on isn't).
        // After the trip, its own arrival stands (it went by the walkable point at that height, which can lie off the
        // Level row's own height); before it, the same point is compared against.
        var level = KeepMount || (seen != null ? Math.Abs(me.Value.Y - spot.Y) <= 3f
            : afterTrip || !heightKnown || Math.Abs(me.Value.Y - Travel.FloorNear(spot).Y) <= Math.Max(Travel.SpotRise, within));
        var inAirBeside = !afterTrip && seen != null && !KeepMount && Services.Condition[ConditionFlag.InFlight] && !Landing.RightAboveIt(me.Value, spot);
        if (here == territory && Flat(me.Value, spot) <= within + 0.5f && level && !inAirBeside)
        {
            Current = State.Arrived;
            Status = "Arrived at " + what;
            return;
        }
        if (++legs > MaxLegs) { Fail($"couldn't find the way to {what} (too many legs)."); return; }

        leg = PortalRoute.First(here, me.Value, territory, spot, Portals.List, CanTeleport, broken);
        // Reaching the portal itself can need another one first (Elysion's portal stands in A-4 Research).
        if (leg != null && leg.Portal.Territory == here
            && PortalRoute.First(here, me.Value, here, leg.Portal.At, Portals.List, CanTeleport, broken) is { } inner && inner.Portal != leg.Portal)
            leg = inner;

        if (leg == null)
        {
            if (legs > 1 && here == territory) Note($"{what}: on from here ({Flat(me.Value, spot):0} yalms)");
            travel.GoTo(what, territory, new Vector2(spot.X, spot.Z), within, exact, heightKnown || seen != null ? spot.Y : null);
            Current = State.Travelling;
            return;
        }
        var p = leg.Portal;
        Note($"{what}: through the portal {p.Holder} in zone {p.Territory} (\"{Portals.Question(p.Warp)}\", to zone {p.To})");
        travel.GoTo("the portal", p.Territory, new Vector2(p.At.X, p.At.Z), 3f, () => Holder(p)?.Position);
        Current = State.ToPortal;
    }

    /// <summary>At the portal: talk to it, answer its question, and go on once the character has moved through.</summary>
    private void TickPortal()
    {
        var p = leg!.Portal;
        Status = "Taking the portal";
        if (Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51]) { sawLoading = true; return; }
        var me = Services.Objects.LocalPlayer?.Position;
        var here = Services.ClientState.TerritoryType;
        if (me != null && here == p.To && (p.To != p.Territory || sawLoading || Vector3.Distance(me.Value, p.At) > 20f))
        {
            Note($"{what}: through the portal, now in zone {here} at {me.Value.X:0},{me.Value.Y:0},{me.Value.Z:0}");
            Plan();
            return;
        }
        // However the portal's windows behave (a menu while scene tasks are pending, a game that stays busy), it gets
        // this long.
        if (DateTime.UtcNow > deadline)
        {
            GiveUpOnPortal(p, "didn't take the character in time");
            return;
        }
        if (Interact.SkipTalk()) { nextTry = DateTime.UtcNow.AddSeconds(0.5); return; }
        if (QuestSteps.Question() is { } question)
        {
            if (StepRules.KnownQuestion(question, new[] { Portals.Question(p.Warp) }))
            {
                if (QuestSteps.Yes()) Note($"{what}: \"{question}\" -> Yes");
                MoveIntent.Expect(30, "a portal");
                nextTry = DateTime.UtcNow.AddSeconds(2);
                return;
            }
            Fail($"the portal asked \"{question}\", which isn't its own question; answer it yourself.");
            return;
        }
        if (QuestSteps.Menu() is { } menu)
        {
            if (DeliveryWindow.SceneBusy) return;
            var name = StepRules.Normal(Portals.MenuName(p.Warp));
            var question2 = StepRules.Normal(Portals.Question(p.Warp)).TrimEnd('?');
            var at = menu.Entries.FindIndex(e => StepRules.Normal(e) is var n && n.Length > 0
                                                  && (name.Length > 0 && n.Equals(name, StringComparison.CurrentCultureIgnoreCase)
                                                      || question2.Length > 0 && n.TrimEnd('.', '?').Equals(question2, StringComparison.CurrentCultureIgnoreCase)));
            if (at < 0) { Fail($"the portal's menu has no entry for it ({string.Join(" / ", menu.Entries.Select(StepRules.Normal))}); go through yourself."); return; }
            Note($"{what}: menu \"{StepRules.Normal(menu.Entries[at])}\"");
            menu.Select(at);
            nextTry = DateTime.UtcNow.AddSeconds(1);
            return;
        }
        if (DateTime.UtcNow < nextTry || QuestSteps.Busy) return;
        if (++tries > PortalTries)
        {
            GiveUpOnPortal(p, "didn't take the character");
            return;
        }
        if (Holder(p) is not { } obj) { nextTry = DateTime.UtcNow.AddSeconds(1); return; }
        Interact.With(obj, travels: true);
        nextTry = DateTime.UtcNow.AddSeconds(3);
    }

    /// <summary>The portal doesn't work this trip: another way (or, with none, the ordinary trip, which says why it can't).</summary>
    private void GiveUpOnPortal(Portal p, string why)
    {
        Note($"[!] {what}: the portal {p.Holder} {why}; trying another way");
        broken.Add((p.Holder, p.Warp));
        Plan();
    }

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? Holder(Portal p)
        => Interact.Nearest(p.Npc ? ObjectKind.EventNpc : ObjectKind.EventObj, id => id == p.Holder);

    private static bool CanTeleport(uint zone) => Teleporter.NearestTo(zone, null) != null;

    private static float Flat(Vector3 a, Vector3 b) => Vector2.Distance(new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));

    private void Fail(string message)
    {
        Note($"[!] {what}: {message}");
        travel.Cancel();
        Failure = message;
        Status = message;
        Current = State.Failed;
    }

    private static void Note(string text) => DebugLog.Write("steps", "travel: " + text);
}
