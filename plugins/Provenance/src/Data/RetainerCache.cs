using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace Provenance.Data;

/// <summary>What one retainer held the last time its inventory was open.</summary>
[Serializable]
public sealed class RetainerStock
{
    public string Name { get; set; } = "";
    public DateTime SeenUtc { get; set; }
    public Dictionary<uint, int> Items { get; set; } = new();
}

/// <summary>
/// Remembers retainer inventories. The game only loads a retainer's bags while that retainer is
/// open at a summoning bell, so Provenance notes them whenever that happens (yours or a run's).
/// </summary>
internal static unsafe class RetainerCache
{
    private static readonly InventoryType[] Pages =
    {
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7, InventoryType.RetainerCrystals,
    };

    private static DateTime next;

    /// <summary>Framework thread; snapshots the open retainer about once a second.</summary>
    public static void Tick(Configuration config)
    {
        if (DateTime.UtcNow < next || !Services.PlayerState.IsLoaded) return;
        next = DateTime.UtcNow.AddSeconds(1);
        var rm = RetainerManager.Instance();
        var retainer = rm == null ? null : rm->GetActiveRetainer();
        if (retainer == null || retainer->RetainerId == 0) return;
        var inv = InventoryManager.Instance();
        var first = inv->GetInventoryContainer(InventoryType.RetainerPage1);
        if (first == null || !first->IsLoaded) return;

        var items = new Dictionary<uint, int>();
        foreach (var type in Pages)
        {
            var c = inv->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot == null || slot->GetBaseItemId() == 0) continue;
                items[slot->GetBaseItemId()] = items.GetValueOrDefault(slot->GetBaseItemId()) + (int)slot->GetQuantity();
            }
        }

        var mine = For(config, Services.PlayerState.ContentId, create: true)!;
        var stock = new RetainerStock { Name = retainer->NameString, SeenUtc = DateTime.UtcNow, Items = items };
        if (mine.TryGetValue(retainer->RetainerId, out var old) && SameItems(old.Items, items)) { old.SeenUtc = stock.SeenUtc; return; }
        mine[retainer->RetainerId] = stock;
        config.Save();
    }

    public static Dictionary<ulong, RetainerStock>? For(Configuration config, ulong contentId, bool create = false)
    {
        if (config.Retainers.TryGetValue(contentId, out var d)) return d;
        if (!create) return null;
        return config.Retainers[contentId] = new Dictionary<ulong, RetainerStock>();
    }

    /// <summary>Total of an item across the current character's remembered retainers.</summary>
    public static Dictionary<uint, int> Totals(Configuration config)
    {
        var totals = new Dictionary<uint, int>();
        if (!Services.PlayerState.IsLoaded || For(config, Services.PlayerState.ContentId) is not { } mine) return totals;
        foreach (var r in mine.Values)
        foreach (var (id, n) in r.Items)
            totals[id] = totals.GetValueOrDefault(id) + n;
        return totals;
    }

    private static bool SameItems(Dictionary<uint, int> a, Dictionary<uint, int> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a) if (!b.TryGetValue(k, out var w) || w != v) return false;
        return true;
    }
}
