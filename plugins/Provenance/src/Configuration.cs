using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Provenance.Core.Character;
using Provenance.Core.Orders;

namespace Provenance;

/// <summary>Persisted settings and the active order. One class per area.</summary>
[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public Order Order { get; set; } = new();
    public StockSettings Stock { get; set; } = new();
    public BuyingSettings Buying { get; set; } = new();
    public ImportSettings Import { get; set; } = new();
    public BrowseSettings Browse { get; set; } = new();
    public WindowSettings Window { get; set; } = new();
    public ConsumableSettings Consumables { get; set; } = new();
    public CraftingSettings Crafting { get; set; } = new();
    /// <summary>Last "Check character" result per character (content id).</summary>
    public Dictionary<ulong, CharacterProfile> Characters { get; set; } = new();

    public void Save() => Services.PluginInterface.SavePluginConfig(this);

    public static Configuration Load()
    {
        var c = Services.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        // Sections added in later versions are null in older files.
        c.Order ??= new();
        c.Order.Lines ??= new();
        c.Stock ??= new();
        c.Buying ??= new();
        c.Import ??= new();
        c.Browse ??= new();
        c.Window ??= new();
        c.Characters ??= new();
        c.Consumables ??= new();
        c.Crafting ??= new();
        c.Consumables.CraftFood ??= new();
        c.Consumables.CraftMedicine ??= new();
        c.Consumables.GatherFood ??= new();
        c.Consumables.GatherMedicine ??= new();
        return c;
    }
}

/// <summary>Which owned items count against the order. The armoury chest never does.</summary>
[Serializable]
public sealed class StockSettings
{
    public bool Inventory { get; set; } = true;
    public bool Saddlebag { get; set; } = true;
    public bool Retainers { get; set; } = true;
    /// <summary>Count finished items already owned against the order.</summary>
    public bool CountFinishedItems { get; set; } = false;
}

[Serializable]
public sealed class BuyingSettings
{
    /// <summary>Most gil per unit the run may pay a vendor. 0 means no limit.</summary>
    public uint VendorMaxPrice { get; set; } = 2000;
    /// <summary>Buy vendor-sold materials instead of gathering or fishing them.</summary>
    public bool PreferVendor { get; set; } = true;
}

[Serializable]
public sealed class ImportSettings
{
    public bool IgnoreTeamcraftProgress { get; set; } = false;
    /// <summary>Imports replace the order (true) or add to it (false).</summary>
    public bool Replace { get; set; } = true;
}

[Serializable]
public sealed class BrowseSettings
{
    /// <summary>Bit per crafter (Carpenter = bit 0). 0 shows every job.</summary>
    public int JobMask { get; set; } = 0;
    public int MinLevel { get; set; } = 1;
    public int MaxLevel { get; set; } = 100;
}

[Serializable]
public sealed class CraftingSettings
{
    /// <summary>Self-repair when any equipped piece drops below this percentage. 0 turns repairing off.</summary>
    public int RepairBelow { get; set; } = 20;
    /// <summary>Ask Raphael for the best quality on ordered items; intermediates always get the fastest rotation.</summary>
    public bool AimForHq { get; set; } = true;
    /// <summary>Let rotations use Heart and Soul and Quick Innovation when a specialist soul is equipped.</summary>
    public bool UseSpecialistActions { get; set; } = true;
}

/// <summary>Food and medicine to use while crafting and while gathering. None picked: no consumables.</summary>
[Serializable]
public sealed class ConsumableSettings
{
    public ConsumableChoice CraftFood { get; set; } = new();
    public ConsumableChoice CraftMedicine { get; set; } = new();
    public ConsumableChoice GatherFood { get; set; } = new();
    public ConsumableChoice GatherMedicine { get; set; } = new();

    public (ConsumableChoice Food, ConsumableChoice Medicine) For(Discipline d)
        => d == Discipline.Hand ? (CraftFood, CraftMedicine) : (GatherFood, GatherMedicine);
}

[Serializable]
public sealed class WindowSettings
{
    public int LastTab { get; set; } = 0;
}
