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
    /// <summary>A master book or folklore tome to read before anything else (needed by a recipe or a node).</summary>
    Learn,
}

public sealed class PlannerOptions
{
    /// <summary>
    /// Count order items already in the bags against the order. Off: the order always crafts or gathers
    /// its full amount on top of what is owned. Materials always count what's owned.
    /// </summary>
    public bool CountOwnedTargets { get; init; }

    /// <summary>Buy from a gil vendor even when the item can be gathered or fished.</summary>
    public bool PreferVendor { get; init; } = true;

    /// <summary>Highest gil per unit the run may pay a vendor. 0 means no limit.</summary>
    public uint VendorMaxPrice { get; init; }

    /// <summary>Picks among several usable recipes for the same item. Defaults to the lowest-level one.</summary>
    public System.Func<IReadOnlyList<RecipeInfo>, RecipeInfo?>? ChooseRecipe { get; init; }

    /// <summary>Why the character can't use a recipe (level, job), or null when it can.</summary>
    public System.Func<RecipeInfo, string?>? RecipeBlocker { get; init; }

    /// <summary>Item id of the master book a recipe needs that the character hasn't learned, or 0.</summary>
    public System.Func<RecipeInfo, uint>? MissingBook { get; init; }

    /// <summary>Whether the character has read a folklore tome (by the tome's item id). Null: assume yes.</summary>
    public System.Func<uint, bool>? HasTome { get; init; }

    /// <summary>How a run could buy a book or tome (by item id) at an exchange; null when no exchange sells it.</summary>
    public System.Func<uint, BookPurchase?>? BuyBook { get; init; }
}

/// <summary>A book or tome sold at a scrip or token exchange.</summary>
/// <param name="CanBuy">The character has the currency and can reach the exchange.</param>
/// <param name="Text">"300 Purple Crafters' Scrips at the scrip exchange in Solution Nine", or why it can't be bought.</param>
public sealed record BookPurchase(bool CanBuy, string Text);

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

    /// <summary>For books and tomes to learn: the items that need it.</summary>
    public IReadOnlyList<uint> NeededFor { get; internal set; } = System.Array.Empty<uint>();

    /// <summary>For books and tomes not in the bags: where the run can buy it, if anywhere.</summary>
    public BookPurchase? Purchase { get; internal set; }

    /// <summary>A book the run buys before reading it.</summary>
    public bool WillBuy => Supply == Supply.Learn && Remaining > 0 && Purchase is { CanBuy: true };
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
        Missing = lines.Values.Where(l => l.Supply is Supply.Manual or Supply.Learn && l.Remaining > 0 && !l.WillBuy).ToList();
        Learn = lines.Values.Where(l => l.Supply == Supply.Learn).ToList();
        VendorGil = lines.Values.Where(l => l.Supply == Supply.Vendor).Sum(l => (long)l.Remaining * l.VendorUnitPrice);
    }

    public IReadOnlyCollection<PlanLine> Lines => lines.Values;

    /// <summary>Recipes in the order they must run: every ingredient's recipe comes before the recipes that use it.</summary>
    public IReadOnlyList<PlanLine> CraftSteps { get; }

    public IReadOnlyList<PlanTreeNode> Tree { get; }

    /// <summary>Items the run cannot get by itself (including books not in the bags). Non-empty blocks the start.</summary>
    public IReadOnlyList<PlanLine> Missing { get; }

    /// <summary>Master books and folklore tomes to read before anything else.</summary>
    public IReadOnlyList<PlanLine> Learn { get; }

    public long VendorGil { get; }

    public bool CanStart => lines.Count > 0 && Missing.Count == 0;

    /// <summary>Ordered items whose recipe tree uses this item (or that are the item), in order.</summary>
    public IReadOnlyList<uint> TargetsNeeding(uint itemId)
    {
        var result = new List<uint>();
        foreach (var root in Tree)
            if (Uses(root) && !result.Contains(root.ItemId)) result.Add(root.ItemId);
        return result;

        bool Uses(PlanTreeNode node) => node.ItemId == itemId || node.Children.Any(Uses);
    }

    public PlanLine? this[uint itemId] => lines.GetValueOrDefault(itemId);

    public int Count(Supply supply) => lines.Values.Count(l => l.Supply == supply && l.Remaining > 0);
}
