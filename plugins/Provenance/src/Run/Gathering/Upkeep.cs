using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using ECommons.Automation;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Provenance.Integrations;

namespace Provenance.Run.Gathering;

/// <summary>
/// Between nodes: extract materia from fully spiritbonded gear, and aetherially reduce spare
/// collectables when the bags fill up (never collectables the order or the scrip earning still needs).
/// Window handling follows GatherBuddy Reborn's Spiritbond and Purify parts (Apache-2.0).
/// </summary>
internal sealed unsafe class Upkeep
{
    private const uint MateriaQuest = 66174;   // Forging the Spirit
    private const uint ReductionQuest = 67633; // the aetherial reduction unlock
    private const int ReduceBelowFreeSlots = 20;

    private enum Step { Idle, Materia, Reduce, Done }

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime deadline;
    private int extracted;

    public Upkeep(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public bool Busy => step is Step.Materia or Step.Reduce;
    public string Status { get; private set; } = "";

    /// <summary>Equipped pieces at 100% spiritbond.</summary>
    public static int FullySpiritbonded()
    {
        var c = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null) return 0;
        var n = 0;
        for (var i = 0; i < c->Size; i++)
        {
            var s = c->GetInventorySlot(i);
            if (s != null && s->ItemId != 0 && s->SpiritbondOrCollectability >= 10000) n++;
        }
        return n;
    }

    /// <summary>Collectables the order needs (scrip collectables, ordered collectables): reduction is off while any are in the bags.</summary>
    private IReadOnlySet<uint> needed = new HashSet<uint>();

    /// <summary>Reducible collectables in the bags (base item ids), and whether any collectable in the bags is needed.</summary>
    private (List<uint> Spare, bool AnyNeeded) Reducible()
    {
        var items = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var result = new List<uint>();
        var anyNeeded = false;
        foreach (var bag in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var c = InventoryManager.Instance()->GetInventoryContainer(bag);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || !s->IsCollectable()) continue;
                var id = s->GetBaseItemId();
                // Only reducible ones show up in the reduction window, so only those are at risk.
                if (items.GetRowOrDefault(id)?.AetherialReduce is not > 0) continue;
                if (needed.Contains(id)) { anyNeeded = true; continue; }
                result.Add(id);
            }
        }
        return (result, anyNeeded);
    }

    /// <summary>Starts extraction or reduction when there is something to do. True when it started.</summary>
    public bool Begin(IReadOnlySet<uint> neededCollectables)
    {
        if (Busy) return true;
        needed = neededCollectables;
        var s = plugin.Config.Run;
        var c = Services.Condition;
        if (c[ConditionFlag.Mounted] || c[ConditionFlag.Gathering] || c[ConditionFlag.InCombat] || c[ConditionFlag.BetweenAreas]) return false;
        if (s.ExtractMateria && FullySpiritbonded() > 0 && QuestDone(MateriaQuest))
        {
            step = Step.Materia;
            extracted = 0;
            deadline = DateTime.UtcNow.AddSeconds(60);
            Status = "Extracting materia";
            return true;
        }
        // The game's reduction window reduces whatever it lists, so it only runs while no needed collectable is in the bags.
        if (s.ReduceCollectables && Data.Stock.FreeSlots() < ReduceBelowFreeSlots && QuestDone(ReductionQuest)
            && Reducible() is { Spare.Count: > 0, AnyNeeded: false })
        {
            step = Step.Reduce;
            deadline = DateTime.UtcNow.AddSeconds(120);
            Status = "Aetherial reduction";
            log("Bags are filling up; reducing spare collectables.", false);
            return true;
        }
        return false;
    }

    public void Tick()
    {
        if (!Busy || DateTime.UtcNow < waitUntil) return;
        if (DateTime.UtcNow > deadline) { Finish("took too long; carrying on"); return; }
        var occupied = Services.Condition[ConditionFlag.Occupied39];
        switch (step)
        {
            case Step.Materia:
                if (Interact.Ready("MaterializeDialog") is var dialog && dialog != null) { new AddonMaster.MaterializeDialog(dialog).Materialize(); extracted++; Wait(1.5); return; }
                if (occupied) { Wait(0.3); return; }
                if (FullySpiritbonded() == 0)
                {
                    if (Interact.Ready("Materialize") is var done && done != null) Callback.Fire(done, true, -1);
                    if (extracted > 0) log($"Extracted materia from {extracted} {(extracted == 1 ? "piece" : "pieces")} of gear.", false);
                    Finish(null);
                    return;
                }
                if (Interact.Ready("Materialize") is var list && list != null) { Callback.Fire(list, true, 2, 0); Wait(1); return; }
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 14);
                Wait(1.2);
                return;

            case Step.Reduce:
                if (Interact.Ready("PurifyResult") is var result && result != null)
                {
                    if (occupied) { Wait(0.5); return; }
                    // Reduce everything the selector offers in one go, then close.
                    new AddonMaster.PurifyResult(result).Automatic();
                    Wait(2);
                    return;
                }
                if (occupied) { Wait(0.5); return; }
                if (Interact.Ready("PurifyItemSelector") is var selector && selector != null)
                {
                    var (spare, anyNeeded) = Reducible();
                    if (spare.Count == 0 || anyNeeded) { Callback.Fire(selector, true, -1); Finish(null); return; }
                    Callback.Fire(selector, true, 12, 0u);
                    Wait(1.5);
                    return;
                }
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 21);
                Wait(1.2);
                return;
        }
    }

    private void Finish(string? problem)
    {
        if (Interact.Ready("PurifyResult") is var r && r != null) Callback.Fire(r, true, -1);
        if (Interact.Ready("PurifyItemSelector") is var s && s != null) Callback.Fire(s, true, -1);
        if (problem != null) log($"{Status}: {problem}.", true);
        step = Step.Idle;
        Status = "";
    }

    private static bool QuestDone(uint quest) => UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(quest);

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
