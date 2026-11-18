using System;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Provenance.Data;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>
/// After a finished order: back to where the run started, into an inn room (talk to the innkeeper),
/// or to a house or apartment from the teleport list. Ticked on the framework thread.
/// </summary>
internal sealed unsafe class Homecoming
{
    private enum Step { Idle, Travel, Talk, Teleport, Arriving, Done, Failed }

    // "Retire to an inn room." and its translations; the menu entry that leads into the room.
    private static readonly string[] InnEntry = { "inn room", "Retire", "Gästezimmer", "Zimmer", "chambre", "宿屋", "部屋" };

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly Travel travel = new();
    private Step step = Step.Idle;
    private GameCatalog.Inn? inn;
    private Teleporter.Destination? home;
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

    /// <summary>Starts the trip. False when there is nowhere to go (Stay, or nothing picked).</summary>
    public bool Begin(AfterOrderSettings settings, (uint Territory, Vector3 Position)? start)
    {
        Failure = null;
        inn = null;
        home = null;
        tries = 0;
        menuTry = 0;
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
                travel.GoTo("where the run started", s.Territory, new Vector2(s.Position.X, s.Position.Z), 4f, null);
                step = Step.Travel;
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

            default:
                return false;
        }
    }

    private void StartInn()
    {
        var i = inn!;
        arriveIn = i.RoomTerritory;
        travel.GoTo($"{i.NpcName} in {i.City}", i.TerritoryId, i.Position, 3.5f,
            () => Interact.Nearest(ObjectKind.EventNpc, id => id == i.NpcId)?.Position);
        step = Step.Travel;
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
                if (inn == null) { Done("Back where the run started."); return; }
                step = Step.Talk;
                deadline = DateTime.UtcNow.AddSeconds(20);
                return;

            case Step.Talk:
                Talk();
                return;

            case Step.Teleport:
                Status = "Teleporting home";
                if (Services.ClientState.TerritoryType == arriveIn) { Done($"Home: {home!.Estate}."); return; }
                if (Teleporter.Teleport(home!) is { } why)
                {
                    if (DateTime.UtcNow > deadline) { Fail($"Couldn't teleport home: {why}."); return; }
                    Wait(1);
                    return;
                }
                sawLoading = false;
                step = Step.Arriving;
                deadline = DateTime.UtcNow.AddSeconds(45);
                return;

            case Step.Arriving:
                if (Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51]) { sawLoading = true; return; }
                if (Services.ClientState.TerritoryType == arriveIn && Services.Objects.LocalPlayer != null)
                {
                    Done(inn != null ? $"Resting at the inn in {inn.City}." : $"Home: {home!.Estate}.");
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

    /// <summary>Talk to the innkeeper, pick the inn room, confirm.</summary>
    private void Talk()
    {
        var i = inn!;
        Status = "Asking " + i.NpcName + " for a room";
        if (Services.Condition[ConditionFlag.BetweenAreas] || Services.Condition[ConditionFlag.BetweenAreas51]) { sawLoading = true; step = Step.Arriving; deadline = DateTime.UtcNow.AddSeconds(45); return; }
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
