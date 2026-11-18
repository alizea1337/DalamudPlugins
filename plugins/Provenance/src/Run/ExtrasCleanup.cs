using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using Provenance.Integrations;

namespace Provenance.Run;

/// <summary>
/// Throws away what gathering brought in over the order's amount (a node is always finished, so the
/// last one usually overshoots). Only normal-quality items in the main bags; a stack bigger than the
/// extra is split first so exactly the extra goes. One step a second, so the server keeps up.
/// </summary>
internal sealed unsafe class ExtrasCleanup
{
    private static readonly InventoryType[] Bags = { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 };
    private const int MaxSteps = 12;

    private readonly Queue<(uint ItemId, int Keep, int Start)> work = new();
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

    /// <param name="keep">Per item, how many normal-quality ones to keep in the bags.</param>
    public void Begin(IEnumerable<(uint ItemId, int Keep)> keep)
    {
        work.Clear();
        foreach (var (id, k) in keep)
        {
            var have = GameState.BagCount(id, false);
            if (have > k) work.Enqueue((id, k, have));
        }
        steps = 0;
        nextStep = default;
    }

    public void Cancel() => work.Clear();

    public void Tick()
    {
        if (work.Count == 0 || DateTime.UtcNow < nextStep) return;
        var c = Services.Condition;
        if (c[ConditionFlag.Gathering] || c[ConditionFlag.Casting] || c[ConditionFlag.BetweenAreas] || c[ConditionFlag.Occupied]) return;

        var (item, keep, start) = work.Peek();
        var have = GameState.BagCount(item, false);
        var extra = have - keep;
        Status = $"Throwing away {Math.Max(0, extra)} extra {name(item)}";
        if (extra <= 0)
        {
            log($"Threw away {start - have} extra {name(item)}.", false);
            Next();
            return;
        }
        if (++steps > MaxSteps)
        {
            log($"Couldn't throw away the {extra} extra {name(item)}; do it yourself if you don't want them.", true);
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
                if (slot == null || slot->ItemId != item || slot->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality)) continue;
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

    private void Next()
    {
        work.Dequeue();
        steps = 0;
        nextStep = DateTime.UtcNow.AddSeconds(0.5);
        if (work.Count == 0) Status = "";
    }
}
