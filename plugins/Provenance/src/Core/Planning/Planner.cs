using System;
using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Catalog;
using Provenance.Core.Orders;

namespace Provenance.Core.Planning;

/// <summary>
/// Expands an order into every material it needs, decides how each one is obtained and in which
/// order the recipes run. Fish lines bring the bait they are cast with (<see cref="PlannerOptions.FishBaits"/>):
/// bought from a gil vendor before fishing, or a stand-in for bait no vendor sells under the cap, never crafted.
/// A bait the order crafts anyway is cast from the bags, then bought by the fisher when a vendor sells it, or
/// stood in for when none does.
/// Pure: game data comes through <see cref="IGameCatalog"/>, stock as a dictionary.
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

        // Bait: what each fish is cast with, the fish each bait is for (in the order they were met), and the
        // stand-in for bait no vendor sells under the cap (only when the stand-in itself can be bought).
        var fishBaits = new Dictionary<uint, IReadOnlyList<(uint Bait, double PerCatch)>>();
        var fishMet = new List<uint>();
        var baitUsers = new Dictionary<uint, List<uint>>();
        var baitLines = new HashSet<uint>();
        var fallback = options.FallbackBait != 0 && VendorOk(catalog.Sources(options.FallbackBait)) && catalog.RecipesFor(options.FallbackBait).Count == 0
            ? options.FallbackBait
            : 0u;

        // 1. Decide how every reachable item is supplied, and record a post-order (ingredients first).
        foreach (var t in targets)
        {
            var line = GetOrAddLine(t.ItemId, isTarget: true, t.RecipeId);
            line.IsTarget = true;
        }
        foreach (var t in targets) Visit(t.ItemId);

        // 2. Push demand from products down to ingredients and from fish to their bait (reverse post-order is
        // a topological order). The stand-in bait goes last, once every bait it may stand in for is settled.
        var demand = new Dictionary<uint, int>();
        foreach (var t in targets) demand[t.ItemId] = demand.GetValueOrDefault(t.ItemId) + t.Quantity;
        var baitDemand = new Dictionary<uint, double>(); // bait the fish still to catch use up, by bait
        var spilled = new List<uint>();                  // fish whose bait the stand-in takes over (in part)
        var handedOver = 0;                              // bait the stand-in takes over

        for (var i = postOrder.Count - 1; i >= 0; i--)
        {
            if (postOrder[i] == fallback) continue;
            Settle(lines[postOrder[i]]);
        }
        if (fallback != 0 && lines.TryGetValue(fallback, out var standIn)) Settle(standIn);

        var craftSteps = postOrder.Select(id => lines[id]).Where(l => l.Supply == Supply.Craft && l.Crafts > 0).ToList();
        var tree = targets.Select(t => BuildTree(t.ItemId, t.Quantity, 0)).ToList();

        // Materials below an intermediate that stock fully covers are not needed at all (bait still missing is).
        foreach (var id in lines.Where(kv => kv.Value.Needed == 0 && !kv.Value.IsTarget && kv.Value.MissingBait == 0).Select(kv => kv.Key).ToList())
            lines.Remove(id);

        // Bait says which fish it is for (only fish still to catch), in the order the fish were met.
        foreach (var (bait, users) in baitUsers)
            if (lines.TryGetValue(bait, out var baitLine)) baitLine.NeededFor = users.Where(StillFished).ToList();
        if (fallback != 0 && lines.TryGetValue(fallback, out var stand))
        {
            var direct = baitUsers.GetValueOrDefault(fallback);
            stand.NeededFor = fishMet.Where(f => (direct?.Contains(f) == true || spilled.Contains(f)) && StillFished(f)).ToList();
        }

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
            else if (line.Supply == Supply.Fish && options.FishBaits?.Invoke(itemId) is { Count: > 0 } baits)
            {
                // A fish brings the bait it is cast with: its tackle and its predators' bait (never another fish
                // the order catches, which has its own line).
                var used = baits.Where(b => b.Bait != 0 && b.Bait != itemId && b.PerCatch > 0 && !double.IsNaN(b.PerCatch)
                                            && !(lines.TryGetValue(b.Bait, out var known) && known.Supply == Supply.Fish)).ToList();
                if (used.Count > 0)
                {
                    fishBaits[itemId] = used;
                    fishMet.Add(itemId);
                }
                foreach (var (bait, _) in used)
                {
                    AddBait(bait, itemId);
                    Visit(bait);
                }
            }
            state[itemId] = VisitState.Done;
            postOrder.Add(itemId);
        }

        // How much of an item the order needs, what the stock covers, and (for recipes) the demand on the
        // ingredients; fish add the bait their catches use up.
        void Settle(PlanLine line)
        {
            var id = line.ItemId;
            var other = demand.GetValueOrDefault(id); // as an order item or an ingredient
            var isBait = baitLines.Contains(id);
            // Order items are made or gathered on top of what's already owned ("2000 more"), unless the
            // user asks to count them. Materials always use what's owned first. Bait only counts what the
            // run can cast with.
            var ignoreStock = line.IsTarget && !options.CountOwnedTargets;
            var stock = ignoreStock ? 0
                : Math.Max(0, isBait && options.BaitOnHand != null ? options.BaitOnHand(id) : owned.GetValueOrDefault(id));
            var needed = other;
            if (isBait)
            {
                var casts = (int)Math.Ceiling(baitDemand.GetValueOrDefault(id) - 1e-9);
                var takenOver = id == fallback ? handedOver : 0;
                if (casts > 0 && id != fallback && CantBuyBait(line))
                {
                    // Bait the run can't get before fishing: no vendor sells it under the cap, and crafting it (for the
                    // order or a recipe) comes after the fishing. What the bags hold is cast first (an ordered one is
                    // crafted on top of it), the stand-in does the rest; without one the player brings it.
                    var spareInBags = line.Supply == Supply.Craft && ignoreStock
                        ? Math.Max(0, options.BaitOnHand?.Invoke(id) ?? owned.GetValueOrDefault(id))
                        : Math.Max(0, stock - other);
                    var covered = Math.Min(spareInBags, casts);
                    if (covered < casts && fallback != 0)
                    {
                        handedOver += casts - covered;
                        foreach (var fish in baitUsers[id].Where(StillFished))
                            if (!spilled.Contains(fish)) spilled.Add(fish);
                        casts = covered;
                    }
                    if (line.Supply == Supply.Craft)
                    {
                        // The crafted ones come too late to cast: the line keeps only what's crafted.
                        line.MissingBait = casts - covered;
                        if (line.MissingBait > 0)
                        {
                            var src = catalog.Sources(id);
                            line.ManualReason = $"Crafted ({line.Recipe?.Job.Abbreviation()}) only after the fishing, so the run can't cast the ones it makes"
                                                + (src.Has(Obtain.Vendor) ? $", and its vendor price {src.VendorPrice:N0} gil is above your cap of {options.VendorMaxPrice:N0}" : "");
                        }
                        casts = 0;
                    }
                }
                // Crafted for the order, and a gil vendor sells it too: the fisher buys what it casts when it gets there
                // (crafting comes after the fishing), so the line keeps only what's crafted.
                else if (line.Supply == Supply.Craft) casts = 0;
                // Spare only for bait the run buys; bait it can't buy only needs the estimate.
                else if (casts + takenOver > 0 && line.Supply == Supply.Vendor) casts += Math.Max(0, options.SpareBait);
                needed += casts + takenOver;
            }
            line.Needed = needed;
            line.FromStock = Math.Min(stock, line.Needed);

            if (line.Supply == Supply.Fish && line.Remaining > 0 && fishBaits.TryGetValue(id, out var baits))
                foreach (var (bait, perCatch) in baits)
                    baitDemand[bait] = baitDemand.GetValueOrDefault(bait) + line.Remaining * perCatch;

            if (line.Supply != Supply.Craft || line.Recipe == null) return;
            line.Crafts = (line.Remaining + line.Recipe.Yield - 1) / line.Recipe.Yield;
            foreach (var ing in line.Recipe.Ingredients)
                demand[ing.ItemId] = demand.GetValueOrDefault(ing.ItemId) + line.Crafts * ing.Amount;
        }

        bool StillFished(uint fish) => lines.TryGetValue(fish, out var l) && l.Supply == Supply.Fish && l.Remaining > 0;

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

        // A bait line for a fish. Bait is bought before fishing (crafting comes after it), so a new bait line
        // never picks a recipe; an item the order already has (ordered, or a recipe's ingredient) keeps its way,
        // and when that's crafting, its casts come from the bags, then the fisher's purchase when a gil vendor sells
        // it, else the stand-in.
        void AddBait(uint bait, uint fish)
        {
            baitLines.Add(bait);
            if (!baitUsers.TryGetValue(bait, out var users)) baitUsers[bait] = users = new List<uint>();
            if (!users.Contains(fish)) users.Add(fish);
            if (!lines.ContainsKey(bait))
            {
                var line = new PlanLine { ItemId = bait };
                lines[bait] = line;
                DecideBait(line);
            }
            // Bait no vendor sells under the cap (crafted after the fishing or not): the stand-in may have to cover it.
            if (fallback == 0 || bait == fallback || !CantBuyBait(lines[bait])) return;
            baitLines.Add(fallback);
            if (lines.ContainsKey(fallback)) return;
            var stand = new PlanLine { ItemId = fallback };
            lines[fallback] = stand;
            DecideBait(stand);
        }

        void DecideBait(PlanLine line)
        {
            line.Recipe = null;
            var src = catalog.Sources(line.ItemId);
            line.VendorUnitPrice = src.VendorPrice;
            if (VendorOk(src))
            {
                line.Supply = Supply.Vendor;
                line.ManualReason = null;
                return;
            }
            line.Supply = Supply.Manual;
            line.ManualReason = BaitReason(line.ItemId, src);
        }

        // Why the run can't get a bait itself: the price cap, where it is sold instead, or that it is crafted.
        string BaitReason(uint bait, ItemSources src)
        {
            if (src.Has(Obtain.Vendor))
                return $"Vendor price {src.VendorPrice:N0} gil is above your cap of {options.VendorMaxPrice:N0}";
            if (options.BaitSource?.Invoke(bait) is { Length: > 0 } where) return where;
            var recipes = catalog.RecipesFor(bait);
            if (recipes.Count > 0) return $"Crafted ({recipes[0].Job.Abbreviation()}) and not sold for gil; a run buys bait but doesn't craft it";
            return "Not sold by any gil vendor a run can reach";
        }

        bool VendorOk(ItemSources src) => VendorSells(src, options.VendorMaxPrice);

        // Bait the run can't buy for gil: none of its own sold under the cap, whether crafted for the order or not.
        // A crafted one a vendor sells is bought by the fisher when it fishes (see BuysBait).
        bool CantBuyBait(PlanLine line)
            => line.Supply == Supply.Manual || line.Supply == Supply.Craft && !VendorOk(catalog.Sources(line.ItemId));

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
            var vendorOk = VendorOk(src);
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
            if (line.Supply == Supply.Fish && depth < MaxTreeDepth && fishBaits.TryGetValue(itemId, out var baits))
            {
                // The bait this branch's catches use up (before anything owned is subtracted), and the stand-in
                // where it takes over.
                foreach (var (bait, perCatch) in baits)
                {
                    var amount = (int)Math.Ceiling(quantity * perCatch - 1e-9);
                    node.Children.Add(new PlanTreeNode { ItemId = bait, Quantity = amount, Supply = lines[bait].Supply });
                    if (fallback != 0 && bait != fallback && spilled.Contains(itemId) && CantBuyBait(lines[bait])
                        && node.Children.All(c => c.ItemId != fallback))
                        node.Children.Add(new PlanTreeNode { ItemId = fallback, Quantity = amount, Supply = lines[fallback].Supply });
                }
                return node;
            }
            if (line.Supply != Supply.Craft || line.Recipe == null || line.Crafts == 0 || depth >= MaxTreeDepth) return node;
            var crafts = (quantity + line.Recipe.Yield - 1) / line.Recipe.Yield;
            foreach (var ing in line.Recipe.Ingredients)
                node.Children.Add(BuildTree(ing.ItemId, crafts * ing.Amount, depth + 1));
            return node;
        }
    }

    /// <summary>A gil vendor sells the item at or under <paramref name="vendorMaxPrice"/> (0: no limit).</summary>
    public static bool VendorSells(ItemSources src, uint vendorMaxPrice)
        => src.Has(Obtain.Vendor) && (vendorMaxPrice == 0 || src.VendorPrice <= vendorMaxPrice);

    /// <summary>
    /// The run buys a bait itself, so it casts its own rather than a stand-in: the plan buys it from a gil vendor before
    /// the fishing, or it's crafted for the order and a gil vendor sells it under the cap, so the fisher buys what it
    /// casts when it gets there (crafting comes after the fishing).
    /// </summary>
    public static bool BuysBait(Plan plan, IGameCatalog catalog, uint bait, uint vendorMaxPrice)
        => plan[bait] switch
        {
            { Supply: Supply.Vendor } => true,
            { Supply: Supply.Craft } => VendorSells(catalog.Sources(bait), vendorMaxPrice),
            _ => false,
        };

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
