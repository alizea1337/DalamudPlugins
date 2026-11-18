using System;
using System.Collections.Generic;
using System.Linq;

namespace Provenance.Core.Orders;

/// <summary>One finished item the order should produce.</summary>
[Serializable]
public sealed class OrderLine
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; }
    /// <summary>A specific recipe (from a Teamcraft import string); 0 lets the planner choose.</summary>
    public uint RecipeId { get; set; }
    /// <summary>Craft it in high quality (only matters for crafted items that can be HQ).</summary>
    public bool Hq { get; set; } = true;

    public OrderLine() { }

    public OrderLine(uint itemId, int quantity, uint recipeId = 0, bool hq = true)
    {
        ItemId = itemId;
        Quantity = quantity;
        RecipeId = recipeId;
        Hq = hq;
    }
}

/// <summary>What the user wants made. v1 keeps one active order.</summary>
[Serializable]
public sealed class Order
{
    public string Name { get; set; } = "New order";
    public List<OrderLine> Lines { get; set; } = new();

    public bool IsEmpty => Lines.Count == 0 || Lines.All(l => l.Quantity <= 0);

    /// <summary>Adds to an existing line for the same item, recipe and quality, or appends a new one.</summary>
    public void Add(uint itemId, int quantity, uint recipeId = 0, bool hq = true)
    {
        if (quantity <= 0) return;
        var existing = Lines.FirstOrDefault(l => l.ItemId == itemId && l.RecipeId == recipeId && l.Hq == hq);
        if (existing != null) existing.Quantity += quantity;
        else Lines.Add(new OrderLine(itemId, quantity, recipeId, hq));
    }

    public void Merge(IEnumerable<OrderLine> lines)
    {
        foreach (var l in lines) Add(l.ItemId, l.Quantity, l.RecipeId, l.Hq);
    }
}
