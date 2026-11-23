using System;
using Dalamud.Game.ClientState.Objects.Enums;
using System.Collections.Generic;
using System.Linq;
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
    public enum State { Idle, Teleporting, ToCrystal, Aethernet, Arriving, WaitingForMap, Walking, Arrived, Failed, ToFirmament, FirmamentMenu }

    /// <summary>The Firmament: no aetheryte of its own; reached from Foundation's aetheryte ("Travel to the Firmament").</summary>
    public const uint Firmament = 886;
    private const uint Foundation = 418;
    private const uint FoundationAetheryte = 70;

    /// <summary>The aetheryte menu's aethernet entry, should the game's own text for it not be readable.</summary>
    private static readonly string[] AethernetEntry = { "Aethernet", "Ätherytennetz", "Réseau de transport urbain", "都市転送網" };
    /// <summary>How close to walk to a main aetheryte (big, with a wide base) and to an aethernet shard (small).</summary>
    private const float MainCrystalRange = 6f;
    /// <summary>On the way to the Firmament (a retry goes back to Foundation's aetheryte menu).</summary>
    private bool viaFoundation;

    /// <summary>
    /// The Firmament's aethernet: not in the game's aetheryte data; its shards open a menu of destinations named by
    /// place. Place names and positions (X, Z) are facts as Lifestream lists them.
    /// </summary>
    private static readonly (uint Place, Vector2 At)[] FirmamentShards =
    {
        (3436, new Vector2(23.9f, 169.4f)),    // The Mendicant's Court
        (3473, new Vector2(76.0f, 10.3f)),     // The Mattock
        (3475, new Vector2(149.5f, 98.6f)),    // The New Nest
        (3474, new Vector2(207.8f, -25.6f)),   // Saint Roelle's Dais
        (3525, new Vector2(-78.8f, 76.0f)),    // Featherfall
        (3528, new Vector2(-132.6f, -14.7f)),  // Hoarfrost Hall
        (3646, new Vector2(-91.7f, -115.2f)),  // Western Risensong Quarter
        (3645, new Vector2(114.3f, -107.4f)),  // Eastern Risensong Quarter
    };

    /// <summary>A hop on the Firmament's aethernet is under way (its shard menu, not the city aethernet window).</summary>
    private bool firmamentHop;
    private Vector2 firmamentFrom;
    private uint firmamentTo;
    private const float ShardRange = 3.5f;
    /// <summary>The aethernet window open this long without the destination in it: give up.</summary>
    private static readonly TimeSpan ListPatience = TimeSpan.FromSeconds(6);

    /// <summary>From the Security tab: escalating unstuck steps, and how long without progress counts as stuck.</summary>
    public static bool AdvancedUnstuck { get; set; } = true;
    public static TimeSpan StuckAfter { get; set; } = TimeSpan.FromSeconds(6);
    private const uint Jump = 2;
    /// <summary>Further than this, mount up first (where the zone allows mounts).</summary>
    private const float RideFrom = 40f;
    private const uint MountRoulette = 9;
    /// <summary>General action "Dismount" (from the air it lands first).</summary>
    private const uint Dismount = 23;
    /// <summary>How far above or below the target still counts as there (the game refuses to talk beyond a few yalms up or down).</summary>
    private const float MaxRise = 3f;
    private DateTime landingSince;
    private bool dismountTried;

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
    /// <summary>When the aethernet window was first seen open on this try.</summary>
    private DateTime listSince;
    /// <summary>What the aethernet step saw last (for the failure text).</summary>
    private string aethernetSaw = "";
    private DateTime mountUntil;
    private bool flying;
    /// <summary>An unstuck move (backing off, side-stepping) is playing out until then.</summary>
    private DateTime unstuckUntil;
    private static readonly Random Rng = new();

    /// <summary>The last walk or flight target written to the debug log (a new line only for a new target).</summary>
    private Vector3? lastPath;
    /// <summary>"No path yet" is written once per walk, not every frame vnavmesh says no.</summary>
    private bool pathFailNoted;

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
        viaFoundation = false;
        firmamentHop = false;
        landingSince = default;
        dismountTried = false;
        lastPath = null;
        pathFailNoted = false;
        Current = State.Idle;
        Note($"trip to {what} in {Place(territory)} (zone {territory}), within {range:0.#} yalms, from {Place(Services.ClientState.TerritoryType)} (zone {Services.ClientState.TerritoryType})");
        if (Services.ClientState.TerritoryType == territory) { Enter(State.WaitingForMap); return; }
        if (territory == Firmament) { StartFirmament(); return; }
        if (Teleporter.NearestTo(territory, null) != null) { Enter(State.Teleporting); return; }
        if (Teleporter.AethernetRoute(territory) is not { } r) { Fail($"No unlocked aetheryte or aethernet shard leads to {Place(territory)}."); return; }
        aethernetOnly = true;
        hopped = true;
        StartHop(r.Main.TerritoryId, r.Main.AetheryteId, r.Shard);
    }

    /// <summary>To the Firmament: to Foundation's aetheryte (teleporting there if need be), then its "Travel to the Firmament".</summary>
    private void StartFirmament()
    {
        viaFoundation = true;
        var crystal = Teleporter.Position(FoundationAetheryte) ?? Vector3.Zero;
        Note($"{what}: to the Firmament through Foundation's aetheryte");
        crystalTrip = new Travel { hopped = true };
        crystalTrip.GoTo("Foundation's aetheryte", Foundation, new Vector2(crystal.X, crystal.Z), MainCrystalRange, () => Interact.Crystal(FoundationAetheryte)?.Position);
        crystalTrip.hopped = true;
        Enter(State.ToFirmament, 180);
    }

    /// <summary>The menu entry of Foundation's aetheryte that goes to the Firmament: it names the place, in the client's language.</summary>
    private static string FirmamentName
    {
        get
        {
            var place = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(Firmament)?.PlaceName.ValueNullable;
            var name = place?.NameNoArticle.ExtractText() is { Length: > 0 } bare ? bare : place?.Name.ExtractText() ?? "Firmament";
            return name.StartsWith("the ", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
        }
    }

    /// <summary>Walk (or teleport) to a crystal, then take the aethernet to a shard.</summary>
    private void StartHop(uint crystalTerritory, uint from, uint to)
    {
        hopFrom = from;
        hopTo = to;
        Note($"{what}: taking the aethernet from {Teleporter.AethernetName(from)} to {Teleporter.AethernetName(to)}");
        var crystal = Teleporter.Position(from) ?? Vector3.Zero;
        crystalTrip = new Travel { hopped = true };
        crystalTrip.GoTo("the aethernet", crystalTerritory, new Vector2(crystal.X, crystal.Z),
            Teleporter.IsMainAetheryte(from) ? MainCrystalRange : ShardRange,
            () => Interact.Crystal(from)?.Position);
        crystalTrip.hopped = true; // the walk to the crystal itself never hops
        Enter(State.ToCrystal, 120);
    }

    public void Cancel()
    {
        if (Current is not (State.Idle or State.Arrived or State.Failed)) Note($"{what}: cancelled while {Current}");
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
                if (crystalTrip.Current == State.Arrived) { StartAethernet(); }
                return;

            case State.ToFirmament:
                crystalTrip!.Tick();
                Status = crystalTrip.Status;
                if (crystalTrip.Current == State.Failed) { Fail(crystalTrip.Failure!); return; }
                if (crystalTrip.Current == State.Arrived) { tries = 0; Enter(State.FirmamentMenu, 25); }
                return;

            case State.FirmamentMenu:
            {
                Status = "Travelling to the Firmament";
                if (DateTime.UtcNow < nextTry) return;
                if (Interact.Menu() is { Count: > 0 } options)
                {
                    var name = FirmamentName;
                    var entry = options.FindIndex(e => e.Text.Contains(name, StringComparison.CurrentCultureIgnoreCase));
                    if (entry < 0)
                    {
                        Note($"{what}: Foundation's aetheryte menu has no Firmament entry: {string.Join(" / ", options.Select(e => e.Text.Trim()))}");
                        Interact.Close("SelectString");
                        Fail("Foundation's aetheryte doesn't offer the Firmament (it opens once Ishgard's restoration has begun).");
                        return;
                    }
                    MoveIntent.Expect(40, "to the Firmament");
                    Note($"{what}: \"{options[entry].Text.Trim()}\"");
                    options[entry].Select();
                    sawLoading = false;
                    Enter(State.Arriving, 40);
                    return;
                }
                if (Interact.SkipTalk()) { nextTry = DateTime.UtcNow.AddSeconds(0.3); return; }
                if (DateTime.UtcNow > deadline || ++tries > 6) { Fail("Foundation's aetheryte didn't open its menu."); return; }
                if (Interact.Crystal(FoundationAetheryte) is { } crystal) Interact.With(crystal, travels: true);
                nextTry = DateTime.UtcNow.AddSeconds(1.5);
                return;
            }

            case State.Aethernet when firmamentHop:
                TickFirmamentAethernet();
                return;

            case State.Aethernet:
                Status = "Taking the aethernet";
                if (DateTime.UtcNow < nextTry) return;
                if (Interact.Ready("TelepotTown") != null)
                {
                    if (listSince == default) listSince = DateTime.UtcNow;
                    var pick = Teleporter.TakeAethernet(hopTo);
                    if (pick == Teleporter.AethernetPick.Taken) { sawLoading = false; Enter(State.Arriving, 40); return; }
                    if (pick != Teleporter.AethernetPick.NotReady && DateTime.UtcNow - listSince > ListPatience)
                    {
                        // The window is open but can't take us there: close it, so the character isn't left in it.
                        Interact.Close("TelepotTown");
                        var stop = Teleporter.AethernetName(hopTo);
                        Fail(pick == Teleporter.AethernetPick.Locked
                            ? $"The aethernet stop {stop} isn't attuned."
                            : $"The aethernet here doesn't list {stop}.");
                        return;
                    }
                    nextTry = DateTime.UtcNow.AddSeconds(0.5);
                    return;
                }
                listSince = default;
                if (Interact.Menu() is { Count: > 0 } menu)
                {
                    aethernetSaw = "a menu came up (" + string.Join(" / ", menu.Select(e => e.Text.Trim())) + "), but not the aethernet";
                    if (!ChooseAethernet(menu)) menu[0].Select();
                    nextTry = DateTime.UtcNow.AddSeconds(1);
                    return;
                }
                if (DateTime.UtcNow > deadline || ++tries > 6)
                {
                    var failure = $"The aethernet didn't open at {Teleporter.AethernetName(hopFrom)}: {aethernetSaw}.";
                    Services.Log.Warning("Travel: {Failure}", failure);
                    Fail(failure);
                    return;
                }
                if (Interact.Crystal(hopFrom) is { } crystalObj)
                {
                    var away = Interact.Distance(crystalObj);
                    aethernetSaw = Interact.With(crystalObj, travels: true)
                        ? $"talking to the crystal ({away:0.0} yalms away) opened nothing"
                        : $"the crystal ({away:0.0} yalms away) couldn't be talked to";
                }
                else aethernetSaw = "no aetheryte or aethernet shard was found there";
                nextTry = DateTime.UtcNow.AddSeconds(1.5);
                return;

            case State.Arriving:
                if (Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51]) { sawLoading = true; return; }
                if (Services.ClientState.TerritoryType == territory && player != null && sawLoading) { Enter(State.WaitingForMap, 90); return; }
                if (!sawLoading && !Services.Condition[ConditionFlag.Casting] && DateTime.UtcNow > deadline - TimeSpan.FromSeconds(30))
                {
                    // Nothing happened (cast interrupted, window closed); try again.
                    Note($"{what}: no loading screen came (cast interrupted or window closed); trying again");
                    if (viaFoundation && Services.ClientState.TerritoryType == Foundation) { tries = 0; Enter(State.FirmamentMenu, 25); }
                    else if (hopTo != 0) StartAethernet();
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
                    if (!hopped && player != null && territory == Firmament && FirmamentHop(player.Position) is { } fh)
                    {
                        hopped = true;
                        StartFirmamentHop(fh.From, fh.To);
                        return;
                    }
                    if (player != null)
                    {
                        var walk = Vector2.Distance(new Vector2(player.Position.X, player.Position.Z), approx);
                        if (walk > 80) Note($"{what}: walking {walk:0} yalms ({(hopped ? "after the aethernet" : "no aethernet hop is quicker here")})");
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
        var seen = exact?.Invoke();
        var target = seen ?? Approximate(me);
        Status = "Walking to " + what;

        var flat = Vector2.Distance(new Vector2(me.X, me.Z), new Vector2(target.X, target.Z));
        // Height matters once the real target (an NPC, a crystal) is in sight: above it on a roof or a mount is too far to talk.
        var rise = seen is { } at ? Math.Abs(me.Y - at.Y) : 0f;
        if (flat <= range && rise <= MaxRise)
        {
            Vnav.Stop();
            Note($"{what}: arrived, {flat:0.0} yalms from the target");
            Current = State.Arrived;
            Status = "Arrived at " + what;
            return;
        }
        if (flat <= range + 2f && rise > MaxRise)
        {
            if (landingSince == default)
            {
                landingSince = DateTime.UtcNow;
                Note($"{what}: right above or below the target ({rise:0} yalms); getting down to it");
                Vnav.Stop();
                Vnav.MoveTo(target, Services.Condition[ConditionFlag.InFlight] || Services.Condition[ConditionFlag.Diving]);
                walkingTo = target;
                return;
            }
            // Flying and still above it after a while: land (dismounting brings the mount down), then walk the rest.
            if (DateTime.UtcNow - landingSince > TimeSpan.FromSeconds(6) && Services.Condition[ConditionFlag.Mounted] && !dismountTried)
            {
                dismountTried = true;
                Note($"{what}: still {rise:0} yalms above or below; dismounting");
                Vnav.Stop();
                var am = ActionManager.Instance();
                if (am != null) am->UseAction(ActionType.GeneralAction, Dismount);
                return;
            }
            if (!Vnav.Busy && DateTime.UtcNow - landingSince > TimeSpan.FromSeconds(2))
            {
                Vnav.MoveTo(target, Services.Condition[ConditionFlag.InFlight] || Services.Condition[ConditionFlag.Diving]);
                walkingTo = target;
            }
            if (DateTime.UtcNow - landingSince > TimeSpan.FromSeconds(30)) { Vnav.Stop(); Fail($"Couldn't get down (or up) to {what}."); }
            return;
        }
        landingSince = default;
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
            Note($"{what}: mounting up, {Vector3.Distance(me, target):0} yalms to go");
            return;
        }

        // (Re)issue the walk when idle, or when the real target came into view somewhere else.
        var retarget = walkingTo is { } w && Vector3.Distance(w, target) > 3;
        if (!Vnav.Busy || retarget)
        {
            if (retarget) Vnav.Stop();
            flying = CanFlyHere();
            if (Vnav.MoveCloseTo(target, flying, Math.Max(1, range - 1)))
            {
                walkingTo = target;
                pathFailNoted = false;
                // A line per new target, not per re-issue.
                if (lastPath is not { } lp || Vector3.Distance(lp, target) > 3)
                    Note($"{what}: {(flying ? "flying" : "walking")} to {Where(target)}, {Vector3.Distance(me, target):0} yalms from {Where(me)}");
                lastPath = target;
            }
            else if (!pathFailNoted)
            {
                pathFailNoted = true;
                Note($"{what}: vnavmesh found no path to {Where(target)} from {Where(me)} yet");
            }
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
        Note($"{what}: stuck ({stuckCount}) at {Where(me)}, {Vector3.Distance(me, target):0} yalms to go");
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
                Note($"{what}: unstuck, finding another way");
                return true; // the next tick paths again
            case 2:
            {
                Status = "Stuck; jumping and backing off";
                Note($"{what}: unstuck, jumping and backing off");
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
                Note($"{what}: unstuck, side-stepping");
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

    /// <summary>At the crystal: talk to it and pick the stop.</summary>
    /// <summary>
    /// In the Firmament: the shard nearest to the character and the one nearest to the target, when walking to the
    /// first and from the second saves a good part of the walk (same rule as the city aethernet).
    /// </summary>
    private (Vector2 From, uint To)? FirmamentHop(Vector3 me)
    {
        var here = new Vector2(me.X, me.Z);
        var walk = Vector2.Distance(here, approx);
        var entry = FirmamentShards.OrderBy(sh => Vector2.Distance(here, sh.At)).First();
        var exit = FirmamentShards.OrderBy(sh => Vector2.Distance(sh.At, approx)).First();
        if (entry.Place == exit.Place) return null;
        var cost = Vector2.Distance(here, entry.At) + Vector2.Distance(exit.At, approx) + 60f;
        return cost < walk * 0.7f ? (entry.At, exit.Place) : null;
    }

    private void StartFirmamentHop(Vector2 from, uint to)
    {
        firmamentHop = true;
        firmamentFrom = from;
        firmamentTo = to;
        hopTo = to;
        Note($"{what}: taking the Firmament's aethernet to {PlaceText(to)}");
        crystalTrip = new Travel { hopped = true };
        crystalTrip.GoTo("the aethernet shard", Firmament, from, 4f, () => FirmamentShardObject(from)?.Position);
        crystalTrip.hopped = true;
        Enter(State.ToCrystal, 120);
    }

    /// <summary>The shard object standing at a Firmament aethernet spot, once it's in view.</summary>
    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? FirmamentShardObject(Vector2 at)
        => Services.Objects
            .Where(o => o.IsTargetable && o.ObjectKind is ObjectKind.Aetheryte or ObjectKind.EventObj
                        && Vector2.Distance(new Vector2(o.Position.X, o.Position.Z), at) < 8f)
            .OrderBy(o => Vector2.Distance(new Vector2(o.Position.X, o.Position.Z), at))
            .FirstOrDefault();

    private static string PlaceText(uint place)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.PlaceName>().GetRowOrDefault(place)?.Name.ExtractText() ?? "";

    /// <summary>At a Firmament shard: talk to it and pick the destination; a destination not in its menu (not attuned yet) means walking.</summary>
    private void TickFirmamentAethernet()
    {
        Status = "Taking the Firmament's aethernet";
        if (DateTime.UtcNow < nextTry) return;
        if (Interact.Ready("TelepotTown") != null)
        {
            if (listSince == default) listSince = DateTime.UtcNow;
            var pick = Teleporter.TakeAethernetPlace(firmamentTo);
            if (pick == Teleporter.AethernetPick.Taken) { sawLoading = false; Enter(State.Arriving, 40); return; }
            if (pick != Teleporter.AethernetPick.NotReady && DateTime.UtcNow - listSince > ListPatience)
            {
                Note($"{what}: the aethernet list {(pick == Teleporter.AethernetPick.Locked ? "has " + PlaceText(firmamentTo) + " locked (not attuned yet)" : "doesn't list " + PlaceText(firmamentTo))}; walking");
                Interact.Close("TelepotTown");
                firmamentHop = false;
                hopTo = 0;
                nextTry = DateTime.UtcNow.AddSeconds(1);
                Enter(State.Walking);
                return;
            }
            nextTry = DateTime.UtcNow.AddSeconds(0.5);
            return;
        }
        if (Interact.IsOpen("TelepotTown")) { nextTry = DateTime.UtcNow.AddSeconds(0.3); return; }
        if (Interact.Menu() is { Count: > 0 } options)
        {
            var name = PlaceText(firmamentTo);
            var entry = name.Length == 0 ? -1 : options.FindIndex(e => e.Text.Contains(name, StringComparison.CurrentCultureIgnoreCase));
            if (entry < 0)
            {
                Note($"{what}: the shard's menu has no {name} (not attuned yet?): {string.Join(" / ", options.Select(e => e.Text.Trim()))}; walking");
                Interact.Close("SelectString");
                firmamentHop = false;
                hopTo = 0;
                Enter(State.Walking);
                return;
            }
            MoveIntent.Expect(30, "aethernet to " + name);
            Note($"{what}: \"{options[entry].Text.Trim()}\"");
            options[entry].Select();
            sawLoading = false;
            Enter(State.Arriving, 40);
            return;
        }
        if (Interact.SkipTalk()) { nextTry = DateTime.UtcNow.AddSeconds(0.3); return; }
        if (DateTime.UtcNow > deadline || ++tries > 6)
        {
            Note($"{what}: the Firmament shard didn't open its menu; walking");
            Interact.Close("TelepotTown");
            Interact.Close("SelectString");
            firmamentHop = false;
            hopTo = 0;
            Enter(State.Walking);
            return;
        }
        if (FirmamentShardObject(firmamentFrom) is { } shard) Interact.With(shard, travels: true);
        listSince = default;
        nextTry = DateTime.UtcNow.AddSeconds(2);
    }

    private void StartAethernet()
    {
        tries = 0;
        listSince = default;
        aethernetSaw = "nothing happened";
        Enter(State.Aethernet, 20);
    }

    /// <summary>
    /// Picks the city's aethernet in a main aetheryte's menu. "Residential District Aethernet." holds the same word,
    /// so of the entries that match, the shortest is the city's own.
    /// </summary>
    private static bool ChooseAethernet(List<(string Text, Action Select)> menu)
    {
        var labels = AethernetEntry.Prepend(Teleporter.AethernetMenuText).Where(l => l.Length > 0).ToList();
        var hits = menu.Where(e => labels.Any(l => e.Text.Contains(l, StringComparison.OrdinalIgnoreCase))).OrderBy(e => e.Text.Trim().Length).ToList();
        if (hits.Count == 0) return false;
        hits[0].Select();
        return true;
    }

    private void Enter(State next, double seconds = 20)
    {
        if (next != Current) Note($"{what}: {Current} -> {next}");
        if (next != State.Walking) pathFailNoted = false;
        Current = next;
        deadline = DateTime.UtcNow.AddSeconds(seconds);
        nextTry = default;
    }

    private void Fail(string message)
    {
        Note($"[!] {what}: {message}");
        Failure = message;
        Status = message;
        Current = State.Failed;
    }

    private static void Note(string text) => DebugLog.Write("travel", text);

    private static string Where(Vector3 p) => $"{p.X:0},{p.Y:0},{p.Z:0}";

    private static string Place(uint territoryId)
        => Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(territoryId)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "that area";
}
