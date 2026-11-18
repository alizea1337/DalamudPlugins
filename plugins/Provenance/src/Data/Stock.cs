using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Provenance.Data;

/// <summary>Counts owned items (NQ and HQ together) in the bags the user allows. Main thread only.</summary>
internal static unsafe class Stock
{
    private static readonly InventoryType[] Bags =
    {
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.Crystals,
    };

    private static readonly InventoryType[] Saddlebags =
    {
        InventoryType.SaddleBag1, InventoryType.SaddleBag2,
        InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    };

    public static Dictionary<uint, int> Snapshot(Configuration config)
    {
        var settings = config.Stock;
        var counts = new Dictionary<uint, int>();
        var manager = InventoryManager.Instance();
        if (manager == null) return counts;
        if (settings.Inventory) Add(manager, Bags, counts);
        if (settings.Saddlebag) Add(manager, Saddlebags, counts);
        if (settings.Retainers)
            foreach (var (id, n) in RetainerCache.Totals(config))
                counts[id] = counts.GetValueOrDefault(id) + n;
        return counts;
    }

    /// <summary>Bags and saddlebag only (what a craft or a vendor can use without visiting a retainer).</summary>
    public static Dictionary<uint, int> OnHand(Configuration config)
    {
        var counts = new Dictionary<uint, int>();
        var manager = InventoryManager.Instance();
        if (manager == null) return counts;
        Add(manager, Bags, counts);
        if (config.Stock.Saddlebag) Add(manager, Saddlebags, counts);
        return counts;
    }

    /// <summary>Empty slots across the four main bags (for the 2-slot safety stop).</summary>
    public static int FreeSlots()
    {
        var manager = InventoryManager.Instance();
        return manager == null ? 0 : (int)manager->GetEmptySlotsInBag();
    }

    private static void Add(InventoryManager* manager, InventoryType[] types, Dictionary<uint, int> counts)
    {
        foreach (var type in types)
        {
            var container = manager->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot == null) continue;
                var id = slot->GetBaseItemId();
                if (id == 0) continue;
                counts[id] = counts.GetValueOrDefault(id) + (int)slot->GetQuantity();
            }
        }
    }
}
