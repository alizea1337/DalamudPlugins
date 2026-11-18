using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Memory;
using ECommons.Automation;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>
/// Turns collectables in for scrips at a collectable appraiser: travel there, talk, then in the
/// appraiser window pick the job tab, the item's row and submit, one collectable at a time. A yes/no
/// prompt right after submitting means the scrips are capped: answer no and stop. The window
/// callbacks follow GatherBuddy Reborn's collectable turn-in (Apache-2.0). Ticked on the framework thread.
/// </summary>
internal sealed unsafe class Appraiser
{
    private enum Step { Idle, Travel, Talk, Menu, Window, Closing, Done, Failed }
    private const string Window = "CollectablesShop";

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly Travel travel = new();
    private readonly List<uint> items = new();
    private (uint NpcId, string Name, uint TerritoryId, System.Numerics.Vector2 Position) npc;
    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime deadline;
    private DateTime submittedAt;
    private int talkTries;
    private int menuTry;
    private int turnedIn;
    private int tries;
    /// <summary>0: pick the tab, 1: pick the row, 2: submit.</summary>
    private int phase;

    public Appraiser(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Finished => step is Step.Done or Step.Failed;
    public bool Capped { get; private set; }

    /// <summary>Turns in every collectable of these items that is in the bags.</summary>
    public void Begin(IEnumerable<uint> collectables)
    {
        items.Clear();
        items.AddRange(collectables.Where(i => GameState.BagCount(i) > 0));
        Failure = null;
        Capped = false;
        turnedIn = 0;
        if (items.Count == 0) { step = Step.Done; return; }
        var catalog = plugin.Plans.Catalog!;
        var reachable = catalog.Appraisers.Where(a => Teleporter.CanReach(a.TerritoryId)).ToList();
        if (reachable.Count == 0) { Fail("No collectable appraiser you can teleport to is known."); return; }
        // Here first, then a city whose aetheryte costs the least.
        var unlocked = Teleporter.Unlocked();
        npc = reachable.OrderBy(a => a.TerritoryId == Services.ClientState.TerritoryType ? 0 : 1)
            .ThenBy(a => unlocked.Where(d => d.TerritoryId == a.TerritoryId).Select(d => (long)d.GilCost).DefaultIfEmpty(100_000).Min())
            .First();
        var place = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.TerritoryType>().GetRowOrDefault(npc.TerritoryId)?.PlaceName.ValueNullable?.Name.ExtractText() ?? "";
        travel.GoTo($"the collectable appraiser in {place}", npc.TerritoryId, npc.Position, 3.5f,
            () => Interact.Nearest(ObjectKind.EventNpc, id => id == npc.NpcId)?.Position);
        talkTries = 0;
        menuTry = 0;
        phase = 0;
        tries = 0;
        step = Step.Travel;
    }

    public void Cancel()
    {
        travel.Cancel();
        Interact.Close(Window);
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
                if (travel.Current == Travel.State.Arrived) { step = Step.Talk; deadline = DateTime.UtcNow.AddSeconds(15); }
                return;
            case Step.Talk: Talk(); return;
            case Step.Menu: Menu(); return;
            case Step.Window: Trade(); return;
            case Step.Closing:
                if (Interact.Ready(Window) is var open && open != null) Callback.Fire(open, true, -1);
                Interact.Close(Window);
                step = Step.Done;
                Status = turnedIn > 0 ? $"Turned in {turnedIn} collectables" : "Nothing to turn in";
                return;
        }
    }

    private void Talk()
    {
        if (Interact.Ready(Window) != null) { step = Step.Window; deadline = DateTime.UtcNow.AddSeconds(60); return; }
        if (Interact.Menu() != null) { step = Step.Menu; return; }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline) { Fail($"The {npc.Name} didn't answer."); return; }
        var target = Interact.Nearest(ObjectKind.EventNpc, id => id == npc.NpcId);
        if (target == null) { Fail($"Couldn't find the {npc.Name} here."); return; }
        Status = "Talking to the " + npc.Name;
        if (++talkTries > 6) { Fail($"Couldn't talk to the {npc.Name}."); return; }
        Interact.With(target);
        Wait(1);
    }

    private void Menu()
    {
        if (Interact.Ready(Window) != null) { step = Step.Window; deadline = DateTime.UtcNow.AddSeconds(60); return; }
        var menu = Interact.Menu();
        if (menu == null) { step = Step.Talk; Wait(0.3); return; }
        if (menuTry == 0 && Interact.Choose(new[] { "collectable", "Sammlerstück", "collector", "収集品" })) { menuTry = 1; Wait(0.6); return; }
        var index = Math.Max(0, menuTry - 1);
        if (index >= menu.Count - 1) { Fail($"Couldn't open the appraiser's window at the {npc.Name}."); return; }
        menu[index].Select();
        menuTry = index + 2;
        Wait(0.6);
    }

    private void Trade()
    {
        var catalog = plugin.Plans.Catalog!;
        // A yes/no right after submitting: scrips are capped.
        if (Interact.Ready("SelectYesno") is var yesno && yesno != null)
        {
            if (DateTime.UtcNow - submittedAt < TimeSpan.FromSeconds(2))
            {
                Callback.Fire(yesno, true, 1);
                Capped = true;
                log("Your scrips are at their cap; stopped turning in.", true);
                step = Step.Closing;
                Wait(0.5);
                return;
            }
            Interact.Yes();
            Wait(0.5);
            return;
        }
        var window = Interact.Ready(Window);
        if (window == null)
        {
            if (DateTime.UtcNow > deadline) { Fail("The appraiser's window closed."); return; }
            step = Step.Talk;
            deadline = DateTime.UtcNow.AddSeconds(15);
            return;
        }

        items.RemoveAll(i => GameState.BagCount(i) == 0);
        if (items.Count == 0) { step = Step.Closing; return; }
        var item = items[0];
        var option = catalog.Collectable(item);
        var name = catalog.ItemName(item);
        Status = $"Turning in {name} ({GameState.BagCount(item)} left)";
        if (option == null) { items.RemoveAt(0); return; }

        switch (phase)
        {
            case 0:
                Callback.Fire(window, false, 14, (uint)option.Tab);
                phase = 1;
                Wait(0.6);
                return;
            case 1:
                var row = RowOf(window, name);
                if (row < 0)
                {
                    if (++tries > 4) { log($"The appraiser doesn't list {name}; turn it in yourself.", true); items.RemoveAt(0); tries = 0; phase = 0; return; }
                    phase = 0;
                    Wait(0.8);
                    return;
                }
                Callback.Fire(window, false, 12, (uint)row);
                phase = 2;
                Wait(0.6);
                return;
            default:
                var before = GameState.BagCount(item);
                Callback.Fire(window, true, 15, 0u);
                submittedAt = DateTime.UtcNow;
                turnedIn++;
                phase = before > 1 ? 1 : 0;
                tries = 0;
                Wait(1.0);
                return;
        }
    }

    /// <summary>Row of an item in the appraiser's list (the tree list, headers skipped), matched by name.</summary>
    private static int RowOf(AtkUnitBase* window, string name)
    {
        AtkComponentTreeList* list = null;
        for (var i = 0; i < window->UldManager.NodeListCount; i++)
        {
            var node = window->UldManager.NodeList[i];
            if (node == null || (int)node->Type != 1028 || node->NodeId != 28) continue;
            var component = node->GetAsAtkComponentNode();
            if (component != null && component->Component != null) list = (AtkComponentTreeList*)component->Component;
            break;
        }
        if (list == null) return -1;
        var row = 0;
        foreach (var entry in list->Items)
        {
            var item = entry.Value;
            if (item == null) continue;
            if (item->Type is TreeListItemType.Group or TreeListItemType.SectionHeader) continue;
            if (item->StringValues.LongCount > 0)
            {
                var label = MemoryHelper.ReadSeStringNullTerminated((nint)item->StringValues[0].Value).TextValue;
                if (label.Contains(name, StringComparison.OrdinalIgnoreCase)) return row;
            }
            row++;
        }
        return -1;
    }

    private void Fail(string message)
    {
        Failure = message;
        Status = message;
        step = Step.Failed;
        travel.Cancel();
        Interact.Close(Window);
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
