using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Provenance.Core.Character;
using GamePlayerState = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState;

namespace Provenance.Integrations;

/// <summary>Small reads and actions on the game the crafter and later stages share.</summary>
internal static unsafe class GameState
{
    private const uint WellFed = 48;
    private const uint Medicated = 49;

    public static uint CurrentJob => Services.Objects.LocalPlayer?.ClassJob.RowId ?? 0;

    /// <summary>In the crafting stance (recipe log open or crafting).</summary>
    public static bool InCraftingStance
        => Services.Condition[ConditionFlag.Crafting] || Services.Condition[ConditionFlag.PreparingToCraft] || Services.Condition[ConditionFlag.ExecutingCraftingAction];

    /// <summary>Highest item level gearset saved for a job, or -1.</summary>
    public static int BestGearset(uint classJob)
    {
        var module = RaptureGearsetModule.Instance();
        if (module == null) return -1;
        var best = -1;
        for (var i = 0; i < 100; i++)
        {
            var e = module->Entries[i];
            if ((e.Flags & RaptureGearsetModule.GearsetFlag.Exists) == 0 || e.ClassJob != classJob) continue;
            if (best < 0 || e.ItemLevel > module->Entries[best].ItemLevel) best = i;
        }
        return best;
    }

    public static bool EquipGearset(int index)
    {
        var module = RaptureGearsetModule.Instance();
        return module != null && module->EquipGearset(index, 0) >= 0;
    }

    /// <summary>A piece of gear is on the character now.</summary>
    public static bool Wearing(uint itemId)
    {
        var c = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null || itemId == 0) return false;
        for (var i = 0; i < c->Size; i++)
            if (c->GetInventorySlot(i) is var slot && slot != null && slot->GetBaseItemId() == itemId) return true;
        return false;
    }

    private static readonly InventoryType[] GearPlaces =
    {
        InventoryType.ArmoryHands, InventoryType.ArmoryBody, InventoryType.ArmoryHead, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets,
        InventoryType.ArmoryWaist, InventoryType.ArmoryEar, InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings,
        InventoryType.ArmoryMainHand, InventoryType.ArmoryOffHand,
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
    };

    /// <summary>
    /// Puts on a piece of gear from the armoury chest or the bags, as dragging it onto its slot does (the piece it
    /// replaces goes to the armoury chest). False when it isn't found or has no slot.
    /// </summary>
    public static bool Equip(uint itemId)
    {
        var inv = InventoryManager.Instance();
        var row = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault(itemId);
        if (inv == null || row?.EquipSlotCategory.ValueNullable is not { } category) return false;
        // The equipped items' slots, in the game's order.
        var slot = category.MainHand > 0 ? 0 : category.OffHand > 0 ? 1 : category.Head > 0 ? 2 : category.Body > 0 ? 3 : category.Gloves > 0 ? 4
            : category.Waist > 0 ? 5 : category.Legs > 0 ? 6 : category.Feet > 0 ? 7 : category.Ears > 0 ? 8 : category.Neck > 0 ? 9
            : category.Wrists > 0 ? 10 : category.FingerR > 0 ? 11 : category.FingerL > 0 ? 12 : -1;
        if (slot < 0) return false;
        foreach (var place in GearPlaces)
        {
            var c = inv->GetInventoryContainer(place);
            if (c == null || !c->IsLoaded) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var s = c->GetInventorySlot(i);
                if (s == null || s->GetBaseItemId() != itemId) continue;
                inv->MoveItemSlot(place, (ushort)i, InventoryType.EquippedItems, (ushort)slot, true);
                return true;
            }
        }
        return false;
    }

    /// <summary>[primary, secondary, points] as the game shows them now, food and medicine included.</summary>
    public static int[] LiveStats(Discipline d)
    {
        var ps = GamePlayerState.Instance();
        var ids = d == Discipline.Hand ? new[] { 70, 71, 11 } : new[] { 72, 73, 10 };
        return ps == null ? new int[3] : ids.Select(i => ps->Attributes[i]).ToArray();
    }

    public static int JobLevel(uint classJob) => Data.PlanService.JobLevel(classJob);

    /// <summary>The soul crystal slot holds a specialist soul.</summary>
    /// <summary>
    /// Crafter's Delineation: each specialist action (Careful Observation, Heart and Soul, Quick Innovation) uses one;
    /// without any the game refuses them ("Cannot execute at this time"), soul crystal or not. Fact from Artisan (BSD-3),
    /// seen in a live log: both refused with the crystal equipped.
    /// </summary>
    public const uint Delineation = 28724;

    /// <summary>Crafter's Delineations in the bags.</summary>
    public static int Delineations => BagCount(Delineation);

    public static bool SpecialistEquipped()
    {
        var c = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null || c->Size <= 13) return false;
        var slot = c->GetInventorySlot(13);
        return slot != null && slot->GetBaseItemId() != 0;
    }

    /// <summary>Lowest condition of any equipped piece, in percent (0–100+).</summary>
    public static float LowestGearCondition()
    {
        var c = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        if (c == null) return 100;
        var lowest = float.MaxValue;
        for (var i = 0; i < c->Size; i++)
        {
            var slot = c->GetInventorySlot(i);
            if (slot == null || slot->GetBaseItemId() == 0 || i == 13) continue;
            lowest = Math.Min(lowest, slot->Condition / 300f);
        }
        return lowest == float.MaxValue ? 100 : lowest;
    }

    /// <summary>Seconds left on Well Fed (food) or Medicated (medicine); 0 when not active.</summary>
    public static float BuffSecondsLeft(bool medicine)
    {
        var id = medicine ? Medicated : WellFed;
        var status = Services.Objects.LocalPlayer?.StatusList.FirstOrDefault(s => s.StatusId == id);
        return status?.RemainingTime ?? 0;
    }

    /// <summary>NQ + HQ count in the main bags (including crystals).</summary>
    public static int BagCount(uint itemId)
    {
        var inv = InventoryManager.Instance();
        // The Cosmopouch too: a Cosmic Exploration mission's supplies and catches (its bait, the fish) live there.
        return inv == null ? 0 : inv->GetInventoryItemCount(itemId, false, false, false, 0) + inv->GetInventoryItemCount(itemId, true, false, false, 0) + PouchCount(itemId);
    }

    /// <summary>How many of an item the Cosmopouch holds (Cosmic Exploration missions keep their items there; empty otherwise).</summary>
    public static int PouchCount(uint itemId)
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return 0;
        var total = 0;
        foreach (var type in new[] { InventoryType.Cosmopouch1, InventoryType.Cosmopouch2 })
        {
            var container = inv->GetInventoryContainer(type);
            if (container == null || !container->IsLoaded) continue;
            for (var i = 0; i < container->Size; i++)
            {
                var slot = container->GetInventorySlot(i);
                if (slot != null && slot->ItemId == itemId) total += slot->Quantity;
            }
        }
        return total;
    }

    private static readonly Lazy<HashSet<uint>> GatheredCollectables = new(() =>
    {
        var items = Services.Data.GetExcelSheet<Lumina.Excel.Sheets.Item>();
        var set = new HashSet<uint>();
        foreach (var gi in Services.Data.GetExcelSheet<Lumina.Excel.Sheets.GatheringItem>())
            if (gi.Item.RowId is > 0 and < 1_000_000 && items.GetRowOrDefault(gi.Item.RowId) is { IsCollectable: true })
                set.Add(gi.Item.RowId);
        return set;
    });

    /// <summary>
    /// Items a gathering node hands out as collectables: every gathered item that is a collectable
    /// (the Rarefied ones, custom delivery and quest items, aethersand sources such as Fire Moraine).
    /// No item is gathered both ways, whatever its stack size. Fish aren't included.
    /// </summary>
    public static bool GatheredAsCollectable(uint itemId) => GatheredCollectables.Value.Contains(itemId);

    /// <summary>
    /// What a run counts as gathered: normal-quality items in the main bags, and for items nodes hand
    /// out as collectables, the collectables (any collectability).
    /// </summary>
    public static int GatheredInBags(uint itemId)
        => BagCount(itemId, false) + (GatheredAsCollectable(itemId) ? Collectables(itemId).Count : 0);

    /// <summary>Collectability of each collectable of an item in the main bags (collectables don't count as normal items).</summary>
    public static List<int> Collectables(uint itemId)
    {
        var result = new List<int>();
        var inv = InventoryManager.Instance();
        if (inv == null) return result;
        foreach (var bag in new[] { InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4 })
        {
            var c = inv->GetInventoryContainer(bag);
            if (c == null) continue;
            for (var i = 0; i < c->Size; i++)
            {
                var slot = c->GetInventorySlot(i);
                if (slot == null || slot->GetBaseItemId() != itemId || !slot->IsCollectable()) continue;
                for (var n = 0; n < Math.Max(1, (int)slot->GetQuantity()); n++) result.Add(slot->GetCollectability());
            }
        }
        return result;
    }

    /// <summary>Scrips, tokens and other exchange currencies: the currency tab or the bags, whichever holds them.</summary>
    public static int CurrencyCount(uint itemId)
    {
        var inv = InventoryManager.Instance();
        if (inv == null) return 0;
        return Math.Max(inv->GetItemCountInContainer(itemId, InventoryType.Currency, false, 0), inv->GetInventoryItemCount(itemId, false, false, false, 0));
    }

    public static int BagCount(uint itemId, bool hq)
    {
        var inv = InventoryManager.Instance();
        return inv == null ? 0 : inv->GetInventoryItemCount(itemId, hq, false, false, 0);
    }

    /// <summary>Uses an item from the bags (food, medicine). HQ items are addressed as id + 1,000,000.</summary>
    public static bool UseItem(uint itemId, bool hq)
        => ActionManager.Instance()->UseAction(ActionType.Item, hq ? itemId + 1_000_000 : itemId, 0xE000_0000, 65535);

    public static bool CanUse(ActionType type, uint id) => ActionManager.Instance()->GetActionStatus(type, id) == 0;

    /// <summary>Why the game won't take an action right now (0: it would), for the log.</summary>
    public static uint ActionStatus(ActionType type, uint id) => ActionManager.Instance()->GetActionStatus(type, id);

    public static bool Use(ActionType type, uint id) => ActionManager.Instance()->UseAction(type, id);
}
