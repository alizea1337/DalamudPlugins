using System.Collections.Generic;
using System.Linq;
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
    public void Missing_folklore_tome_becomes_a_learn_first_step()
    {
        var catalog = new FakeCatalog();
        catalog.SetSources(TimedLog, new ItemSources(Obtain.GatherTimed, 0, Tome: 7));
        var without = Planner.Build(OrderOf(RareLumber, 1), catalog, Nothing, new PlannerOptions { HasTome = _ => false });
        Assert.Equal(Supply.GatherTimed, without[TimedLog]!.Supply);
        var tome = Assert.Single(without.Learn);
        Assert.Equal(7u, tome.ItemId);
        Assert.Equal(new[] { TimedLog }, tome.NeededFor);
        Assert.Contains(tome, without.Missing);           // not in the bags: get it first
        Assert.False(without.CanStart);

        var inBags = Planner.Build(OrderOf(RareLumber, 1), catalog, new Dictionary<uint, int> { [7] = 1 }, new PlannerOptions { HasTome = _ => false });
        Assert.Equal(0, inBags[7]!.Remaining);            // the run reads it first
        Assert.True(inBags.CanStart);

        var read = Planner.Build(OrderOf(RareLumber, 1), catalog, Nothing, new PlannerOptions { HasTome = _ => true });
        Assert.Empty(read.Learn);
    }

    [Fact]
    public void Missing_master_book_keeps_the_recipe_and_adds_the_book_first()
    {
        const uint Book = 9001;
        var catalog = new FakeCatalog();
        var plan = Planner.Build(OrderOf(Bow, 1), catalog, Nothing, new PlannerOptions
        {
            MissingBook = r => r.ItemId == Bow ? Book : 0,
        });
        Assert.Equal(Supply.Craft, plan[Bow]!.Supply);
        Assert.Equal(1, plan[Bow]!.Crafts);
        var book = Assert.Single(plan.Learn);
        Assert.Equal(Book, book.ItemId);
        Assert.Equal(new[] { Bow }, book.NeededFor);
        Assert.False(plan.CanStart);

        var owned = Planner.Build(OrderOf(Bow, 1), catalog, new Dictionary<uint, int> { [Book] = 1 }, new PlannerOptions { MissingBook = r => r.ItemId == Bow ? Book : 0 });
        Assert.True(owned.CanStart);
    }

    [Fact]
    public void A_book_the_run_can_buy_does_not_block_the_start()
    {
        const uint Book = 9001;
        var catalog = new FakeCatalog();
        PlanLine Build(BookPurchase? purchase, out Plan plan)
        {
            plan = Planner.Build(OrderOf(Bow, 1), catalog, Nothing, new PlannerOptions
            {
                MissingBook = r => r.ItemId == Bow ? Book : 0,
                BuyBook = id => id == Book ? purchase : null,
            });
            return plan[Book]!;
        }

        var affordable = Build(new BookPurchase(true, "300 Purple Crafters' Scrips"), out var buyPlan);
        Assert.True(affordable.WillBuy);
        Assert.True(buyPlan.CanStart);
        Assert.Single(buyPlan.Learn);

        var tooPoor = Build(new BookPurchase(false, "Costs 300 Purple Crafters' Scrips; you have 120"), out var poorPlan);
        Assert.False(tooPoor.WillBuy);
        Assert.False(poorPlan.CanStart);
        Assert.Equal("Costs 300 Purple Crafters' Scrips; you have 120", tooPoor.ManualReason);

        var nowhere = Build(null, out var nowherePlan);
        Assert.False(nowhere.WillBuy);
        Assert.False(nowherePlan.CanStart);
    }

    [Fact]
    public void A_book_on_the_order_is_learned_not_made()
    {
        const uint Book = 9001, Tome = 9002, Learned = 9003;
        var order = new Order();
        order.Add(Book, 1);
        order.Add(Tome, 2);       // more than one copy still means read it once
        order.Add(Learned, 1);    // already learned: nothing to do
        order.Add(Bow, 1);
        var plan = Planner.Build(order, new FakeCatalog(), new Dictionary<uint, int> { [Tome] = 1 }, new PlannerOptions
        {
            BookLearned = id => id switch { Book or Tome => false, Learned => true, _ => null },
            BuyBook = id => id == Book ? new BookPurchase(true, "300 Purple Crafters' Scrips") : null,
        });

        Assert.Equal(new[] { Book, Tome }, plan.Learn.Select(l => l.ItemId).OrderBy(i => i));
        var book = plan[Book]!;
        Assert.True(book.IsTarget);
        Assert.Empty(book.NeededFor);
        Assert.Equal(1, book.Needed);
        Assert.True(book.WillBuy);
        var tome = plan[Tome]!;
        Assert.Equal(0, tome.Remaining); // in the bags: just read it
        Assert.Null(plan[Learned]);
        // The book is never a craft or gather line, and the rest of the order plans as usual.
        Assert.Equal(Supply.Craft, plan[Bow]!.Supply);
        Assert.Contains(plan.Tree, n => n.ItemId == Book && n.Supply == Supply.Learn);
        Assert.DoesNotContain(plan.Tree, n => n.ItemId == Learned);
        Assert.True(plan.CanStart);
    }

    [Fact]
    public void An_order_of_learned_books_only_plans_nothing()
    {
        var order = new Order();
        order.Add(9003, 1);
        var plan = Planner.Build(order, new FakeCatalog(), Nothing, new PlannerOptions { BookLearned = _ => true });
        Assert.Empty(plan.Lines);
    }

    [Fact]
    public void A_recipe_without_a_missing_book_is_preferred()
    {
        var catalog = new FakeCatalog();
        var other = catalog.AddRecipe(Lumber, Job.Weaver, 1, 1, (Ore, 1));
        var plan = Planner.Build(OrderOf(Lumber, 1), catalog, Nothing, new PlannerOptions
        {
            MissingBook = r => r.Job == Job.Carpenter ? 9001u : 0u,
        });
        Assert.Equal(other.RecipeId, plan[Lumber]!.Recipe!.RecipeId);
        Assert.Empty(plan.Learn);
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
