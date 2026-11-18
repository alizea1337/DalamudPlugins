using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;
using Provenance.Core.Orders;

namespace Provenance.Core.Planning;

/// <summary>
/// Expands an order into every material it needs, decides how each one is obtained and in which
/// order the recipes run. Pure: game data comes through <see cref="IGameCatalog"/>, stock as a dictionary.
/// </summary>
public static class Planner
{
    private const int MaxTreeDepth = 12;

    public static Plan Build(Order order, IGameCatalog catalog, IReadOnlyDictionary<uint, int> owned, PlannerOptions options)
    {
        var targets = order.Lines.Where(l => l.Quantity > 0).ToList();
        if (targets.Count == 0) return Plan.Empty;

        var lines = new Dictionary<uint, PlanLine>();
        var postOrder = new List<uint>();
        var state = new Dictionary<uint, VisitState>();

        // 1. Decide how every reachable item is supplied, and record a post-order (ingredients first).
        foreach (var t in targets)
        {
            var line = GetOrAddLine(t.ItemId, isTarget: true, t.RecipeId);
            line.IsTarget = true;
        }
        foreach (var t in targets) Visit(t.ItemId);

        // 2. Push demand from products down to ingredients (reverse post-order is a topological order).
        var demand = new Dictionary<uint, int>();
        foreach (var t in targets) demand[t.ItemId] = demand.GetValueOrDefault(t.ItemId) + t.Quantity;

        for (var i = postOrder.Count - 1; i >= 0; i--)
        {
            var line = lines[postOrder[i]];
            line.Needed = demand.GetValueOrDefault(line.ItemId);
            var stock = line.IsTarget && !options.CountOwnedTargets ? 0 : owned.GetValueOrDefault(line.ItemId);
            line.FromStock = Math.Min(Math.Max(0, stock), line.Needed);

            if (line.Supply != Supply.Craft || line.Recipe == null) continue;
            line.Crafts = (line.Remaining + line.Recipe.Yield - 1) / line.Recipe.Yield;
            foreach (var ing in line.Recipe.Ingredients)
                demand[ing.ItemId] = demand.GetValueOrDefault(ing.ItemId) + line.Crafts * ing.Amount;
        }

        var craftSteps = postOrder.Select(id => lines[id]).Where(l => l.Supply == Supply.Craft && l.Crafts > 0).ToList();
        var tree = targets.Select(t => BuildTree(t.ItemId, t.Quantity, 0)).ToList();

        // Materials below an intermediate that stock fully covers are not needed at all.
        foreach (var id in lines.Where(kv => kv.Value.Needed == 0 && !kv.Value.IsTarget).Select(kv => kv.Key).ToList())
            lines.Remove(id);
        return new Plan(lines, craftSteps, tree);

        void Visit(uint itemId)
        {
            if (state.TryGetValue(itemId, out var s))
            {
                // Already done, or a cycle (an item that is its own ingredient) — never follow it twice.
                if (s == VisitState.Open)
                {
                    var cyc = lines[itemId];
                    if (!cyc.IsTarget) DecideAcquire(cyc);
                }
                return;
            }
            state[itemId] = VisitState.Open;
            var line = lines[itemId];
            if (line.Supply == Supply.Craft && line.Recipe != null)
            {
                foreach (var ing in line.Recipe.Ingredients)
                {
                    GetOrAddLine(ing.ItemId, isTarget: false, 0);
                    Visit(ing.ItemId);
                }
            }
            state[itemId] = VisitState.Done;
            postOrder.Add(itemId);
        }

        PlanLine GetOrAddLine(uint itemId, bool isTarget, uint recipeId)
        {
            if (lines.TryGetValue(itemId, out var existing)) return existing;
            var line = new PlanLine { ItemId = itemId, IsTarget = isTarget };
            lines[itemId] = line;

            var recipe = PickRecipe(itemId, recipeId);
            if (recipe != null)
            {
                line.Supply = Supply.Craft;
                line.Recipe = recipe;
            }
            else
            {
                DecideAcquire(line);
            }
            return line;
        }

        RecipeInfo? PickRecipe(uint itemId, uint recipeId)
        {
            if (recipeId != 0)
            {
                var exact = catalog.Recipe(recipeId);
                if (exact != null && exact.ItemId == itemId) return exact;
            }
            var all = catalog.RecipesFor(itemId);
            if (all.Count == 0) return null;
            if (all.Count == 1) return all[0];
            return options.ChooseRecipe?.Invoke(all) ?? all.OrderBy(r => r.Level).ThenBy(r => r.RecipeId).First();
        }

        void DecideAcquire(PlanLine line)
        {
            line.Recipe = null;
            var src = catalog.Sources(line.ItemId);
            var vendorOk = src.Has(Obtain.Vendor) && (options.VendorMaxPrice == 0 || src.VendorPrice <= options.VendorMaxPrice);
            line.VendorUnitPrice = src.VendorPrice;
            line.ManualReason = null;

            if (vendorOk && options.PreferVendor) line.Supply = Supply.Vendor;
            else if (src.Has(Obtain.Gather)) line.Supply = Supply.Gather;
            else if (src.Has(Obtain.GatherTimed)) line.Supply = Supply.GatherTimed;
            else if (src.Has(Obtain.Fish)) line.Supply = Supply.Fish;
            else if (vendorOk) line.Supply = Supply.Vendor;
            else if (src.Has(Obtain.Drop)) line.Supply = Supply.Drop;
            else
            {
                line.Supply = Supply.Manual;
                line.ManualReason = src.Has(Obtain.Vendor)
                    ? $"Vendor price {src.VendorPrice:N0} gil is above your cap of {options.VendorMaxPrice:N0}"
                    : "No gathering, fishing, vendor or monster source";
            }
        }

        PlanTreeNode BuildTree(uint itemId, int quantity, int depth)
        {
            var line = lines[itemId];
            var node = new PlanTreeNode { ItemId = itemId, Quantity = quantity, Supply = line.Supply };
            if (line.Supply != Supply.Craft || line.Recipe == null || line.Crafts == 0 || depth >= MaxTreeDepth) return node;
            var crafts = (quantity + line.Recipe.Yield - 1) / line.Recipe.Yield;
            foreach (var ing in line.Recipe.Ingredients)
                node.Children.Add(BuildTree(ing.ItemId, crafts * ing.Amount, depth + 1));
            return node;
        }
    }

    private enum VisitState { Open, Done }
}
