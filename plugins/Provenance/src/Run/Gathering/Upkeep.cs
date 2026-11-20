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

    /// <summary>Aetherial reduction is unlocked (its quest is done).</summary>
    public static bool ReductionUnlocked => QuestDone(ReductionQuest);

    /// <summary>
    /// Why the collectables gathered for a reduction target can't be reduced right now (something else in the
    /// bags would be reduced with them), or null. The game's reduction reduces every reducible collectable it lists.
    /// </summary>
    public string? ReduceBlocked { get; private set; }
    private const int ReduceBelowFreeSlots = 20;

    private enum Step { Idle, Materia, Reduce, Dismount, Done }

    private readonly Plugin plugin;
    private readonly Action<string, bool> log;
    private Step step = Step.Idle;
    private DateTime waitUntil;
    private DateTime deadline;
    private int extracted;
    /// <summary>Times "Automatic" was pressed in this reduction (it reduces the rest in one go).</summary>
    private int automatics;

    public Upkeep(Plugin plugin, Action<string, bool> log)
    {
        this.plugin = plugin;
        this.log = log;
    }

    public bool Busy => step is Step.Materia or Step.Reduce or Step.Dismount;
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

    /// <summary>Collectables the order needs (scrip collectables, ordered collectables): reduction is off while a reducible one is in the bags.</summary>
    private IReadOnlySet<uint> needed = new HashSet<uint>();

    /// <summary>Reducible collectables in the bags (base item ids), and whether any reducible collectable in the bags is needed.</summary>
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
    /// <param name="reduceNow">
    /// Collectables gathered to be reduced (for an item that only comes from aetherial reduction): reduced as soon
    /// as they're in the bags, whatever the free slots, but never together with a collectable the order needs, nor
    /// with spare ones while "Reduce spare collectables" is off.
    /// </param>
    public bool Begin(IReadOnlySet<uint> neededCollectables, IReadOnlySet<uint>? reduceNow = null)
    {
        if (Busy) return true;
        needed = neededCollectables;
        var s = plugin.Config.Run;
        var c = Services.Condition;
        // Never with the rod out: extraction and reduction with the fishing stance up aren't checked in game.
        if (c[ConditionFlag.Gathering] || c[ConditionFlag.Fishing] || c[ConditionFlag.InCombat] || c[ConditionFlag.BetweenAreas]) return false;
        // Neither works on a mount. Collectables waiting to be reduced for the order: get off first (land when
        // flying), or nothing would ever reduce them and the run would stand there.
        if (c[ConditionFlag.Mounted])
        {
            if (reduceNow is not { Count: > 0 } || !QuestDone(ReductionQuest) || !Reducible().Spare.Any(reduceNow.Contains)) return false;
            step = Step.Dismount;
            deadline = DateTime.UtcNow.AddSeconds(20);
            Status = "Getting off the mount to reduce collectables";
            return true;
        }
        if (s.ExtractMateria && FullySpiritbonded() > 0 && QuestDone(MateriaQuest))
        {
            step = Step.Materia;
            extracted = 0;
            deadline = DateTime.UtcNow.AddSeconds(60);
            Status = "Extracting materia";
            return true;
        }
        ReduceBlocked = null;
        if (reduceNow is { Count: > 0 } && QuestDone(ReductionQuest) && Reducible() is var r && r.Spare.Any(reduceNow.Contains))
        {
            var others = r.Spare.Where(id => !reduceNow.Contains(id)).Distinct().ToList();
            var items = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
            string Name(uint id) => items.GetRowOrDefault(id)?.Name.ExtractText() ?? "a collectable";
            if (r.AnyNeeded)
                ReduceBlocked = "the bags also hold a reducible collectable the order needs, and reduction would take it too";
            else if (others.Count > 0 && !s.ReduceCollectables)
                ReduceBlocked = $"the bags also hold other reducible collectables ({string.Join(", ", others.Take(3).Select(Name))}) and \"Reduce spare collectables\" is off; put them in a retainer or turn the setting on";
            else
            {
                step = Step.Reduce;
                automatics = 0;
                deadline = DateTime.UtcNow.AddSeconds(120);
                Status = "Aetherial reduction";
                log($"Reducing {string.Join(", ", r.Spare.Where(reduceNow.Contains).Distinct().Select(Name))}.", false);
                return true;
            }
        }
        // The game's reduction window reduces whatever it lists, so it only runs while no needed reducible collectable is in the bags.
        if (s.ReduceCollectables && Data.Stock.FreeSlots() < ReduceBelowFreeSlots && QuestDone(ReductionQuest)
            && Reducible() is { Spare.Count: > 0, AnyNeeded: false })
        {
            step = Step.Reduce;
            automatics = 0;
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

            case Step.Dismount:
                if (!Services.Condition[ConditionFlag.Mounted])
                {
                    // Off: the caller asks again and the reduction starts.
                    step = Step.Idle;
                    Status = "";
                    return;
                }
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 23);
                Wait(1.5);
                return;

            case Step.Reduce:
            {
                // "Automatic" reduces the rest on its own: wait while it works, then close its window.
                if (Interact.Ready("PurifyAutoDialog") is var auto && auto != null)
                {
                    var running = new AddonMaster.PurifyAutoDialog(auto);
                    if (running.PurificationActive) { Wait(0.5); return; }
                    running.CancelExit();
                    Wait(1);
                    return;
                }
                var (spare, anyNeeded) = Reducible();
                var more = spare.Count > 0 && !anyNeeded;
                // The result of one reduction: the rest in one go while there is more, else close it.
                if (Interact.Ready("PurifyResult") is var result && result != null)
                {
                    if (occupied) { Wait(0.5); return; }
                    var r = new AddonMaster.PurifyResult(result);
                    if (more && automatics < 2) { automatics++; r.Automatic(); Wait(2); return; }
                    r.Close();
                    Wait(1);
                    return;
                }
                if (occupied) { Wait(0.5); return; }
                if (Interact.Ready("PurifyItemSelector") is var selector && selector != null)
                {
                    if (!more) { Callback.Fire(selector, true, -1); Wait(0.5); Finish(null); return; }
                    Callback.Fire(selector, true, 12, 0u);
                    Wait(1.5);
                    return;
                }
                // Every window closed: done once nothing is left to reduce.
                if (!more) { Finish(null); return; }
                ActionManager.Instance()->UseAction(ActionType.GeneralAction, 21);
                Wait(1.2);
                return;
            }
        }
    }

    /// <summary>Closes the aetherial reduction windows left open (the automatic run's, the result, the item list).</summary>
    public static void CloseReductionWindows()
    {
        if (Interact.Ready("PurifyAutoDialog") is var a && a != null) new AddonMaster.PurifyAutoDialog(a).CancelExit();
        if (Interact.Ready("PurifyResult") is var r && r != null) new AddonMaster.PurifyResult(r).Close();
        if (Interact.Ready("PurifyItemSelector") is var s && s != null) Callback.Fire(s, true, -1);
    }

    private void Finish(string? problem)
    {
        CloseReductionWindows();
        if (problem != null) log($"{Status}: {problem}.", true);
        step = Step.Idle;
        Status = "";
    }

    private static bool QuestDone(uint quest) => UIState.Instance()->IsUnlockLinkUnlockedOrQuestCompleted(quest);

    private void Wait(double seconds) => waitUntil = DateTime.UtcNow.AddSeconds(seconds);
}
