using System.Collections.Generic;
using System.Linq;
using Provenance.Core.Orders;
using Provenance.Core.Planning;
using Xunit;
using static Provenance.Tests.FakeCatalog;

namespace Provenance.Tests;

public class PlannerTests
{
    private static readonly FakeCatalog Catalog = new();
    private static readonly Dictionary<uint, int> Nothing = new();

    private static Plan Build(Order order, Dictionary<uint, int>? owned = null, PlannerOptions? options = null)
        => Planner.Build(order, Catalog, owned ?? Nothing, options ?? new PlannerOptions());

    private static Order OrderOf(params (uint Id, int Qty)[] lines)
    {
        var o = new Order();
        foreach (var (id, qty) in lines) o.Add(id, qty);
        return o;
    }

    [Fact]
    public void Expands_the_full_tree_with_yields()
    {
        // 2 bows: 4 lumber (12 logs, 4 wind), 2 rivets -> 1 craft yields 3 (1 ingot -> 2 ore, 1 fire; +1 fire)
        var plan = Build(OrderOf((Bow, 2)));

        Assert.Equal(2, plan[Bow]!.Crafts);
        Assert.Equal(4, plan[Lumber]!.Crafts);
        Assert.Equal(12, plan[Log]!.Needed);
        Assert.Equal(1, plan[Rivets]!.Crafts);
        Assert.Equal(3, plan[Rivets]!.Produced);
        Assert.Equal(1, plan[Ingot]!.Crafts);
        Assert.Equal(2, plan[Ore]!.Needed);
        Assert.Equal(2, plan[FireShard]!.Needed);
        Assert.Equal(4 + 2, plan[WindShard]!.Needed);
        Assert.True(plan.CanStart);
    }

    [Fact]
    public void Shared_intermediates_are_summed_before_rounding_up()
    {
        // bow needs 1 rivet, shield needs 2: 3 rivets is exactly one craft, not two.
        var plan = Build(OrderOf((Bow, 1), (Shield, 1)));
        Assert.Equal(3, plan[Rivets]!.Needed);
        Assert.Equal(1, plan[Rivets]!.Crafts);
    }

    [Fact]
    public void Finds_the_ordered_items_that_need_a_material()
    {
        var plan = Build(OrderOf((Bow, 1), (Shield, 1), (Ore, 5)));
        Assert.Equal(new[] { Bow, Shield }, plan.TargetsNeeding(Rivets));   // both use rivets
        Assert.Equal(new[] { Bow, Shield, Ore }, plan.TargetsNeeding(Ore)); // ore deep down, and ordered itself
        Assert.Equal(new[] { Shield }, plan.TargetsNeeding(Shield));
        Assert.Empty(plan.TargetsNeeding(Log + 999));
    }

    [Fact]
    public void Craft_steps_put_ingredients_before_products()
    {
        var plan = Build(OrderOf((Bow, 1)));
        var order = plan.CraftSteps.Select(l => l.ItemId).ToList();
        Assert.True(order.IndexOf(Ingot) < order.IndexOf(Rivets));
        Assert.True(order.IndexOf(Rivets) < order.IndexOf(Bow));
        Assert.True(order.IndexOf(Lumber) < order.IndexOf(Bow));
    }

    [Fact]
    public void Owned_intermediates_cut_the_tree_below_them()
    {
        var plan = Build(OrderOf((Bow, 1)), new Dictionary<uint, int> { [Lumber] = 2, [Rivets] = 5 });
        Assert.Equal(0, plan[Lumber]!.Crafts);
        Assert.Equal(0, plan[Rivets]!.Crafts);
        Assert.Null(plan[Log]);
        Assert.Null(plan[Ingot]);
        Assert.Single(plan.CraftSteps);
    }

    [Fact]
    public void Owned_finished_items_are_ignored_unless_asked()
    {
        var owned = new Dictionary<uint, int> { [Bow] = 1 };
        Assert.Equal(1, Build(OrderOf((Bow, 1)), owned)[Bow]!.Crafts);
        Assert.Equal(0, Build(OrderOf((Bow, 1)), owned, new PlannerOptions { CountOwnedTargets = true })[Bow]!.Crafts);
    }

    [Fact]
    public void Timed_only_materials_are_marked_timed()
    {
        var plan = Build(OrderOf((RareLumber, 1)));
        Assert.Equal(Supply.GatherTimed, plan[TimedLog]!.Supply);
        Assert.Equal(1, plan.Count(Supply.GatherTimed));
    }

    [Fact]
    public void Vendor_over_the_cap_becomes_manual_and_blocks_start()
    {
        var order = OrderOf((FancyDye, 2));
        var capped = Build(order, options: new PlannerOptions { VendorMaxPrice = 1000 });
        Assert.Equal(Supply.Manual, capped[FancyDye]!.Supply);
        Assert.False(capped.CanStart);
        Assert.Contains("above your cap", capped.Missing.Single().ManualReason);

        var uncapped = Build(order, options: new PlannerOptions { VendorMaxPrice = 0 });
        Assert.Equal(Supply.Vendor, uncapped[FancyDye]!.Supply);
        Assert.Equal(10000, uncapped.VendorGil);
        Assert.True(uncapped.CanStart);
    }

    [Fact]
    public void Prefer_vendor_switches_fish_to_buying()
    {
        Assert.Equal(Supply.Vendor, Build(OrderOf((Fish, 1)), options: new PlannerOptions { PreferVendor = true })[Fish]!.Supply);
        Assert.Equal(Supply.Fish, Build(OrderOf((Fish, 1)), options: new PlannerOptions { PreferVendor = false })[Fish]!.Supply);
    }

    [Fact]
    public void Items_without_a_source_block_until_owned()
    {
        Assert.False(Build(OrderOf((Mystery, 1))).CanStart);
        var owned = Build(OrderOf((Mystery, 1)), options: new PlannerOptions { CountOwnedTargets = true }, owned: new Dictionary<uint, int> { [Mystery] = 1 });
        Assert.True(owned.CanStart);
    }

    [Fact]
    public void Drops_are_a_source()
    {
        Assert.Equal(Supply.Drop, Build(OrderOf((Fang, 3)))[Fang]!.Supply);
    }

    [Fact]
    public void Tree_shows_branch_quantities()
    {
        var plan = Build(OrderOf((Bow, 2)));
        var root = Assert.Single(plan.Tree);
        Assert.Equal(Bow, root.ItemId);
        var lumber = root.Children.Single(c => c.ItemId == Lumber);
        Assert.Equal(4, lumber.Quantity);
        Assert.Equal(12, lumber.Children.Single(c => c.ItemId == Log).Quantity);
    }

    [Fact]
    public void Explicit_recipe_id_is_respected()
    {
        var catalog = new FakeCatalog();
        var alt = catalog.AddRecipe(Lumber, Provenance.Core.Catalog.Job.Weaver, 90, 1, (Ore, 1));
        var order = new Order();
        order.Add(Lumber, 1, alt.RecipeId);
        var plan = Planner.Build(order, catalog, Nothing, new PlannerOptions());
        Assert.Equal(alt.RecipeId, plan[Lumber]!.Recipe!.RecipeId);
        Assert.Null(plan[Log]);
    }

    [Fact]
    public void Empty_order_gives_empty_plan()
    {
        var plan = Build(new Order());
        Assert.False(plan.CanStart);
        Assert.Empty(plan.Lines);
    }
}
