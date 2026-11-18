using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;

namespace Provenance.Core.Planning;

/// <summary>How the plan gets an item.</summary>
public enum Supply
{
    Craft,
    Gather,
    GatherTimed,
    Fish,
    Vendor,
    Drop,
    /// <summary>No automated source: the user has to provide it before the order can start.</summary>
    Manual,
}

public sealed class PlannerOptions
{
    /// <summary>
    /// Count crafted order items already in the bags against the order. Off: the order always crafts its
    /// full amount. Order items that aren't crafted always count what's owned.
    /// </summary>
    public bool CountOwnedTargets { get; init; }

    /// <summary>Buy from a gil vendor even when the item can be gathered or fished.</summary>
    public bool PreferVendor { get; init; } = true;

    /// <summary>Highest gil per unit the run may pay a vendor. 0 means no limit.</summary>
    public uint VendorMaxPrice { get; init; }

    /// <summary>Picks among several usable recipes for the same item. Defaults to the lowest-level one.</summary>
    public System.Func<IReadOnlyList<RecipeInfo>, RecipeInfo?>? ChooseRecipe { get; init; }

    /// <summary>Why the character can't use a recipe (level, master book), or null when it can.</summary>
    public System.Func<RecipeInfo, string?>? RecipeBlocker { get; init; }

    /// <summary>Whether the character has read a folklore tome (by the tome's item id). Null: assume yes.</summary>
    public System.Func<uint, bool>? HasTome { get; init; }
}

/// <summary>Everything the plan knows about one item, summed over the whole order.</summary>
public sealed class PlanLine
{
    public uint ItemId { get; init; }
    public Supply Supply { get; internal set; }
    public RecipeInfo? Recipe { get; internal set; }
    public bool IsTarget { get; internal set; }

    /// <summary>Total amount the order consumes or delivers.</summary>
    public int Needed { get; internal set; }
    /// <summary>Taken from items already owned.</summary>
    public int FromStock { get; internal set; }
    /// <summary>Still to craft or acquire.</summary>
    public int Remaining => Needed - FromStock;

    /// <summary>Synthesis count (craft lines only).</summary>
    public int Crafts { get; internal set; }
    /// <summary>Items those crafts yield (can exceed what is needed when a recipe makes several).</summary>
    public int Produced => Recipe == null ? 0 : Crafts * Recipe.Yield;

    public uint VendorUnitPrice { get; internal set; }
    public string? ManualReason { get; internal set; }
}

/// <summary>A node of the display tree: one target and the materials under it.</summary>
public sealed class PlanTreeNode
{
    public uint ItemId { get; init; }
    /// <summary>Amount this branch alone uses, before anything owned is subtracted.</summary>
    public int Quantity { get; init; }
    public Supply Supply { get; init; }
    public List<PlanTreeNode> Children { get; } = new();
}

public sealed class Plan
{
    public static readonly Plan Empty = new(new Dictionary<uint, PlanLine>(), new List<PlanLine>(), new List<PlanTreeNode>());

    private readonly Dictionary<uint, PlanLine> lines;

    internal Plan(Dictionary<uint, PlanLine> lines, List<PlanLine> craftSteps, List<PlanTreeNode> tree)
    {
        this.lines = lines;
        CraftSteps = craftSteps;
        Tree = tree;
        Missing = lines.Values.Where(l => l.Supply == Supply.Manual && l.Remaining > 0).ToList();
        VendorGil = lines.Values.Where(l => l.Supply == Supply.Vendor).Sum(l => (long)l.Remaining * l.VendorUnitPrice);
    }

    public IReadOnlyCollection<PlanLine> Lines => lines.Values;

    /// <summary>Recipes in the order they must run: every ingredient's recipe comes before the recipes that use it.</summary>
    public IReadOnlyList<PlanLine> CraftSteps { get; }

    public IReadOnlyList<PlanTreeNode> Tree { get; }

    /// <summary>Items the run cannot get by itself. Non-empty blocks the start.</summary>
    public IReadOnlyList<PlanLine> Missing { get; }

    public long VendorGil { get; }

    public bool CanStart => lines.Count > 0 && Missing.Count == 0;

    public PlanLine? this[uint itemId] => lines.GetValueOrDefault(itemId);

    public int Count(Supply supply) => lines.Values.Count(l => l.Supply == supply && l.Remaining > 0);
}
