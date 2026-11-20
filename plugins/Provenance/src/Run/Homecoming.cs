using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>
/// Trips the run makes for you: after a finished order, back to where it started, into an inn room (talk to the
/// innkeeper), to a house or apartment from the teleport list, or to the crafting spot; and before crafting, to
/// the crafting spot (through the door of a house or apartment when it's inside one). Ticked on the framework thread.
/// </summary>
internal sealed unsafe class Homecoming
{
    private enum Step { Idle, Travel, Talk, Teleport, Arriving, Next, FindDoor, Door, Done, Failed }

    /// <summary>What a finished walk leads to.</summary>
    private enum Then { Done, Talk, Door }

    // "Retire to an inn room." and its translations; the menu entry that leads into the room.
    private static readonly string[] InnEntry = { "inn room", "Retire", "Gästezimmer", "Zimmer", "chambre", "宿屋", "部屋" };

    /// <summary>Legs of a trip to the crafting spot before it counts as going round in circles.</summary>
    private const int MaxLegs = 6;

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly Travel travel = new();
    private Step step = Step.Idle;
    private Then then;
    private GameCatalog.Inn? inn;
    private Teleporter.Destination? home;
    private CraftingSpotSettings? spot;
    private bool teleported;
    private int legs;
    private uint arriveIn;
    private bool sawLoading;
    private DateTime deadline;
    private DateTime waitUntil;
    private int tries;
    private int menuTry;

    public Homecoming(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Finished => step is Step.Done or Step.Failed;

    private void Reset()
    {
        travel.Cancel();
        Failure = null;
        inn = null;
        home = null;
        spot = null;
        teleported = false;
        legs = 0;
        tries = 0;
        menuTry = 0;
        waitUntil = default;
    }

    /// <summary>Starts the trip after an order. False when there is nowhere to go (Stay, or nothing picked).</summary>
    public bool Begin(AfterOrderSettings settings, (uint Territory, Vector3 Position)? start)
    {
        Reset();
        var catalog = plugin.Plans.Catalog!;
        var territories = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>();
        switch (settings.Place)
        {
            case AfterOrderPlace.WhereStarted when start is { } s:
                // Started in an inn room: go back into that inn.
                if (territories.GetRowOrDefault(s.Territory)?.TerritoryIntendedUse.RowId == 2)
                {
                    inn = catalog.Inns.FirstOrDefault(i => i.RoomTerritory == s.Territory);
                    if (inn == null) return false;
                    StartInn();
                    return true;
                }
                if (!Teleporter.CanReach(s.Territory))
                {
                    log("The run started somewhere it can't travel back to (a house or a duty); staying here.", true);
                    return false;
                }
                Walk("where the run started", s.Territory, new Vector2(s.Position.X, s.Position.Z), 4f, Then.Done);
                return true;

            case AfterOrderPlace.Inn:
                inn = catalog.Inns.FirstOrDefault(i => i.NpcId == settings.InnNpc);
                if (inn == null) { log("No inn picked in Settings; staying here.", true); return false; }
                if (Services.ClientState.TerritoryType == inn.RoomTerritory) return false;
                StartInn();
                return true;

            case AfterOrderPlace.Home:
                home = Teleporter.Homes().FirstOrDefault(d => d.AetheryteId == settings.HomeAetheryte && d.SubIndex == settings.HomeSubIndex);
                if (home == null) { log("The home picked in Settings isn't in your teleport list; staying here.", true); return false; }
                arriveIn = home.TerritoryId;
                step = Step.Teleport;
                deadline = DateTime.UtcNow.AddSeconds(30);
                return true;

            case AfterOrderPlace.CraftingSpot:
                if (!plugin.Config.CraftingSpot.IsSet) { log("No crafting spot saved (Settings, Crafting); staying here.", true); return false; }
                return BeginSpot(plugin.Config.CraftingSpot);

            default:
                return false;
        }
    }

    /// <summary>
    /// Starts the trip to the crafting spot. False when already there. A failure shows in <see cref="Failure"/>
    /// once <see cref="Finished"/>.
    /// </summary>
    public bool BeginSpot(CraftingSpotSettings s)
    {
        Reset();
        if (!s.IsSet || CraftingSpots.IsAt(s)) return false;
        spot = s;
        NextLeg();
        return true;
    }

    /// <summary>
    /// The next leg toward the crafting spot, from wherever the character is now: a walk when it's already in the
    /// right zone (and house); else the inn, the estate's door, or a teleport to the estate first.
    /// </summary>
    private void NextLeg()
    {
        var s = spot!;
        if (++legs > MaxLegs) { Fail("Couldn't get there: every way in led somewhere else."); return; }
        if (CraftingSpots.InPlace(s) || s.Kind == CraftingSpotKind.OpenWorld)
        {
            if (s.Kind == CraftingSpotKind.OpenWorld && !Teleporter.CanReach(s.Territory)) { Fail("No unlocked aetheryte leads there any more."); return; }
            Walk("the crafting spot", s.Territory, new Vector2(s.X, s.Z), 1.5f, Then.Done);
            return;
        }
        switch (s.Kind)
        {
            case CraftingSpotKind.InnRoom:
                inn = plugin.Plans.Catalog?.Inns.FirstOrDefault(i => i.RoomTerritory == s.Territory);
                if (inn == null) { Fail("The inn of the saved room isn't known."); return; }
                StartInn();
                return;

            default:
                // A house or an apartment: through its door, from outside in its ward; else teleport there first.
                if (CraftingSpots.OutsideInWard(s) && CraftingSpots.Door(s) is { } door)
                {
                    Walk("the entrance", s.District, new Vector2(door.Position.X, door.Position.Z), 2.5f, Then.Door, () => CraftingSpots.Door(s)?.Position);
                    return;
                }
                if (teleported)
                {
                    // Landed at the estate, but no door in sight yet: they appear once the area has loaded.
                    step = Step.FindDoor;
                    deadline = DateTime.UtcNow.AddSeconds(15);
                    return;
                }
                home = Teleporter.Homes().FirstOrDefault(d => d.AetheryteId == s.Aetheryte && d.SubIndex == s.SubIndex);
                if (home == null) { Fail("It isn't in your teleport list any more."); return; }
                teleported = true;
                arriveIn = home.TerritoryId;
                step = Step.Teleport;
                deadline = DateTime.UtcNow.AddSeconds(30);
                return;
        }
    }

    private void Walk(string what, uint territory, Vector2 xz, float within, Then next, Func<Vector3?>? exact = null)
    {
        travel.GoTo(what, territory, xz, within, exact);
        then = next;
        step = Step.Travel;
    }

    private void StartInn()
    {
        var i = inn!;
        arriveIn = i.RoomTerritory;
        Walk($"{i.NpcName} in {i.City}", i.TerritoryId, i.Position, 3.5f, Then.Talk,
            () => Interact.Nearest(ObjectKind.EventNpc, id => id == i.NpcId)?.Position);
    }

    public void Cancel()
    {
        travel.Cancel();
        if (!Finished) step = Step.Idle;
    }

    public void Tick()
    {
        if (Finished || step == Step.Idle || DateTime.UtcNow < waitUntil) return;
        switch (step)
        {
            case Step.Travel:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed) { Fail(travel.Failure!); return; }
                if (travel.Current != Travel.State.Arrived) return;
                switch (then)
                {
                    case Then.Talk:
                        step = Step.Talk;
                        deadline = DateTime.UtcNow.AddSeconds(20);
                        return;
                    case Then.Door:
                        step = Step.Door;
                        tries = 0;
                        menuTry = 0;
                        deadline = DateTime.UtcNow.AddSeconds(20);
                        return;
                    default:
                        Done(spot != null ? $"At the crafting spot ({spot.Label})." : "Back where the run started.");
                        return;
                }

            case Step.Talk:
                Talk();
                return;

            case Step.Door:
                Door();
                return;

            case Step.Next:
                NextLeg();
                return;

            case Step.FindDoor:
                Status = "Looking for the entrance";
                if (CraftingSpots.OutsideInWard(spot!) && CraftingSpots.Door(spot!) is not null) { NextLeg(); return; }
                if (DateTime.UtcNow > deadline) { Fail(spot!.Kind == CraftingSpotKind.Apartment ? "Couldn't find the apartment building's entrance." : "Couldn't find the house's entrance."); return; }
                Wait(0.5);
                return;

            case Step.Teleport:
                Status = spot != null ? "Teleporting to " + (home!.Estate ?? home.Name) : "Teleporting home";
                // Already in the zone: done for a trip home; the crafting spot still needs its ward and plot.
                if (spot == null && Services.ClientState.TerritoryType == arriveIn) { Arrived(); return; }
                if (Teleporter.Teleport(home!) is { } why)
                {
                    if (DateTime.UtcNow > deadline) { Fail($"Couldn't teleport {(spot != null ? "there" : "home")}: {why}."); return; }
                    Wait(1);
                    return;
                }
                sawLoading = false;
                step = Step.Arriving;
                deadline = DateTime.UtcNow.AddSeconds(45);
                return;

            case Step.Arriving:
                if (Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51]) { sawLoading = true; return; }
                // To the crafting spot the zone may not change (a teleport within the housing district), so only a
                // loading screen counts as arriving there.
                if (Services.ClientState.TerritoryType == arriveIn && Services.Objects.LocalPlayer != null && (sawLoading || spot == null))
                {
                    Arrived();
                    return;
                }
                if (!sawLoading && home != null && !Services.Condition[ConditionFlag.Casting] && DateTime.UtcNow > deadline - TimeSpan.FromSeconds(35))
                {
                    step = Step.Teleport; // the cast didn't happen; try again
                    deadline = DateTime.UtcNow.AddSeconds(20);
                    return;
                }
                if (DateTime.UtcNow > deadline) Fail("Didn't arrive.");
                return;
        }
    }

    /// <summary>In the zone the last teleport or door led to: done, or on to the next leg once the area has settled.</summary>
    private void Arrived()
    {
        if (spot != null)
        {
            step = Step.Next;
            Wait(1.5);
            return;
        }
        Done(inn != null ? $"Resting at the inn in {inn.City}." : $"Home: {home!.Estate}.");
    }

    /// <summary>Talk to the innkeeper, pick the inn room, confirm.</summary>
    private void Talk()
    {
        var i = inn!;
        Status = "Asking " + i.NpcName + " for a room";
        if (Loading()) return;
        if (Interact.Yes()) { Wait(1); return; }
        if (Interact.Menu() is { Count: > 0 } menu)
        {
            if (menuTry == 0 && Interact.Choose(InnEntry)) { menuTry = 1; Wait(1); return; }
            // No match: the first entry is the room on most innkeepers.
            var index = Math.Max(0, menuTry - 1);
            if (index >= menu.Count - 1) { Fail($"Couldn't find the inn room in {i.NpcName}'s menu."); return; }
            menu[index].Select();
            menuTry = index + 2;
            Wait(1);
            return;
        }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline || ++tries > 8) { Fail($"{i.NpcName} didn't let you in."); return; }
        if (Interact.Nearest(ObjectKind.EventNpc, id => id == i.NpcId) is { } npc) Interact.With(npc);
        Wait(1.5);
    }

    /// <summary>Through the door: "Enter the estate hall?" for a house, "Go to your apartment" for an apartment building.</summary>
    private void Door()
    {
        var s = spot!;
        Status = "Going inside";
        arriveIn = s.Territory;
        if (Loading()) return;
        if (Interact.Yes()) { Wait(1); return; }
        if (Interact.Menu() is { Count: > 0 })
        {
            if (Interact.Choose(CraftingSpots.ApartmentEntry())) { Wait(1); return; }
            Fail("The entrance didn't offer \"Go to your apartment\".");
            return;
        }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline || ++tries > 8) { Fail("The door didn't open (no access to this house any more?)."); return; }
        if (CraftingSpots.Door(s) is { } door) Interact.With(door);
        Wait(1.5);
    }

    /// <summary>A loading screen started (into the inn room or the house): wait for the arrival.</summary>
    private bool Loading()
    {
        if (!Services.Condition[ConditionFlag.BetweenAreas] && !Services.Condition[ConditionFlag.BetweenAreas51]) return false;
        sawLoading = true;
        step = Step.Arriving;
        deadline = DateTime.UtcNow.AddSeconds(45);
        return true;
    }

    private void Done(string message)
    {
        Status = message;
        step = Step.Done;
        log(message, false);
    }

    private void Fail(string message)
    {
        Failure = message;
        Status = message;
        step = Step.Failed;
        travel.Cancel();
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
