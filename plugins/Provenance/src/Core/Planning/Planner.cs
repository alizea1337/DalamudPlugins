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
        var learn = new Dictionary<uint, List<uint>>(); // book or tome item -> items that need it
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
            // Order items are made or gathered on top of what's already owned ("2000 more"), unless the
            // user asks to count them. Materials always use what's owned first.
            var ignoreStock = line.IsTarget && !options.CountOwnedTargets;
            var stock = ignoreStock ? 0 : owned.GetValueOrDefault(line.ItemId);
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

        // Books and tomes to read first, but only for what is still made or gathered.
        foreach (var (book, users) in learn)
        {
            var still = users.Where(u => lines.TryGetValue(u, out var l) && l.Remaining > 0).Distinct().ToList();
            if (still.Count == 0) continue;
            var have = Math.Min(1, Math.Max(0, owned.GetValueOrDefault(book)));
            var purchase = have > 0 ? null : options.BuyBook?.Invoke(book);
            lines[book] = new PlanLine
            {
                ItemId = book, Supply = Supply.Learn, Needed = 1, FromStock = have, NeededFor = still, Purchase = purchase,
                ManualReason = have > 0 ? null : purchase?.Text ?? "Not learned yet and not in your bags",
            };
        }
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

            var (recipe, blocked) = PickRecipe(itemId, recipeId);
            if (recipe != null)
            {
                line.Supply = Supply.Craft;
                line.Recipe = recipe;
                if (options.MissingBook?.Invoke(recipe) is > 0 and var book) NeedToLearn(book, itemId);
            }
            else
            {
                // No recipe, or none the character can use yet: buy or gather it instead if possible.
                DecideAcquire(line);
                if (blocked != null && line.Supply == Supply.Manual) line.ManualReason = blocked;
            }
            return line;
        }

        (RecipeInfo? Recipe, string? Blocked) PickRecipe(uint itemId, uint recipeId)
        {
            var all = catalog.RecipesFor(itemId);
            if (all.Count == 0) return (null, null);
            var usable = options.RecipeBlocker == null ? all : all.Where(r => options.RecipeBlocker(r) == null).ToList();
            if (usable.Count == 0)
            {
                var easiest = all.OrderBy(r => r.Level).ThenBy(r => r.RecipeId).First();
                return (null, options.RecipeBlocker!(easiest));
            }
            if (recipeId != 0)
            {
                var exact = usable.FirstOrDefault(r => r.RecipeId == recipeId);
                if (exact != null) return (exact, null);
            }
            // Prefer recipes that need no book the character still has to learn.
            var ready = options.MissingBook == null ? usable : usable.Where(r => options.MissingBook(r) == 0).ToList();
            var pool = ready.Count > 0 ? ready : usable;
            if (pool.Count == 1) return (pool[0], null);
            return (options.ChooseRecipe?.Invoke(pool) ?? pool.OrderBy(r => r.Level).ThenBy(r => r.RecipeId).First(), null);
        }

        void NeedToLearn(uint book, uint forItem)
        {
            if (!learn.TryGetValue(book, out var users)) learn[book] = users = new List<uint>();
            users.Add(forItem);
        }

        void DecideAcquire(PlanLine line)
        {
            line.Recipe = null;
            var src = catalog.Sources(line.ItemId);
            var vendorOk = src.Has(Obtain.Vendor) && (options.VendorMaxPrice == 0 || src.VendorPrice <= options.VendorMaxPrice);
            line.VendorUnitPrice = src.VendorPrice;
            line.ManualReason = null;
            var tomeOk = src.Tome == 0 || options.HasTome == null || options.HasTome(src.Tome);

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
                    : (src.Places & ~Place.OpenWorld) != Place.None
                        ? ElsewhereReason(src)
                        : "No gathering, fishing, vendor or monster source";
            }
            // Legendary nodes and some fishing holes only show up once their folklore tome is read.
            if (!tomeOk && line.Supply is Supply.GatherTimed or Supply.Fish) NeedToLearn(src.Tome, line.ItemId);
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

    /// <summary>"Only gathered in the Diadem, which a run can't go to."</summary>
    internal static string ElsewhereReason(ItemSources src)
    {
        var ways = new List<string>(3);
        if (src.Has(Obtain.GatherElsewhere)) ways.Add("gathered");
        if (src.Has(Obtain.FishElsewhere)) ways.Add("fished");
        if (src.Has(Obtain.CraftElsewhere)) ways.Add("crafted");
        var how = string.Join(" or ", ways);
        // Quest nodes and content that's gone only count when nothing else is left.
        var where = src.Places & ~Place.OpenWorld;
        if (where != Place.Other) where &= ~Place.Other;
        if (where == Place.Other)
            return ways.Count == 0
                ? "Only from quests or content that's no longer in the game"
                : $"Only {how} during quests or in content that's no longer in the game";
        return ways.Count == 0
            ? $"Only from {Places.Describe(where)}, which a run can't go to"
            : $"Only {how} in {Places.Describe(where)}, which a run can't go to";
    }

    private enum VisitState { Open, Done }
}
