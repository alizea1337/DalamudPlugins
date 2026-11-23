using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Core.Catalog;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>
/// Takes items out of retainers: to a summoning bell, then for each retainer that holds something
/// the order needs: open it, "Entrust or withdraw items", move the stacks into the bags, dismiss.
/// Ticked on the framework thread.
/// </summary>
internal sealed unsafe class RetainerRun
{
    private enum Step { Idle, FindBell, Travel, Talk, PickRetainer, RetainerMenu, Withdraw, Dismiss, CloseList, Done, Failed }

    /// <summary>"Entrust or withdraw items" in the client languages.</summary>
    private static readonly string[] ItemsEntry = { "withdraw items", "Gegenstände", "objets", "アイテム" };

    private static readonly InventoryType[] RetainerPages =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
    };

    private static readonly InventoryType[] Bags = { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private readonly Travel travel = new();
    private readonly Queue<(ulong Retainer, string Name, List<(uint Item, int Amount)> Items)> work = new();
    private (ulong Retainer, string Name, List<(uint Item, int Amount)> Items)? current;
    private Dictionary<uint, int> movedThisRetainer = new();
    /// <summary>
    /// The current retainer's place in the bell's list (1 = the first), which the log and the status name it by: they
    /// go into bug reports, which carry no retainer names.
    /// </summary>
    private int number;
    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime deadline;
    private int tries;
    private bool pauseRequested;
    private bool travelledByTeleport;

    public RetainerRun(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public string Status { get; private set; } = "";
    public string? Failure { get; private set; }
    public bool Finished => step is Step.Done or Step.Failed;
    public bool IsPaused { get; private set; }

    public void Begin(IEnumerable<(ulong Retainer, string Name, List<(uint Item, int Amount)> Items)> retainers)
    {
        work.Clear();
        foreach (var r in retainers.Where(r => r.Items.Count > 0)) work.Enqueue(r);
        current = null;
        Failure = null;
        IsPaused = false;
        pauseRequested = false;
        travelledByTeleport = false;
        step = work.Count == 0 ? Step.Done : Step.FindBell;
    }

    public void RequestPause() => pauseRequested = true;

    public void Resume()
    {
        pauseRequested = false;
        if (!IsPaused) return;
        IsPaused = false;
        step = Step.FindBell;
    }

    public void Cancel()
    {
        travel.Cancel();
        Interact.Close("InventoryRetainerLarge");
        Interact.Close("InventoryRetainer");
        Interact.Close("RetainerList");
    }

    public void Tick()
    {
        if (Finished || IsPaused || DateTime.UtcNow < waitUntil) return;
        switch (step)
        {
            case Step.FindBell: FindBell(); return;
            case Step.Travel:
                travel.Tick();
                Status = travel.Status;
                if (travel.Current == Travel.State.Failed) { Fail(travel.Failure!); return; }
                if (travel.Current != Travel.State.Arrived) return;
                // Arrived near the aetheryte: now walk the last bit to the bell itself.
                if (Bell() is { } b && Interact.Distance(b) > 4) { step = Step.FindBell; return; }
                if (Bell() == null) { Fail("There's no summoning bell near the aetheryte set in Settings; pick another one."); return; }
                Go(Step.Talk, 15);
                return;
            case Step.Talk: Talk(); return;
            case Step.PickRetainer: PickRetainer(); return;
            case Step.RetainerMenu: RetainerMenu(); return;
            case Step.Withdraw: Withdraw(); return;
            case Step.Dismiss: Dismiss(); return;
            case Step.CloseList:
                Interact.Close("RetainerList");
                if (Interact.IsOpen("RetainerList")) { Wait(0.3); return; }
                step = Step.Done;
                Status = "Took everything out";
                return;
        }
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? Bell()
        => Interact.Nearest(ObjectKind.EventObj, plugin.Plans.Catalog!.SummoningBells.Contains);

    private void FindBell()
    {
        var bell = Bell();
        if (bell != null && Interact.Distance(bell) < 120)
        {
            var pos = bell.Position;
            travel.GoTo("the summoning bell", Services.ClientState.TerritoryType, new Vector2(pos.X, pos.Z), 2.5f, () => Bell()?.Position);
            step = Step.Travel;
            return;
        }
        if (travelledByTeleport) { Fail("There's no summoning bell near the aetheryte set in Settings; pick another one."); return; }
        var aetheryte = plugin.Config.Run.BellAetheryte;
        var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Aetheryte>().GetRowOrDefault(aetheryte);
        var at = Teleporter.Position(aetheryte);
        if (row == null || at == null) { Fail("The aetheryte set for summoning bells isn't known; pick another one in Settings."); return; }
        travelledByTeleport = true;
        travel.GoTo("the summoning bell", row.Value.Territory.RowId, new Vector2(at.Value.X, at.Value.Z), 15f, () => Bell()?.Position);
        step = Step.Travel;
    }

    private void Talk()
    {
        if (Interact.Ready("RetainerList") != null) { Go(Step.PickRetainer, 10); return; }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (DateTime.UtcNow > deadline) { Fail("The summoning bell didn't open."); return; }
        var bell = Bell();
        if (bell == null) { step = Step.FindBell; return; }
        Status = "Using the summoning bell";
        if (++tries > 5) { Fail("Couldn't use the summoning bell."); return; }
        Interact.With(bell);
        Wait(1.2);
    }

    private void PickRetainer()
    {
        if (pauseRequested) { step = Step.CloseList; IsPaused = true; pauseRequested = false; Status = "Paused"; return; }
        if (current == null)
        {
            if (!work.TryDequeue(out var next)) { step = Step.CloseList; return; }
            current = next;
            number = 0;
            movedThisRetainer = new Dictionary<uint, int>();
        }
        var list = Interact.Ready("RetainerList");
        if (list == null)
        {
            if (DateTime.UtcNow > deadline) { step = Step.FindBell; tries = 0; }
            Wait(0.3);
            return;
        }
        var rm = RetainerManager.Instance();
        for (uint i = 0; i < rm->GetRetainerCount(); i++)
        {
            var r = rm->GetRetainerBySortedIndex(i);
            if (r == null || r->RetainerId != current.Value.Retainer) continue;
            number = (int)i + 1;
            Status = $"Calling retainer {number}";
            new AddonMaster.RetainerList((nint)list).Retainers[i].Select();
            Go(Step.RetainerMenu, 10);
            Wait(1);
            return;
        }
        log("One of the retainers wasn't found at the bell; skipped.", true);
        current = null;
    }

    private void RetainerMenu()
    {
        if (Interact.Ready("InventoryRetainerLarge") != null || Interact.Ready("InventoryRetainer") != null) { Go(Step.Withdraw, 30); return; }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        if (Interact.Choose(ItemsEntry)) { Wait(1); return; }
        if (DateTime.UtcNow > deadline) { Fail($"Retainer {number}'s menu didn't open."); return; }
        Wait(0.3);
    }

    /// <summary>Moves whole stacks of each wanted item into the bags until the amount is covered.</summary>
    private void Withdraw()
    {
        var items = current!.Value.Items;
        var inv = InventoryManager.Instance();
        foreach (var (item, amount) in items)
        {
            if (movedThisRetainer.GetValueOrDefault(item) >= amount) continue;
            var crystal = Items.IsCrystal(item);
            var pages = crystal ? new[] { InventoryType.RetainerCrystals } : RetainerPages;
            foreach (var page in pages)
            {
                var c = inv->GetInventoryContainer(page);
                if (c == null || !c->IsLoaded) continue;
                for (var s = 0; s < c->Size; s++)
                {
                    var slot = c->GetInventorySlot(s);
                    if (slot == null || slot->GetBaseItemId() != item || slot->GetQuantity() == 0) continue;
                    var (dstType, dstSlot) = crystal ? (InventoryType.Crystals, (int)item - 2) : EmptyBagSlot();
                    if (dstSlot < 0) { Fail("Your bags are full; make room and start again."); return; }
                    Status = $"Taking {plugin.Plans.Catalog!.ItemName(item)} from retainer {number}";
                    var qty = (int)slot->GetQuantity();
                    inv->MoveItemSlot(page, (ushort)s, dstType, (ushort)dstSlot, true);
                    movedThisRetainer[item] = movedThisRetainer.GetValueOrDefault(item) + qty;
                    log($"Took {qty}× {plugin.Plans.Catalog!.ItemName(item)} from retainer {number}.", false);
                    Wait(0.6);
                    return; // one move per tick, so the game keeps up
                }
            }
            // Nothing (more) of it here: remembered contents were out of date.
            movedThisRetainer[item] = amount;
        }
        Go(Step.Dismiss, 15);
    }

    private void Dismiss()
    {
        Interact.Close("InventoryRetainerLarge");
        Interact.Close("InventoryRetainer");
        if (Interact.Ready("RetainerList") != null) { current = null; Go(Step.PickRetainer, 10); return; }
        if (Interact.SkipTalk()) { Wait(0.3); return; }
        var menu = Interact.Menu();
        if (menu != null && menu.Count > 0) { menu[^1].Select(); Wait(1); return; } // last entry: Quit
        if (DateTime.UtcNow > deadline) { Fail("Couldn't dismiss the retainer."); return; }
        Wait(0.3);
    }

    private static (InventoryType, int) EmptyBagSlot()
    {
        var inv = InventoryManager.Instance();
        foreach (var bag in Bags)
        {
            var c = inv->GetInventoryContainer(bag);
            if (c == null) continue;
            for (var s = 0; s < c->Size; s++)
                if (c->GetInventorySlot(s) is var slot && slot != null && slot->GetBaseItemId() == 0) return (bag, s);
        }
        return (InventoryType.Inventory1, -1);
    }

    private void Go(Step next, double seconds)
    {
        step = next;
        deadline = DateTime.UtcNow.AddSeconds(seconds);
        tries = 0;
    }

    private void Fail(string message)
    {
        Failure = message;
        Status = message;
        step = Step.Failed;
        Cancel();
    }

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
