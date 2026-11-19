using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>
/// Throws away what gathering brought in over the order's amount (a node is always finished, so the
/// last one usually overshoots), and fish caught on the side that the order doesn't need. Only plain
/// normal-quality stacks in the main bags, the ones <see cref="GameState.BagCount(uint, bool)"/> counts:
/// never high-quality items or collectables (such as the player's own collectable fish). A stack bigger
/// than the extra is split first so exactly the extra goes. One step a second, so the server keeps up.
/// </summary>
internal sealed unsafe class ExtrasCleanup
{
    private static readonly InventoryType[] Bags = { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
    private const int MaxSteps = 12;

    private readonly Queue<(uint ItemId, int Keep, int Start, bool ByCatch)> work = new();
    private readonly Action<string, bool> log;
    private readonly Func<uint, string> name;
    private DateTime nextStep;
    private int steps;

    public ExtrasCleanup(Action<string, bool> log, Func<uint, string> name)
    {
        this.log = log;
        this.name = name;
    }

    public bool Done => work.Count == 0;
    public string Status { get; private set; } = "";

    /// <param name="extras">Per gathered item, how many normal-quality ones to keep in the bags (counted as <see cref="Plain"/>).</param>
    /// <param name="byCatch">Per fish caught on the side, how many normal-quality ones to keep (at least what was there before).</param>
    public void Begin(IEnumerable<(uint ItemId, int Keep)> extras, IEnumerable<(uint ItemId, int Keep)>? byCatch = null)
    {
        work.Clear();
        foreach (var (id, k) in extras) Enqueue(id, k, false);
        if (byCatch != null)
            foreach (var (id, k) in byCatch) Enqueue(id, k, true);
        steps = 0;
        nextStep = default;
    }

    private void Enqueue(uint id, int keep, bool byCatch)
    {
        var have = GameState.BagCount(id, false);
        if (have > keep) work.Enqueue((id, keep, have, byCatch));
    }

    public void Cancel() => work.Clear();

    public void Tick()
    {
        if (work.Count == 0 || DateTime.UtcNow < nextStep) return;
        var c = Services.Condition;
        // Never with the rod out or a node open: the bags change while fishing or gathering.
        if (c[ConditionFlag.Gathering] || c[ConditionFlag.Fishing] || c[ConditionFlag.Casting] || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.Occupied]) return;

        var (item, keep, start, byCatch) = work.Peek();
        var have = GameState.BagCount(item, false);
        var extra = have - keep;
        Status = byCatch ? $"Throwing away {Math.Max(0, extra)} {name(item)} caught on the side" : $"Throwing away {Math.Max(0, extra)} extra {name(item)}";
        if (extra <= 0)
        {
            log(byCatch ? $"Threw away {start - have} {name(item)} caught on the side." : $"Threw away {start - have} extra {name(item)}.", false);
            Next();
            return;
        }
        if (++steps > MaxSteps)
        {
            log(byCatch ? $"Couldn't throw away the {extra} {name(item)} caught on the side; do it yourself if you don't want them."
                : $"Couldn't throw away the {extra} extra {name(item)}; do it yourself if you don't want them.", true);
            Next();
            return;
        }

        var inv = InventoryManager.Instance();
        if (inv == null) return;
        // A stack no bigger than the extra goes whole (the biggest such); otherwise split the extra off the smallest stack.
        (InventoryType Bag, ushort Slot, int Qty)? whole = null, smallest = null;
        foreach (var bag in Bags)
        {
            var container = inv->GetInventoryContainer(bag);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                // Only stacks the keep and the extra are counted in: a collectable keeps the plain item id in its slot.
                if (slot == null || slot->ItemId != item || !Plain(slot)) continue;
                var qty = (int)slot->Quantity;
                if (qty <= extra && (whole == null || qty > whole.Value.Qty)) whole = (bag, (ushort)i, qty);
                if (smallest == null || qty < smallest.Value.Qty) smallest = (bag, (ushort)i, qty);
            }
        }
        if (whole is { } w) inv->DiscardItem(w.Bag, w.Slot);
        else if (smallest is { } sm) inv->SplitItem(sm.Bag, sm.Slot, extra);
        else { Next(); return; }
        nextStep = DateTime.UtcNow.AddSeconds(1);
    }

    /// <summary>
    /// Normal-quality count of every item in the main bags, taken when gathering starts: fish caught on
    /// the side are never thrown away below what was there before. Counted like <see cref="GameState.BagCount(uint, bool)"/>
    /// (plain stacks only), so it compares with the count the keep and the extra are worked out in.
    /// </summary>
    public static Dictionary<uint, int> NormalCounts()
    {
        var counts = new Dictionary<uint, int>();
        var inv = InventoryManager.Instance();
        if (inv == null) return counts;
        foreach (var bag in Bags)
        {
            var container = inv->GetInventoryContainer(bag);
            if (container == null) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null || slot->ItemId == 0 || !Plain(slot)) continue;
                counts[slot->ItemId] = counts.GetValueOrDefault(slot->ItemId) + (int)slot->Quantity;
            }
        }
        return counts;
    }

    /// <summary>
    /// A normal-quality stack that isn't a collectable: what the game's own item count (<see cref="GameState.BagCount(uint, bool)"/>
    /// with hq false) counts. High-quality items and collectables are left out of that count, and a collectable
    /// keeps the plain item id in its slot, so only these stacks may ever be thrown away.
    /// </summary>
    private static bool Plain(InventoryItem* slot)
        => !slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality) && !slot->IsCollectable();

    private void Next()
    {
        work.Dequeue();
        steps = 0;
        nextStep = DateTime.UtcNow.AddSeconds(0.5);
        if (work.Count == 0) Status = "";
    }
}
