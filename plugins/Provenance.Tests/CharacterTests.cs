using System.Collections.Generic;
using Provenance.Core.Catalog;
using Provenance.Core.Character;
using Provenance.Core.Orders;
using Provenance.Core.Planning;
using Xunit;
using static Provenance.Tests.FakeCatalog;

namespace Provenance.Tests;

public class CharacterTests
{
    private static readonly Dictionary<uint, int> Nothing = new();

    private static Order OrderOf(uint id, int qty)
    {
        var o = new Order();
        o.Add(id, qty);
        return o;
    }

    [Fact]
    public void Blocked_recipe_becomes_manual_with_the_reason()
    {
        var plan = Planner.Build(OrderOf(Bow, 1), new FakeCatalog(), Nothing, new PlannerOptions
        {
            RecipeBlocker = r => r.ItemId == Bow ? "Needs Carpenter level 5; yours is 1" : null,
        });
        Assert.Equal(Supply.Manual, plan[Bow]!.Supply);
        Assert.Equal("Needs Carpenter level 5; yours is 1", plan[Bow]!.ManualReason);
        Assert.False(plan.CanStart);
        Assert.Null(plan[Lumber]);
    }

    [Fact]
    public void A_blocked_recipe_falls_back_to_another_usable_one()
    {
        var catalog = new FakeCatalog();
        var easy = catalog.AddRecipe(Lumber, Job.Weaver, 1, 1, (Ore, 1));
        var plan = Planner.Build(OrderOf(Lumber, 1), catalog, Nothing, new PlannerOptions
        {
            RecipeBlocker = r => r.Job == Job.Carpenter ? "Needs the master book" : null,
        });
        Assert.Equal(easy.RecipeId, plan[Lumber]!.Recipe!.RecipeId);
    }

    [Fact]
    public void Blocked_intermediate_sold_by_a_vendor_is_bought()
    {
        var catalog = new FakeCatalog();
        catalog.SetSources(Rivets, new ItemSources(Obtain.Vendor, 10));
        var plan = Planner.Build(OrderOf(Bow, 1), catalog, Nothing, new PlannerOptions
        {
            RecipeBlocker = r => r.ItemId == Rivets ? "Needs Blacksmith" : null,
        });
        Assert.Equal(Supply.Vendor, plan[Rivets]!.Supply);
        Assert.True(plan.CanStart);
    }

    [Fact]
    public void Missing_folklore_tome_blocks_legendary_only_items()
    {
        var catalog = new FakeCatalog();
        catalog.SetSources(TimedLog, new ItemSources(Obtain.GatherTimed, 0, Tome: 7));
        var without = Planner.Build(OrderOf(RareLumber, 1), catalog, Nothing, new PlannerOptions { HasTome = _ => false });
        Assert.Equal(Supply.Manual, without[TimedLog]!.Supply);
        Assert.Contains("folklore tome", without[TimedLog]!.ManualReason);

        var with = Planner.Build(OrderOf(RareLumber, 1), catalog, Nothing, new PlannerOptions { HasTome = t => t == 7 });
        Assert.Equal(Supply.GatherTimed, with[TimedLog]!.Supply);
    }

    [Fact]
    public void Food_bonus_is_a_capped_percentage_with_better_hq_values()
    {
        // All i Pebre: CP +21% (max 80), HQ +26% (max 100); Control +4% (max 92), HQ +5% (max 115)
        var food = new ConsumableInfo(46253, "All i Pebre", 0, ConsumableKind.Food, Discipline.Hand, 750, true, new[]
        {
            new ConsumableParam(2, true, 21, 80, 26, 100),
            new ConsumableParam(1, true, 4, 92, 5, 115),
        });
        var stats = new[] { 5844, 5182, 572 };
        Assert.Equal(new[] { 0, 92, 80 }, food.Bonus(stats, hq: false));
        Assert.Equal(new[] { 0, 115, 100 }, food.Bonus(stats, hq: true));
        // Low stats stay under the cap: 5% of 1000 Control = 50
        Assert.Equal(50, food.Bonus(new[] { 0, 1000, 0 }, hq: true)[1]);
    }

    [Fact]
    public void Base_stats_are_live_minus_gear_and_add_back_onto_other_gearsets()
    {
        var baseStats = StatMath.BaseFrom(new[] { 4200, 4000, 700 }, new[] { 4200, 4000, 520 });
        Assert.Equal(new[] { 0, 0, 180 }, baseStats);

        var (stats, source) = StatMath.Combine(new[] { 3000, 3100, 500 }, baseStats);
        Assert.Equal(new[] { 3000, 3100, 680 }, stats);
        Assert.Equal(StatSource.GearAndBase, source);

        var (gearOnly, unknown) = StatMath.Combine(new[] { 3000, 3100, 500 }, null);
        Assert.Equal(500, gearOnly[2]);
        Assert.Equal(StatSource.GearOnly, unknown);
    }
}
